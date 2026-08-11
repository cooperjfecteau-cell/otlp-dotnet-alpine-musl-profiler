using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace ProfileAgent;

internal sealed record FoldedSample(string Folded, string Hash, int Depth, bool Truncated, int OriginalDepth, int ThreadId)
{
    public long SampleCount { get; set; }
}

/// <summary>A completed garbage collection.</summary>
internal sealed record GcEvent(int Generation, string Reason, string Kind, long DurationNs, double TimestampMs);

/// <summary>
/// Blocking lock contention, aggregated by (stack, thread) — the same grain as CPU
/// samples, so contention renders on the same flame-graph machinery.
///
/// Aggregated rather than emitted per event on purpose. One session produced 7,266
/// individual contentions; as records that is mostly noise, and it overflowed the
/// exporter queue. What an investigator wants is "which call path waited, and for
/// how long in total", which is this.
/// </summary>
internal sealed record ContentionGroup(int ThreadId, string Folded, string Hash)
{
    public long Count { get; set; }
    public long TotalDurationNs { get; set; }
    public long MaxDurationNs { get; set; }
}

/// <summary>Allocation attributed to a type, aggregated over the window.</summary>
internal sealed record AllocationSummary(string TypeName, long Bytes, long Ticks);

internal sealed record ParsedTrace(
    IReadOnlyCollection<FoldedSample> CpuSamples,
    IReadOnlyCollection<GcEvent> GarbageCollections,
    IReadOnlyCollection<ContentionGroup> Contentions,
    IReadOnlyCollection<AllocationSummary> Allocations);

/// <summary>
/// Parses a nettrace into the four things only this half can provide.
///
/// CPU samples overlap with what eBPF already produces — kept deliberately, so the
/// two techniques can be compared on identical ground. GC, contention and
/// allocation are the reason the sidecar exists at all: eBPF sees a thread parked
/// and can tell you nothing about why.
/// </summary>
internal static class NettraceReader
{
    public static ParsedTrace Read(string nettracePath, ILoggerLike log)
    {
        // Writes an intermediate .etlx at roughly 9x the nettrace size (#8). Not an
        // in-memory path, which is why the work dir needs real space.
        var etlxPath = TraceLog.CreateFromEventPipeDataFile(nettracePath);

        try
        {
            using var traceLog = TraceLog.OpenOrConvert(etlxPath);
            var source = traceLog.Events.GetSource();

            var cpu = new Dictionary<(string Hash, int Tid), FoldedSample>();
            var gcs = new List<GcEvent>();
            var contentions = new Dictionary<(string Hash, int Tid), ContentionGroup>();
            var contentionEvents = 0;
            var allocations = new Dictionary<string, (long Bytes, long Ticks)>();

            var cpuTotal = 0;
            var withoutStack = 0;

            // GCStop does not carry a duration, so starts are paired with stops on
            // (process, count) and the duration is taken from the timestamps.
            var pendingGc = new Dictionary<(int Pid, int Count), GCStartTraceData>();

            source.Clr.GCStart += data =>
            {
                pendingGc[(data.ProcessID, data.Count)] = (GCStartTraceData)data.Clone();
            };

            source.Clr.GCStop += data =>
            {
                if (!pendingGc.Remove((data.ProcessID, data.Count), out var start)) return;
                var durationNs = (long)((data.TimeStampRelativeMSec - start.TimeStampRelativeMSec) * 1_000_000);
                gcs.Add(new GcEvent(
                    Generation: start.Depth,
                    Reason: start.Reason.ToString(),
                    Kind: start.Type.ToString(),
                    DurationNs: durationNs,
                    TimestampMs: start.TimeStampRelativeMSec));
            };

            // ContentionStop carries the DURATION but no call stack; ContentionStart
            // carries the stack that entered the wait. Pairing them by thread is the
            // only way to answer "which call path blocked, and for how long".
            //
            // Reading the stack off ContentionStop alone yields an empty string for
            // every event, which then collapses every record into a single null-hash
            // group. The durations still look correct, so the failure is invisible
            // until you try to render it.
            var pendingContention = new Dictionary<int, (string Folded, string Hash)>();

            source.Clr.ContentionStart += data =>
            {
                if (data.ContentionFlags != ContentionFlags.Managed) return;
                var (folded, _) = Folding.Fold(data.CallStack());
                if (folded.Length == 0) return;
                var (cut, _) = Folding.Truncate(folded);
                pendingContention[data.ThreadID] = (cut, Folding.Hash(folded));
            };

            source.Clr.ContentionStop += data =>
            {
                // Only blocking contention is interesting. A spin that resolved
                // without parking is not a lock problem, and including it would
                // drown the signal we are after.
                if (data.ContentionFlags != ContentionFlags.Managed) return;

                contentionEvents++;

                string cut;
                string hash;
                if (pendingContention.Remove(data.ThreadID, out var started))
                {
                    cut = started.Folded;
                    hash = started.Hash;
                }
                else
                {
                    // No paired start -- the wait began before the trace did, or the
                    // provider gave no stack. Keep the record rather than dropping
                    // it: the duration is still the finding, and a labelled bucket
                    // is honest where a silently missing one is not.
                    cut = "(contention, no stack captured)";
                    hash = Folding.Hash(cut);
                }

                var key = (hash, data.ThreadID);

                if (!contentions.TryGetValue(key, out var group))
                {
                    group = new ContentionGroup(data.ThreadID, cut, hash);
                    contentions[key] = group;
                }

                var durationNs = (long)data.DurationNs;
                group.Count++;
                group.TotalDurationNs += durationNs;
                if (durationNs > group.MaxDurationNs) group.MaxDurationNs = durationNs;
            };

            source.Clr.GCAllocationTick += data =>
            {
                // One tick per ~100KB allocated, attributed to a type. Aggregated
                // rather than emitted per-event: at allocation-heavy load the raw
                // ticks would dominate the record count for very little extra
                // information.
                var type = string.IsNullOrEmpty(data.TypeName) ? "<unknown>" : data.TypeName;
                var current = allocations.GetValueOrDefault(type);
                allocations[type] = (current.Bytes + data.AllocationAmount64, current.Ticks + 1);
            };

            source.AllEvents += ev =>
            {
                if (ev.ProviderName != "Microsoft-DotNETCore-SampleProfiler") return;
                cpuTotal++;

                var stack = ev.CallStack();
                if (stack is null) { withoutStack++; return; }

                var (folded, depth) = Folding.Fold(stack);
                if (folded.Length == 0) { withoutStack++; return; }

                // Hash the UNCUT stack: two stacks truncated to the same visible
                // prefix are still different stacks.
                var hash = Folding.Hash(folded);
                var (cut, truncated) = Folding.Truncate(folded);
                var key = (hash, ev.ThreadID);

                if (!cpu.TryGetValue(key, out var sample))
                {
                    sample = new FoldedSample(cut, hash, depth, truncated, depth, ev.ThreadID);
                    cpu[key] = sample;
                }
                sample.SampleCount++;
            };

            source.Process();

            log.Info(
                $"parsed {cpuTotal} CPU samples into {cpu.Count} (stack,thread) groups " +
                $"({withoutStack} without a usable stack); " +
                $"{gcs.Count} GCs, {contentionEvents} blocking contentions in " +
                $"{contentions.Count} (stack,thread) groups, " +
                $"{allocations.Count} allocation types");

            var allocSummaries = allocations
                .Select(kv => new AllocationSummary(kv.Key, kv.Value.Bytes, kv.Value.Ticks))
                .OrderByDescending(a => a.Bytes)
                // Long tail of one-off types adds records without adding insight.
                .Take(50)
                .ToList();

            return new ParsedTrace(cpu.Values, gcs, contentions.Values, allocSummaries);
        }
        finally
        {
            TryDelete(etlxPath, log);
        }
    }

    private static void TryDelete(string path, ILoggerLike log)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            // Logged rather than swallowed: the .etlx is ~9x the nettrace, so
            // leaking them fills the volume and the next session fails for a reason
            // that looks nothing like disk space.
            log.Warn($"could not delete intermediate {path}: {ex.Message}");
        }
    }
}
