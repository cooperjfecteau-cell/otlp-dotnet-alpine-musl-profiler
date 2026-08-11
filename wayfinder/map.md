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

<!-- Source of truth for the map body is THIS FILE. Always push it with
     `gh issue edit 1 --body-file wayfinder/map.md`. Do not round-trip the body
     through `gh issue view --jq`: that returns an array of lines, and writing it
     back collapses every newline to a space. -->

- [Charter — design decisions settled during charting](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/2) — 28 questions resolved across four grilling rounds; the full record of what was chosen and why
- [Task — create the GitHub repo and scaffold tracker config](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/10) — private repo created; GitHub sub-issues and native dependencies both confirmed working
- [Task — provision the EKS cluster](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/11) — `eks-arm-new` in us-east-1, arm64/AL2023/kernel 6.1, BTF and tracefs verified present; arm64 widens the musl risk on #7/#8/#9
- [Research — OTLP profiles alpha data model](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/3) — mirrors opentelemetry-proto v1.10.0 (v1.11.0 differs only in docs); **record grain corrected to one per unique (stack, thread) per window** so a record *is* an OTLP Sample; `Stack.location_indices` is leaf-first and must be reversed; null trace/span is the model's own defined null (`link_index: 0`)
- [Research — Dynatrace event APIs](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/5) — one endpoint for both (`POST /api/v2/events/ingest`, scope `events.ingest`); **attaching a Davis event to a problem does not exist** — the real mechanism is `CUSTOM_ANNOTATION` carrying `annotation.problem_ids`, idempotent on `annotation.id`
- [Research — Grail bucket, retention, and cost](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/6) — **a record cannot select its own bucket**; routing is an OpenPipeline Storage-stage `bucketAssignment` with two independent match points. ~$0.0006 per 90s single-pod profile; ingest is 94% of the bill and unbounded queries are the tail risk
- [Research — dotnet-monitor on Alpine](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/4) — **no musl image exists past 7.3.4**; the sidecar runs glibc deliberately, which is sound because the app boundary is a Unix socket carrying diagnostic IPC, not an ABI. Sidecar must be the listener (CoreCLR can only dial out); `nosuspend` is load-bearing; `durationSeconds` must be set up front
- **Retention: 7 days, pay-per-query** — settled directly by the owner after #6 showed
  *Retain with Included Queries* has a 10-day minimum and costs 28.6x on storage. Profiling is
  write-heavy and read-light, so paying 28.6x to make the cheap half free is the wrong trade.
  Query cost is instead controlled by bucket filters and session-bounded queries.
- **Two token types, one stored secret** — ingest (`logs.ingest`, `events.ingest`,
  `openTelemetryTrace.ingest`) uses a classic API token; bucket management
  (`storage:bucket-definitions:*`) uses a platform token. Only the API token is stored in
  `.env`; bucket creation is a one-time interactive `dtctl` step, so the long-lived runtime
  secret cannot create or delete storage.

## Not yet specified

- **Broker authentication** — how a Dynatrace workflow proves it may start a profile.
  Shared secret, OAuth client, or mTLS. Sharp enough to ticket once the broker API shape
  is prototyped.
- **Concurrent and overlapping sessions** — what happens when a second profile is
  triggered on a pod already being profiled. Reject, queue, or join?
- **Sampling overhead** — the cost of EventPipe collection on the profiled process, which
  needs measuring before anyone can be told it's safe for production. Sharpened by #4:
  `BufferSizeInMB` is charged to the *application's* memory limit, not the sidecar's, so
  profiling can OOMKill the workload it is observing. 128 MB is a guess pending measurement.
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
