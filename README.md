# otlp-dotnet-alpine-musl-profiler

Getting CPU, GC, and thread-contention profiling data out of **.NET workloads running in
Alpine/musl containers** and into **Dynatrace**, on demand, triggered by a Dynatrace
workflow, and correlated well enough to go from a problem to a flame graph.

> **Status: charting.** Nothing here runs yet. The design is settled; the build is
> tracked as issues. See the [wayfinder map](../../issues?q=label%3Awayfinder%3Amap).

## Why this exists

Alpine/musl containers are a blind spot for .NET profiling. This repo is a reference
implementation an adopter can copy into their own workload — not a demo that only works
on the machine it was written on.

## Shape

Two independent shippers, correlated in DQL inside Dynatrace:

| Component | Gets you | Runs as |
|---|---|---|
| [`opentelemetry-ebpf-profiler`](https://github.com/open-telemetry/opentelemetry-ebpf-profiler) | Kernel and native frames, whole-node, zero-touch | Node DaemonSet |
| `dotnet-monitor` + EventPipe | Managed frames, GC, thread contention | Per-pod sidecar |

Both fold their stacks, stamp a shared join key set, and export as **OTLP logs** —
Dynatrace does not ingest the OpenTelemetry profiles signal today. The record schema
mirrors the OTLP profiles alpha data model so that when it does, this becomes a transport
change rather than a re-model.

A **broker service** in-cluster gives a Dynatrace workflow one endpoint to call: it mints
a session ID, starts collection for a fixed window, and pushes a Davis event onto the
triggering problem carrying a deep link to the flame graph viewer.

## Honest limitations

Recorded up front, because a reference implementation that oversells itself is worse than
none:

- **Correlation is thread-level, not per-sample.** Profile samples join to spans in DQL on
  thread ID plus time window. The OTLP model's per-sample trace/span fields are left
  **null** rather than guessed at.
- **The eBPF profiler's .NET unwinder is unverified on musl.** It is the highest-risk
  assumption in the design and is being tested, not assumed.
- **Profile data is expensive.** It lands in Grail as logs and bills as logs. A dedicated
  short-retention bucket and a worked cost estimate are part of the deliverable.

## Prior art

Evolved deliberately from [`dynatrace-otlp-profiling-poc`](https://github.com/cooperjfecteau-cell/dynatrace-otlp-profiling-poc) —
the OTLP logs ingest path, the exporter's retry and circuit-breaker behaviour, the
aggregate-per-window shape, the collector config, and the load generator. The PoC's C#
sampler is **not** carried over: it recorded manually-wrapped sections rather than walking
stacks, and shipped no stack at all, which makes flame graphs unreachable from its data.

## License

Apache-2.0
