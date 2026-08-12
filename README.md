# otlp-dotnet-alpine-musl-profiler

Profiling .NET workloads that run in **Alpine/musl** containers, into Dynatrace — on demand,
triggered from a Dynatrace workflow, with flame graphs you can open from a problem.

Alpine/musl is a blind spot for .NET profiling. This is a working reference implementation,
not a demo: every claim below was measured on a live cluster.

## Start here

| | |
|---|---|
| **[Getting started](docs/GETTING_STARTED.md)** | The happy path. Follow it top to bottom. |
| **[Lessons learned](docs/LESSONS_LEARNED.md)** | Everything that surprised us. Read it when something behaves oddly — most failures here are silent, and several error messages point away from the cause. |

## What it does

```
Davis problem ─► workflow ─► EdgeConnect ─► broker ─► gate (ConfigMap)
                                                        │
                                    ┌───────────────────┴───────────────────┐
                                    ▼                                       ▼
                         eBPF DaemonSet                          EventPipe sidecar
                    managed + kernel frames                  GC, contention, line numbers
                       always on, cheap                        on demand, expensive
                                    │                                       │
                                    └───────────────► Grail ◄───────────────┘
                                                        │
                                                  Profile Viewer
```

Two independent profilers emit the same record shape, so they reassemble into one picture.
Profile data arrives as **OTLP logs**, because Dynatrace does not ingest the OpenTelemetry
profiles signal yet — the schema mirrors the OTLP profiles data model so that when it does,
migrating is a transport change rather than a re-model.

Everything below was measured on a live cluster on that date, against Collector `0.158.0`,
eBPF profiler `v0.0.202632`, .NET `9.0` on Alpine `3.23`, dotnet-monitor `10.0.3`, and
EdgeConnect `v1.744.0`. **Several findings are version-specific and will expire** — the full
version table, and which claims are expected to age, is at the top of
[LESSONS_LEARNED.md](docs/LESSONS_LEARNED.md#read-this-first-everything-here-is-point-in-time).

Most importantly: this exists because **Dynatrace does not ingest the OpenTelemetry profiles
signal**, which reached public Alpha in March 2026. If that has changed, most of this pipeline
is unnecessary — the schema mirrors the OTLP profiles model so migration is a transport change.
Check first.

From a single workflow click, on .NET 9 / Alpine / arm64:

| | |
|---|---|
| Managed frames resolved | **100%** — including async state machines, generics, Kestrel internals |
| Deep call chains | 12-frame chain named end to end, 46-frame stacks observed |
| Samples parsed | 2.88M, **zero** unresolved stacks |
| GC | 594 collections, by generation and reason |
| Lock contention | 7,998 waits → 97 call paths, top entry named exactly |
| Cost | ~$0.0006 per 90-second single-pod profile |

## Repository layout

```
connector/profilestologsconnector/   OTLP profiles → folded-stack log records (Go)
distribution/                        Collector build manifest
src/ProfileAgent/                    EventPipe half: dotnet-monitor → nettrace → OTLP
src/Broker/                          The endpoint a workflow calls
src/DemoApp/                         Sample workload with four hotspot shapes
app/profile-viewer/                  Strato app: flame graphs
deploy/                              Kubernetes manifests and the workflow
docs/queries/                        Validated DQL
research/                            Measurements behind the decisions
wayfinder/                           Decision map
```

## Honest limitations

- **Thread-level correlation**, not per-sample. Samples join to spans on thread id plus time
  window; the OTLP model's per-sample trace/span fields are left null rather than guessed at.
- **Native frames never symbolize on Alpine.** Managed and kernel resolve at 100%; native ELF
  at 0%, permanently, because Alpine strips and no debuginfo exists for Microsoft's musl
  runtime. They appear as `module+0xaddress`.
- **Allocation type attribution** needs `Verbose` event level and is off by default.
- **`BufferSizeInMB: 128` is unmeasured.** It is charged against your *application's* memory
  limit, so profiling can OOMKill the workload it observes. Measure it against yours.
- **EdgeConnect is amd64-only**, so the workflow trigger needs one x86 node on an arm64
  cluster.
- **Setup cannot be fully automated.** Four things require the Dynatrace UI because the API
  refuses them even with the named scope.

## Prior art

Evolved from [`dynatrace-otlp-profiling-poc`](https://github.com/cooperjfecteau-cell/dynatrace-otlp-profiling-poc):
the OTLP ingest path, the retry and circuit-breaker exporter, the aggregate-per-window shape,
the collector config, and the load generator. Its C# sampler is deliberately **not** carried
over — it recorded manually-wrapped sections rather than walking stacks, and shipped no stack
at all, which makes flame graphs unreachable from its data. Its JSON-to-OTLP exporter also
cannot work: that endpoint is protobuf-only.

## License

Apache-2.0
