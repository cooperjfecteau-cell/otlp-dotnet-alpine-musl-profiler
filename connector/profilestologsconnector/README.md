# profilestologs connector

Converts OTLP **profiles** into OTLP **log records** carrying folded stacks.

It exists for one reason: Dynatrace does not ingest the OpenTelemetry profiles signal.
Until it does, profile data has to arrive as something Dynatrace *does* ingest, and logs
are the only shape that preserves per-stack detail.

The record layout deliberately mirrors the OTLP profiles data model, so that when native
profile ingest arrives, migrating is a **transport change rather than a re-model**.

## Why not the existing one

A `profilestologs` connector already runs on the `eks-arm-test` cluster and works. It is not
reused because:

- there is **no upstream equivalent** — Elastic publishes a profiles→*metrics* connector, but
  nothing profiles→logs exists publicly;
- its **source is gone** — it was a one-off build;
- its **image lives in a private ECR**, so no adopter can pull it.

A reference implementation whose central component cannot be reproduced is a demo, not a
reference. Rebuilding was forced, not preferred.

## Configuration

```yaml
connectors:
  profilestologs:
    # Must stay below the platform's 32,768-character attribute ceiling.
    max_folded_chars: 30000
    schema_version: "otlp-profiles-v1development/1"
    # Set at resource level; OpenPipeline's bucketAssignment matcher tests these.
    resource_marker:
      dt.openpipeline.source: dotnet-profiler
    gating:
      enabled: true
      session_file: /etc/profiler-sessions/sessions.json
      reload_interval: 5s
```

Every field has a working default. Running with an empty config block produces correct,
cost-bounded output rather than something that must be tuned before it is safe.

## Emitted attributes

| Attribute | Notes |
|---|---|
| `profile.stack.folded` | Root-first, `;`-separated |
| `profile.stack.hash` | Hash of the **stack alone**, never including the thread |
| `profile.stack.depth` | Frame count |
| `profile.stack.truncated` | Present only when we cut it |
| `profile.stack.original_depth` | Present only when truncated |
| `profile.sample_count` | Samples in this (stack, thread) group |
| `profile.cpu_ns` | Derived: `sample_count × period` |
| `profile.period_ns`, `profile.window_start_ns`, `profile.window_duration_ns` | Window framing |
| `thread.id` | Real OS TID — the join key to EventPipe |
| `profile.session_id` | Present only when gating is on and a session matched |
| `profile.schema_version` | The proto is still `v1development`; consumers need to know the shape |

Per-sample `trace.id`/`span.id` are deliberately **absent**. `link_index: 0` is the model's
own defined null, and correlation here is thread-level by design — a wrong span id is worse
than none.

## Four things that are easy to get wrong

**Stacks are stored leaf-first.** `Stack.location_indices` runs leaf→root, the opposite of
what every flame graph tool expects. The reversal in `fold.go` is the single most important
line in this connector; getting it backwards renders the graph upside down and it still looks
plausible.

**The record grain is `(stack, thread)`, not stack.** The spec defines Sample identity as
`{stack_index, attributes, link_index}` and `thread.id` is a Sample attribute, so one stack
seen on five threads is five Samples. Keying on the stack alone would force an arbitrary
thread onto a merged record and destroy the EventPipe join. The hash still covers the stack
alone, so flame-graph queries collapse across threads unchanged.

**The platform truncates attributes silently at 32,768 characters.** Measured, not assumed.
No rejection, no warning, no flag — the value is simply cut. Since the stacks that overflow
are the *deepest* ones, silent truncation biases every flame graph toward shallow paths while
looking perfectly healthy. So this connector cuts first, at 30,000, **from the root end** —
leaf frames carry the hotspot — and flags it. Config validation rejects any
`max_folded_chars >= 32768`.

**Gating fails closed.** A missing session file means "no sessions". A *malformed* one also
means no sessions, and logs an error. It must never be read as "profile everything": that
turns a ConfigMap typo into an unbounded ingest bill.

## Gating and the two-tier design

With gating enabled and no active session, this connector emits **nothing** — not a reduced
sample, not a summary. That is intentional. The always-on tier is
`profilingmetricsconnector`, which classifies leaf frames into cheap counters continuously;
this connector is the expensive per-stack tier and runs only when a Dynatrace workflow opens
a session.

Metrics tell you *where* to look. This tells you *what the call path was*.

Sessions are read from a file rather than an HTTP endpoint because the collector runs as a
DaemonSet: the broker writes one ConfigMap and every node observes it, instead of the broker
enumerating pods and fanning out N calls it must then retry individually.

Be aware that ConfigMap propagation to a mounted volume is itself on the order of a minute,
so a short `reload_interval` does not make the whole path fast. A session that must start
*now* needs a different mechanism — see the map's open question on the control path.

## Unsymbolized frames

Native frames that cannot be resolved are emitted as `module+0xaddress` rather than dropped.
On Alpine this is the common case, not the exception: runtime `.so` files are stripped and no
public debuginfo exists for them, measured at **100% of native ELF frames**. Dropping them
would silently shorten stacks and misrepresent the call path; keeping them is honest and
still greppable.
