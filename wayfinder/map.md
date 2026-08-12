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
- [Research — eBPF `dotnet_tracer` on arm64+musl](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/7) — **managed .NET frames resolve fully**, verified on the live cluster and corroborated against real ASP.NET services (29-frame stacks with async state machines, generics, lambdas, Kestrel internals). Managed 100%, kernel 100%, **native ELF 0%** — Alpine strips and no debuginfo exists for Microsoft's musl runtime `.so`s, so native symbolization is *unavailable, not pending*. Upstream publishes **no runnable agent image**; the real distro is `ghcr.io/open-telemetry/opentelemetry-collector-releases/opentelemetry-collector-ebpf-profiler`
- [Research — TraceEvent on arm64+musl](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/8) — **works**, verified empirically on Alpine 3.23/musl 1.2.5/aarch64: 41,111 samples, zero unresolved frames, folded correctly. **The managed→OS thread-id mapping requirement in the charter rests on a false premise and is dropped** — EventPipe samples already carry the real Linux TID, the same namespace eBPF reports, so the join key needs no translation
- [Research — attribute vs body limits](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/27) — measured against the tenant: **attributes cap at exactly 32,768 chars, body at ≥524,287**. #3 was right, #6 was wrong. **Truncation is silent** — oversized attributes ingest successfully and are simply cut, so the deepest stacks vanish invisibly. Folded stack goes in `profile.stack.folded` (attribute) with a mandatory exporter-side guard at 30,000 chars setting `profile.stack.truncated`
- [Build — Alpine/musl .NET demo workload](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/17) — **the central claim is proven end to end.** A live ASP.NET Core app on `aspnet:9.0-alpine`/arm64 yielded a complete 46-frame managed stack: thread pool → Kestrel → async state machines → our lambda → all twelve `DeepStack` levels → `SHA256.HashData` → `libcrypto`. Session gating validated at the same time by opening a real session — 3,218 records, one session, one service, nothing else admitted from ~112 processes on the node
- [Build — eBPF DaemonSet](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/20) and [collector pipeline](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/21) — deployed to `dotnet-profiler` namespace and **validated against real profile data**. Ours emits folded stacks, stack hashes, `thread.id` and non-zero `cpu_ns` on 100% of records where the existing pipeline emits none of them, with per-process `service.name` instead of one constant. Three findings the deploy caught that review did not: the receiver needs debugfs and tracefs **mounted into the container** (privileged alone fails to start, not degrade); `include_env_vars` is required or `service.name` is null everywhere; and a `transform` declaring only `profile_statements` on a logs pipeline **silently does nothing** — enrichment now runs once upstream of the connectors
- [Build — Strato flame graph viewer](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/24) — deployed and driven in a browser. Renders `DeepStack.Level01`→`Level12`→`Workloads.CpuBound` from an Alpine/musl container. Numbers first, parked threads hidden loudly (54–67% of weight), sources never merged, second flame graph for contention weighted by time blocked. Browser testing found three defects review had not, including **contention carrying no stacks at all** — `ContentionStop` has the duration, `ContentionStart` has the stack; reading only Stop silently produced one empty group from 9,135 waits
- [Build — EventPipe sidecar](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/19) and [broker](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/22) — **the full chain works with no manual step.** Broker mints a ULID, writes the gate ConfigMap, and both halves react on their own: 4,427 eBPF records plus 1,276 CPU samples, 724 GCs and 14 contention groups from EventPipe, all under one session id. 2.8M samples parsed with zero unresolved stacks on musl/arm64. Writing one ConfigMap turned out to be the entire activation mechanism, so the broker never talks to dotnet-monitor and never fans out per-pod
- [Prototype — broker API](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/14) — `docs/broker-api.md`. The broker got *smaller*: it does not start eBPF (samples flow continuously, it only opens a gate) and cannot stop a trace early (dotnet-monitor yields no usable nettrace if terminated). **Concurrency settled: idempotent on `problemEventId`, and at most one active session per pod (409 otherwise)** — the unit is the pod because that is where the overhead is, since `BufferSizeInMB` is charged to the *application's* memory limit. A global lock was rejected: unrelated services share no EventPipe overhead
- **Headline claim: sidecar-led** — the reference implementation's claim is "a complete
  on-demand profiling pipeline with managed-code depth", not "zero-instrumentation CPU
  profiling". eBPF proved sufficient for CPU frames in #7, but the artifact's centrepiece is
  the EventPipe path and the depth it adds — GC, contention, allocation, line numbers,
  inlined frames.
- [Decision — EventPipe keeps CPU collection](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/30) — both halves collect CPU. Redundancy is **bounded by the session window** once export is gated, so the cost objection largely dissolves, and a sidecar-led claim needs the A/B comparison to justify the sidecar rather than assert it
- [Decision — `dotnet-monitor` sidecar](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/28) — glibc, documented as intentional. A sidecar-led story is exactly the case where dotnet-monitor's trigger rules, egress providers and auth get used, and adopters can verify a Microsoft artifact independently. The app image stays Alpine; the boundary is a Unix socket, not an ABI
- **Two-tier export: metrics always-on, per-stack logs gated to sessions** — the shape that
  resolves the cost/coverage tradeoff instead of trading one for the other. Elastic's
  `profilingmetricsconnector` emits classified counters (`samples.kernel.count`,
  `samples.native.count`, per-runtime, with syscall/shlib/kernel-area attributes) at a tiny
  fraction of per-stack volume, so it runs continuously and preserves a **pre-problem triage
  signal**. The expensive per-stack log records are emitted only during a workflow-triggered
  session. Metrics tell you *where* to look; logs tell you *what the call path was*.
- **Gating lives inside our connector, not a separate gateway** — the objection that sank
  [#31](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/31) was
  that gating needed a new hot-path component. Since #32 builds a profiles→logs connector
  anyway, session-awareness folds into it. Cost control still also uses the static levers:
  scoping to target `service.name` and a deliberate sample rate (19 Hz vs 99 Hz is ~5x)
- **Retention: 7 days, pay-per-query** — settled directly by the owner after #6 showed
  *Retain with Included Queries* has a 10-day minimum and costs 28.6x on storage. Profiling is
  write-heavy and read-light, so paying 28.6x to make the cheap half free is the wrong trade.
  Query cost is instead controlled by bucket filters and session-bounded queries.
- [Task — tenant details and ingest token](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/12) — token verified live (204 on `logs/ingest`). **The OTLP endpoint is protobuf-only** — `application/json` returns 415 even with a valid payload, so the PoC's hand-rolled JSON POST cannot work and export must go through the collector's `otlphttp` exporter. `.live` serves ingest, `.apps` serves the platform; they 404 each other's paths
- [Task — create the profiling bucket](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/29) — `profiling_dotnet_7d`, table `logs`, **retention 7 days accepted**, created manually by the owner after `dtctl` hit a 403. Trap recorded: OAuth scopes are not IAM permissions, and `--check-scopes` passes while the API refuses
- **Two token types, one stored secret** — ingest (`logs.ingest`, `events.ingest`,
  `openTelemetryTrace.ingest`) uses a classic API token; bucket management
  (`storage:bucket-definitions:*`) uses a platform token. Only the API token is stored in
  `.env`; bucket creation is a one-time interactive `dtctl` step, so the long-lived runtime
  secret cannot create or delete storage.

## Not yet specified

- **How the connector learns a session is active** — gating is decided, the control path is
  not. The connector runs as a DaemonSet, so the broker must reach every node: a ConfigMap
  the connector watches (natural fan-out, eventually consistent), an HTTP control endpoint per
  pod (immediate, but the broker must enumerate pods), or a CRD. Sharpen once the broker API
  is prototyped in #14.
- **Broker authentication** — the one part of the API left undecided. How a Dynatrace workflow proves it may start a profile.
  Shared secret, OAuth client, or mTLS. Sharp enough to ticket once the broker API shape
  is prototyped.
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
- **A standalone session gateway service** — [#31](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/31). Gating itself came back once the
  always-on metrics tier removed its cost to coverage, but it is implemented *inside* the
  connector in #32 rather than as a separate hot-path service. The standalone-service design
  on #31 stays out of scope.
- **OneAgent comparison / benchmarking** — interesting, not on the route.
- **Windows containers** and **a local Docker build loop** — deliberately excluded.
