package profilestologsconnector

import (
	"context"
	"strconv"

	"go.opentelemetry.io/collector/component"
	"go.opentelemetry.io/collector/consumer"
	"go.opentelemetry.io/collector/pdata/pcommon"
	"go.opentelemetry.io/collector/pdata/plog"
	"go.opentelemetry.io/collector/pdata/pprofile"
	"go.uber.org/zap"
)

// profilesToLogs converts OTLP profiles into log records carrying folded stacks.
//
// It exists because Dynatrace does not ingest the OpenTelemetry profiles signal.
// The record layout deliberately mirrors the OTLP profiles data model so that when
// native ingest arrives, migrating is a transport change rather than a re-model.
type profilesToLogs struct {
	cfg      *Config
	next     consumer.Logs
	logger   *zap.Logger
	sessions *sessionStore
}

// groupKey is the record grain: one log record per unique (stack, thread) per
// batch.
//
// Stack alone is NOT sufficient. The spec defines Sample identity as
// {stack_index, attributes, link_index} and thread.id is a Sample attribute, so
// the same stack observed on five threads is five Samples, not one. Keying on the
// stack alone would force an arbitrary thread onto a merged record and destroy the
// join to EventPipe.
type groupKey struct {
	stackHash string
	threadID  int64
}

type groupValue struct {
	folded     string
	depth      int
	truncated  bool
	origDepth  int
	sampleSum  int64
	firstTsNano uint64
}

func (c *profilesToLogs) Capabilities() consumer.Capabilities {
	return consumer.Capabilities{MutatesData: false}
}

func (c *profilesToLogs) Start(_ context.Context, _ component.Host) error {
	if c.sessions != nil {
		c.sessions.start()
	}
	return nil
}

func (c *profilesToLogs) Shutdown(_ context.Context) error {
	if c.sessions != nil {
		c.sessions.shutdown()
	}
	return nil
}

func (c *profilesToLogs) ConsumeProfiles(ctx context.Context, pd pprofile.Profiles) error {
	dic := pd.Dictionary()
	out := plog.NewLogs()

	for _, rp := range pd.ResourceProfiles().All() {
		res := rp.Resource()
		service := stringAttr(res.Attributes(), "service.name")
		namespace := stringAttr(res.Attributes(), "k8s.namespace.name")

		var rl plog.ResourceLogs
		var rlInit bool

		for _, sp := range rp.ScopeProfiles().All() {
			for _, prof := range sp.Profiles().All() {
				tsNano := int64(prof.Time())

				// Gate before doing any folding work. Folding is the expensive part
				// of this connector, so checking the session first keeps the idle
				// cost of a gated deployment near zero.
				var sess Session
				if c.cfg.Gating.Enabled {
					var ok bool
					sess, ok = c.sessions.activeFor(service, namespace, tsNano)
					if !ok {
						continue
					}
				}

				groups := c.foldSamples(dic, prof)
				if len(groups) == 0 {
					continue
				}

				if !rlInit {
					rl = out.ResourceLogs().AppendEmpty()
					res.Attributes().CopyTo(rl.Resource().Attributes())
					for k, v := range c.cfg.ResourceMarker {
						rl.Resource().Attributes().PutStr(k, v)
					}
					rlInit = true
				}

				sl := rl.ScopeLogs().AppendEmpty()
				sl.Scope().SetName(scopeName)
				sl.Scope().SetVersion(scopeVersion)

				periodNanos := prof.Period()
				for key, g := range groups {
					c.emit(sl, key, g, prof, periodNanos, sess, tsNano)
				}
			}
		}
	}

	if out.LogRecordCount() == 0 {
		return nil
	}
	return c.next.ConsumeLogs(ctx, out)
}

// foldSamples collapses a profile's samples into (stack, thread) groups.
func (c *profilesToLogs) foldSamples(
	dic pprofile.ProfilesDictionary,
	prof pprofile.Profile,
) map[groupKey]*groupValue {
	stackTable := dic.StackTable()
	groups := make(map[groupKey]*groupValue)

	for _, sample := range prof.Samples().All() {
		si := int(sample.StackIndex())
		if si < 0 || si >= stackTable.Len() {
			continue
		}

		folded, depth := foldedStack(dic, stackTable.At(si))
		if folded == "" {
			continue
		}

		threadID := sampleThreadID(dic, sample)

		cut, truncated := truncateFolded(folded, c.cfg.MaxFoldedChars)
		// Hash the UNCUT stack: two records truncated to the same visible prefix are
		// still different stacks, and collapsing them in the flame graph would
		// overstate the survivor.
		key := groupKey{stackHash: stackHash(folded), threadID: threadID}

		g, ok := groups[key]
		if !ok {
			g = &groupValue{
				folded:    cut,
				depth:     depth,
				truncated: truncated,
				origDepth: depth,
			}
			if ts := sample.TimestampsUnixNano(); ts.Len() > 0 {
				g.firstTsNano = ts.At(0)
			}
			groups[key] = g
		}
		g.sampleSum += sampleCount(sample)
	}

	return groups
}

func (c *profilesToLogs) emit(
	sl plog.ScopeLogs,
	key groupKey,
	g *groupValue,
	prof pprofile.Profile,
	periodNanos int64,
	sess Session,
	profileTsNano int64,
) {
	rec := sl.LogRecords().AppendEmpty()

	ts := g.firstTsNano
	if ts == 0 {
		ts = uint64(profileTsNano)
	}
	rec.SetTimestamp(pcommon.Timestamp(ts))
	rec.SetObservedTimestamp(pcommon.Timestamp(ts))
	rec.SetSeverityNumber(plog.SeverityNumberInfo)
	rec.SetSeverityText("INFO")

	// The body stays a short human-readable marker. The stack lives in an attribute
	// so DQL can filter and group on it without regex-parsing prose on every query -
	// and query cost is billed on bytes scanned (#6).
	rec.Body().SetStr("profile sample")

	a := rec.Attributes()
	a.PutStr("log.source", "continuous_profiler")
	a.PutStr("profile.schema_version", c.cfg.SchemaVersion)
	a.PutStr("profile.stack.folded", g.folded)
	a.PutStr("profile.stack.hash", key.stackHash)
	a.PutInt("profile.stack.depth", int64(g.depth))
	a.PutInt("profile.sample_count", g.sampleSum)

	if g.truncated {
		// Explicit, because the platform's own truncation is silent (#27) and biases
		// flame graphs toward shallow paths while looking healthy.
		a.PutBool("profile.stack.truncated", true)
		a.PutInt("profile.stack.original_depth", int64(g.origDepth))
	}

	// cpu_ns is derived, not reported. The existing pipeline left this at literal
	// zero in 100% of records, which makes every duration-weighted query return
	// nothing while appearing to work.
	if periodNanos > 0 {
		a.PutInt("profile.cpu_ns", g.sampleSum*periodNanos)
	}
	if periodNanos > 0 {
		a.PutInt("profile.period_ns", periodNanos)
	}
	if d := prof.DurationNano(); d > 0 {
		a.PutInt("profile.window_duration_ns", int64(d))
	}
	a.PutInt("profile.window_start_ns", profileTsNano)

	if key.threadID >= 0 {
		// The join key to EventPipe. #8 verified this is the real Linux TID on both
		// sides, so no managed-to-OS translation is needed.
		a.PutInt("thread.id", key.threadID)
	}

	if sess.ID != "" {
		a.PutStr("profile.session_id", sess.ID)
	}

	// Per-sample trace/span are deliberately absent. link_index 0 is the model's own
	// defined null, and correlation here is thread-level by design - a wrong span id
	// would be worse than none.
}

// sampleThreadID pulls thread.id off a Sample's attributes, resolving through the
// dictionary's attribute table. Returns -1 when absent.
func sampleThreadID(dic pprofile.ProfilesDictionary, sample pprofile.Sample) int64 {
	attrTable := dic.AttributeTable()
	strTable := dic.StringTable()
	idx := sample.AttributeIndices()

	for i := 0; i < idx.Len(); i++ {
		ai := int(idx.At(i))
		if ai < 0 || ai >= attrTable.Len() {
			continue
		}
		kv := attrTable.At(ai)
		ki := int(kv.KeyStrindex())
		if ki < 0 || ki >= strTable.Len() {
			continue
		}
		if strTable.At(ki) != "thread.id" {
			continue
		}
		switch kv.Value().Type() {
		case pcommon.ValueTypeInt:
			return kv.Value().Int()
		case pcommon.ValueTypeStr:
			if n, err := strconv.ParseInt(kv.Value().Str(), 10, 64); err == nil {
				return n
			}
		}
	}
	return -1
}

// sampleCount reads the sample's value. For a CPU profile the first value is the
// sample count; summing all values would double-count profiles that carry several
// value types.
func sampleCount(sample pprofile.Sample) int64 {
	v := sample.Values()
	if v.Len() == 0 {
		return 1
	}
	n := v.At(0)
	if n <= 0 {
		return 1
	}
	return n
}

func stringAttr(m pcommon.Map, key string) string {
	if v, ok := m.Get(key); ok {
		return v.Str()
	}
	return ""
}
