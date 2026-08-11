# Wayfinder tickets

Format: `## <id> | <title>` then `labels:` / `blocked-by:` lines, then `---`, then the body.
`push.ps1` parses this file and creates one GitHub issue per section.

## C1 | Charter — design decisions settled during charting
labels: wayfinder:grilling
blocked-by:
state: closed
---
## Question

What is being built, and on what terms?

## Resolution

Settled across four grilling rounds before the map was charted.

**Destination and scope**
- Destination is a working reference implementation — repo, running system, adoption docs — not a spec handed off.
- Python and Java polyglot demos are out of scope.
- The flame graph viewer is in scope for this map, not deferred.

**Runtime**
- Managed Kubernetes on AWS EKS. Cluster to be provided later.
- Nodes must have kernel BTF (AL2023, not AL2) or the eBPF profiler will not load.
- No local Docker or WSL by choice; Alpine images build in CI.

**Profiling components — no feature flags, independent shippers**
- eBPF: `opentelemetry-ebpf-profiler` as a node DaemonSet, for kernel and native frames.
- EventPipe: `dotnet-monitor` as a per-pod sidecar, for managed frames, GC, and contention.
- The PoC's cooperative-section sampler is dropped entirely — it was manual section timing, not sampling.
- The two ship independently; correlation happens in DQL inside Dynatrace.

**Data model**
- Transport is OTLP logs to `/api/v2/otlp/v1/logs`; Dynatrace does not ingest the profiles signal today.
- Field layout mirrors the OTLP profiles alpha data model so a future native-ingest swap is a transport change, not a re-model.
- Full stacks ship as a folded-stack string attribute plus a stack hash — one record per unique stack per window. Flame graphs are unreconstructable without this; the PoC's C# path dropped stacks entirely.
- nettrace is converted to folded stacks **in the sidecar**, in-process, via `Microsoft.Diagnostics.Tracing.TraceEvent`.

**Correlation**
- Thread-level, joined at query time in DQL on thread ID plus time window.
- Per-sample trace/span fields in the OTLP model are left **null** rather than guessed at — a wrong span ID is worse than none.
- Mandatory join keys on every shipper: `profile.session_id`, `k8s.pod.name`, `k8s.namespace.name`, `host.name`, `container.id`, `process.pid`, `thread.id`, `service.name`, aligned timestamps.
- Managed thread ID → OS thread ID mapping is an explicit sidecar requirement. Without it the two profilers never join.

**Control plane**
- A broker service in-cluster exposes one authenticated endpoint taking `{service, duration, problemId}`.
- It resolves pods, starts EventPipe collection, flips eBPF sampling for the window, mints a ULID `profile.session_id`, and pushes the event.
- Session identity is a ULID, not the problem ID — one problem can trigger several profiles.

**Events**
- A Davis event on the triggering problem, plus a custom event on the service entity.
- The event carries the session ID and the viewer deep link.

**Storage**
- Dedicated Grail bucket, 7-day retention. Query cost is billed on bytes scanned regardless of bucket, so every documented query carries an explicit bucket filter.
- A worked cost estimate for a 90-second profile across N pods belongs in the docs.

**Viewer**
- A Strato app querying DQL directly from the UI via `useDql`.
- Deep-links on `session_id` alone; the session record carries its own window and service.

**Demo app**
- ASP.NET Core minimal API on `mcr.microsoft.com/dotnet/aspnet:9.0-alpine`.
- Four hotspot shapes: CPU-bound, allocation-heavy, lock-contended, I/O-bound.
- At least one path with a deliberately deep call stack — a flame graph of three frames demonstrates nothing.
- Load generator ported from the PoC.

**Inherited from the PoC** (`cooperjfecteau-cell/dynatrace-otlp-profiling-poc`), deliberately, not wholesale: the direct OTLP logs ingest path, `traceId`/`spanId` as first-class log record fields, the retry + circuit-breaker exporter, the aggregate-per-window frequency map shape, the collector config, the load generator.

## R1 | Research — OTLP profiles alpha data model field layout
labels: wayfinder:research
blocked-by:
---
## Question

What exactly is the OTLP profiles data model as of the March 2026 public alpha, and which
of its fields map onto a log record?

Produce the concrete field list we will mirror: sample, location, function, mapping,
stack, attribute units, and the per-sample trace/span slots. Note which fields have no
sensible log-record equivalent and what we do instead.

Gates the exporter schema, the DQL, and the viewer — nearly everything downstream.

## R2 | Research — dotnet-monitor on Alpine: images, diagnostic ports, on-demand API
labels: wayfinder:research
blocked-by:
---
## Question

Does `dotnet-monitor` ship a musl/Alpine image, and what is the exact configuration for a
sidecar that collects on demand rather than continuously?

Cover: available image tags, `DOTNET_DiagnosticPorts` in listen vs connect mode, the
shared-volume layout for the diagnostic socket, the HTTP API surface for starting and
stopping a trace of fixed duration, authentication on that API, and which EventPipe
providers give CPU samples, GC, and contention.

## R3 | Research — Dynatrace event APIs for Davis events and custom entity events
labels: wayfinder:research
blocked-by:
---
## Question

What are the exact API calls and payload schemas to (a) attach a Davis event to an
existing problem and (b) push a custom event onto a service entity?

Include required scopes, field limits (the deep link has to fit), how the event is
addressed to a specific entity, and how it renders for a human looking at the problem.

## R4 | Research — Grail custom bucket, retention, and log ingest cost model
labels: wayfinder:research
blocked-by:
---
## Question

How is a custom Grail bucket created and given 7-day retention, and what does profile
data actually cost?

Produce a worked estimate: ingest cost per GB, query cost per GB scanned, and the
resulting cost of one 90-second profile at a stated sample rate across N pods. This
number goes in the adoption docs — anyone copying this pattern needs it before they
switch it on.

## R5 | Research — does the eBPF profiler's dotnet_tracer resolve managed frames on musl?
labels: wayfinder:research
blocked-by: T2
---
## Question

The single highest-risk assumption in the plan, and currently untested.

`opentelemetry-ebpf-profiler` advertises a .NET unwinder. Does it resolve managed frames
for a .NET process in an Alpine/musl container, or does it degrade to native frames and
addresses?

Whichever way it lands is a finding worth documenting. If it degrades, that degradation
*is* half the story this reference implementation tells, and it raises how much weight
EventPipe has to carry.

## R6 | Research — does TraceEvent parse nettrace on Alpine musl?
labels: wayfinder:research
blocked-by: T2
---
## Question

`Microsoft.Diagnostics.Tracing.TraceEvent` is the chosen nettrace parser and it runs
inside the sidecar, which means it runs on musl. Does it work there?

TraceEvent has historically carried native dependencies and Windows-shaped assumptions.
If it fails on musl, the alternative is a glibc-based sidecar image or a different parser,
and that choice changes the sidecar design.

## R7 | Research — musl native frame symbolization for the eBPF profiler
labels: wayfinder:research
blocked-by: R5
---
## Question

How do native frames from musl-linked binaries get symbolized, given Alpine images
routinely ship stripped binaries with no separate debuginfo?

Cover what the profiler can resolve unaided, whether a debuginfo sidecar or symbol server
is needed, and what an adopter has to change about their image build to get readable
native frames.

## T1 | Task — create the GitHub repo and scaffold tracker config
labels: wayfinder:task
blocked-by:
---
## Question

Stand up `otlp-dotnet-alpine-musl-profiler` as a public GitHub repo, push the initial
scaffold, and write `docs/agents/issue-tracker.md` so the engineering skills know where
issues live.

Records: repo URL, default branch, and the tracker config location.

## T2 | Task — provision the EKS cluster
labels: wayfinder:task
blocked-by:
---
## Question

Stand up EKS with BTF-capable nodes and record how to reach it.

Requirements: AL2023 node AMI (AL2 largely lacks kernel BTF and the profiler will not
load), nodes large enough to run a DaemonSet plus several Alpine .NET pods, and a region.
Privileged containers and host PID namespace must be permitted.

Records: cluster name, region, kubeconfig retrieval command, node AMI and kernel version,
and the verified presence of `/sys/kernel/btf/vmlinux`.

Unblocks every experiment that needs a real kernel.

## T3 | Task — Dynatrace tenant details and ingest token
labels: wayfinder:task
blocked-by:
---
## Question

Supply the "fal" tenant URL and create an ingest token with the scopes for OTLP log
ingest, event push, and bucket management.

Placeholders go in `.env.example`; the real values never land in the repo. Records where
the token is stored and which scopes it carries.

## P1 | Prototype — DQL that reassembles a flame graph from folded-stack records
labels: wayfinder:prototype
blocked-by: R1, R4
---
## Question

Write the DQL that turns folded-stack log records back into something a flame graph can
render, and confirm it is tractable.

This is the load-bearing test of the whole record shape. If the query to rebuild a tree
from folded stacks is unreasonable in DQL, the schema is wrong and it is far cheaper to
learn that now than after the sidecar is built.

Consult `dt-dql-essentials` first. Deliverable: a working query against sample data, plus
a judgement on whether the shape holds.

## P2 | Prototype — broker API shape
labels: wayfinder:prototype
blocked-by: R2
---
## Question

Sketch the broker's HTTP surface concretely enough to react to.

Cover: the request a Dynatrace workflow sends, the synchronous response, how the caller
learns a session finished, error and rejection cases, and what the broker does when the
target service has many pods. A rough stub is worth more here than a description.

## G1 | Grilling — flame graph viewer UX and deep-link contract
labels: wayfinder:grilling
blocked-by: R1, P1
---
## Question

What does the viewer actually show, and what does the deep link have to carry?

Cover: landing state when opened cold vs from a problem link, how eBPF and EventPipe data
are presented together given they arrive as separate shippers joined in DQL, how the
thread-level correlation approximation is communicated honestly to the viewer, and the
exact deep-link parameter contract.

## G2 | Grilling — documentation structure and the adoption guide
labels: wayfinder:grilling
blocked-by: I9
---
## Question

What does someone need in order to run this in their own workload, and in what order?

The destination names documentation as a first-class deliverable. Cover the reader's
starting point, what they must change in their own manifests, what they can copy
unchanged, the failure modes worth calling out in advance, and where the honest
limitations are stated.

## I1 | Build — ASP.NET Core demo app on Alpine
labels: wayfinder:task
blocked-by: T1
---
## Question

Minimal API on `mcr.microsoft.com/dotnet/aspnet:9.0-alpine` with four endpoints exercising
CPU-bound, allocation-heavy, lock-contended, and I/O-bound work, plus at least one
deliberately deep call stack. OpenTelemetry tracing enabled so spans exist to correlate
against.

## I2 | Build — load generator
labels: wayfinder:task
blocked-by: I1
---
## Question

Port the PoC's load generator to drive the four endpoints with a traffic mix that produces
a profile worth looking at, including a sustained mode for triggering anomaly-based
workflows.

## I3 | Build — EventPipe sidecar
labels: wayfinder:task
blocked-by: R1, R2, R6, P2
---
## Question

The sidecar: `dotnet-monitor` collecting on demand, TraceEvent parsing nettrace in-process,
stacks folded and hashed, managed→OS thread ID mapping captured, records stamped with the
full join key set, exported as OTLP logs.

The largest single component and the one nothing in the PoC prefigures.

## I4 | Build — eBPF profiler DaemonSet and collector pipeline
labels: wayfinder:task
blocked-by: R5, T2
---
## Question

Deploy `opentelemetry-ebpf-profiler` as a DaemonSet with the least privilege that works,
wired to the collector, emitting the same join keys and folded-stack shape as the sidecar
so the two reassemble in DQL.

## I5 | Build — OTel Collector config for profile-as-logs export
labels: wayfinder:task
blocked-by: R1, T3
---
## Question

Collector pipeline receiving from both shippers, normalizing to the agreed schema,
batching, and exporting to the Dynatrace OTLP logs endpoint with retry and backpressure.
Start from the PoC's `collector/config.yaml`.

## I6 | Build — broker service
labels: wayfinder:task
blocked-by: P2, R3
---
## Question

Implement the broker: authenticated endpoint, pod resolution, session ULID minting,
EventPipe start/stop, eBPF window control, and the Davis event plus service custom event
carrying the session ID and deep link.

## I7 | Build — Dynatrace workflow that triggers a profile
labels: wayfinder:task
blocked-by: I6
---
## Question

A workflow that fires on a problem — response time or CPU/memory anomaly — and calls the
broker for a 90-second profile, passing the problem ID. Consult `dt-alerting`.

## I8 | Build — Strato flame graph app
labels: wayfinder:task
blocked-by: G1, P1
---
## Question

The viewer: a Strato app querying DQL directly via `useDql`, rendering a flame graph from
folded-stack records, deep-linked on `session_id`.

Consult the `dt-app-mcp` Strato tools before writing components; import from category
subdirectories, never the package root.

## I9 | Build — end-to-end verification on the cluster
labels: wayfinder:task
blocked-by: I3, I4, I5, I6, I8
---
## Question

Drive the whole path on the real cluster: load generator induces a problem, workflow fires,
broker starts a session, both shippers emit, data lands in the bucket, the event appears on
the problem, the deep link opens the viewer, and the flame graph shows the induced hotspot.

Records what worked, what degraded, and the measured overhead.

## I10 | Docs — adoption guide
labels: wayfinder:task
blocked-by: G2, I9
---
## Question

Write the documentation the destination is defined by: architecture, what to copy, what to
change, cost, limitations, and the honest account of what the thread-level correlation
approximation does and does not give you.
