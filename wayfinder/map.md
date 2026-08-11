# Map — .NET profiling on Alpine/musl into Dynatrace

## Destination

A public reference implementation — repo, running system, and adoption docs — that gets
CPU/GC/contention profiling data out of .NET workloads running in Alpine/musl containers
and into Dynatrace as OTLP logs, on demand, triggered by a Dynatrace workflow, and
correlated well enough that a human can go from a problem to a flame graph.

Done when a third party can follow the docs and stand the same thing up in their own
workload, and when the demo cluster shows it working end to end.

## Notes

**This map carries execution.** Wayfinder's default is plan-don't-do; this effort
overrides that in favour of a built artifact, because "anyone can implement this" is only
demonstrable from something that ran. Decision tickets still come first and gate the
build tickets that depend on them.

**Domain**: continuous profiling, eBPF, .NET EventPipe/nettrace, OpenTelemetry Collector,
Dynatrace Grail/DQL, Kubernetes on EKS, Strato app development.

**Skills to consult**: `dt-dql-essentials` before any DQL; `dt-app-dashboards` and the
`dt-app-mcp` Strato tools before UI work; `dt-js-runtime` for app functions;
`dt-alerting` for the workflow trigger; `dt-obs-kubernetes` for cluster queries;
`writing-for-agents` for the adoption docs.

**Standing preferences**: comment *why*, not *what*. Surface Dynatrace API errors as
actionable messages with the raw error as a suffix. Never commit tenant IDs, tokens, or
service-user UUIDs. Every documented DQL query carries an explicit bucket filter.

## Decisions so far

- [Charter — design decisions settled during charting](#) — 28 questions resolved across four grilling rounds; the full record of what was chosen and why

## Not yet specified

- **Broker authentication** — how a Dynatrace workflow proves it may start a profile.
  Shared secret, OAuth client, or mTLS. Sharp enough to ticket once the broker API shape
  is prototyped.
- **Concurrent and overlapping sessions** — what happens when a second profile is
  triggered on a pod already being profiled. Reject, queue, or join?
- **Sampling overhead** — the cost of EventPipe collection on the profiled process, which
  needs measuring before anyone can be told it's safe for production.
- **CI/CD for Alpine image builds** — GitHub Actions is the assumed path (no local Docker
  by choice), but the workflow shape isn't pinned.
- **DaemonSet RBAC and privilege scope** — the least privilege the eBPF profiler can run
  with, which matters a lot to anyone adopting this.
- **Strato app state** — whether the viewer stores anything per session, and its retention.

## Out of scope

- **Python and Java polyglot demos** — ruled out during charting; they widen the surface
  without strengthening the .NET/musl case.
- **Native OTLP profiles ingest** — Dynatrace does not ingest the profiles signal today.
  The schema is deliberately modelled on the OTLP profiles alpha so this becomes a
  transport swap later, but the swap itself is a future effort.
- **OneAgent comparison / benchmarking** — interesting, not on the route.
- **Windows containers** and **a local Docker build loop** — deliberately excluded.
