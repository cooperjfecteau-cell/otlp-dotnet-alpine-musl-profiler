# Lessons learned

Everything below was measured, not assumed. Most of it was learned by being wrong first.

The common thread: **these failures are quiet.** Records vanish with no error, a config is
read and ignored, a value is truncated without a flag, a component reports success for work it
never did. Almost none of them announce themselves, and several of the error messages point
somewhere other than the cause.

If you are adopting this, skim the headings. You will meet most of them.

---

## Profiling semantics

### eBPF resolves managed .NET frames on Alpine/musl — fully

The assumption the whole project rested on, and it holds. Measured on a live ASP.NET Core
workload on `aspnet:9.0-alpine`, arm64:

```
System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart
System.Threading.ThreadPoolWorkQueue.Dispatch
Microsoft.AspNetCore.Server.Kestrel…KestrelConnection`1.ExecuteAsync
…<ExecuteAsync>d__8.MoveNext                    ← async state machines, by name
Microsoft.AspNetCore.Routing.EndpointMiddleware.Invoke
Program+<>c.<<Main>$>b__0_8                     ← application lambda
DeepStack.Level01 → … → Level12                 ← twelve frames, all named
Workloads.CpuBound
System.Security.Cryptography.SHA256.HashData
```

Across 9,212 frames on a whole node: **managed 100%, kernel 100%, Go 100%, native ELF 0%.**

### Native frames never symbolize on Alpine

Not "not yet" — **not ever**, without changing your base image. Alpine strips binaries and
Microsoft publishes no debuginfo for its musl runtime `.so` files. Those frames unwind
correctly (module, offset, build ID all present) but cannot be named.

We emit them as `module+0xaddress` rather than dropping them, because a silently shortened
stack misrepresents the call path. The viewer colours them distinctly.

A debuginfo sidecar was considered and rejected: it cannot help for the binaries that matter,
and the managed frames — the ones anyone actually reads — are perfect.

### Wall-clock sampling means most of your profile is threads doing nothing

EventPipe's `SampleProfiler` samples **every thread, including parked ones**. Measured:

| | samples | share |
|---|---:|---:|
| parked (semaphore / monitor / wait) | 1,525,280 | **54%** |
| on-CPU | 1,274,072 | 46% |

The single largest stack was `LowLevelLifoSemaphore.WaitNative` at **28% of the entire
profile**, doing nothing at all.

A flame graph rendered from raw weight shows *waiting* as the dominant hotspot and buries the
real work. The viewer filters parked stacks by default and says loudly how much it hid. If you
build your own view, do the same — and never silently.

### "CPU time" from a wall-clock sampler is not CPU time

Our header originally read **`CPU 3,039,036 ms`** for a 220-second window. The number is real
— 53 threads sampled by wall clock — but calling it CPU is a lie the reader cannot catch. It
is now labelled "sampled thread-time across all threads".

### ContentionStop carries the duration; ContentionStart carries the stack

Reading only `ContentionStop` produced an **empty stack for all 9,135 waits**, which collapsed
every record into a single null-hash group that the viewer then filtered out. The durations
were correct throughout, which is exactly why nobody noticed.

Pair them by thread id. Once fixed, the top entry was `Workloads.HoldTheLock(int32)` at
5,432 ms blocked — precisely the method holding the contended lock.

### Aggregate contention; do not emit per event

7,266 individual contention records became **14 `(stack, thread)` groups** carrying count,
total and max wait. Same information, 500x fewer records, and a better answer: you want to know
*which call path waited and for how long*, not to scroll thousands of individual waits.

---

## Data model

### The record grain is (stack, thread), not stack

"One record per unique stack" and "thread.id on every record" cannot both hold — a stack seen
on five threads is either five records or one with an arbitrary thread.

The OTLP profiles spec settles it: `Sample` identity is `{stack, attributes, link}` and
`thread.id` is a Sample attribute. Hash the **stack alone** so flame-graph queries still
collapse across threads.

### Stacks are stored leaf-first, in two different APIs

The OTLP spec's `Stack.location_indices` runs leaf→root. TraceEvent's `CallStack()` walks
leaf→root via `Caller`. Both need reversing for a root-first folded string.

Get it wrong and the flame graph renders **upside down and looks entirely plausible**.

### Attributes truncate silently at 32,768 characters

Measured against the tenant with tail markers, because a value truncated *to* the limit still
looks plausible if you only check its length:

| Sent | Attribute stored | Tail intact |
|---:|---:|:--|
| 32,768 | 32,767 | yes |
| 65,536 | **32,768** | **NO** |
| 524,288 | **32,768** | **NO** |

No rejection, no warning, no flag. The body holds ≥524,287.

For profiling this is the worst possible failure mode, because **the stacks that overflow are
the deepest ones** — a flame graph built on truncated data looks healthy while systematically
under-representing deep paths. Truncate deliberately below the ceiling, from the **root** end,
and set a flag.

### The OTLP endpoint is protobuf-only

`application/json` returns **415** even with a valid OTLP payload. Only
`application/x-protobuf` is accepted. Hand-rolled JSON POSTs to `/api/v2/otlp/v1/logs` cannot
work — export through a collector, whose `otlphttp` exporter emits protobuf natively.

### A log record cannot choose its own bucket

There is no writable bucket field. `dt.system.bucket` is read-only. Routing happens in
OpenPipeline's Storage stage, and there are **two independent match points** — the routing
table picks the pipeline, the storage stage picks the bucket. Configure one and forget the
other and records land in `default_logs` at 5x your intended retention, silently.

---

## Platform and architecture

### dotnet-monitor has no musl image

None since 7.3.4. Every supported line ships Azure Linux or Ubuntu — glibc.

This does **not** break the design: the app↔sidecar boundary is a Unix socket carrying
diagnostic IPC, a byte stream rather than an ABI. The sidecar is yours; only the app image has
to be Alpine. Document it as intentional rather than hiding it.

### EdgeConnect is amd64-only

No arm64 image at any of its tags. On a Graviton cluster it dies with **`exec format error`**,
which reads like a corrupt image rather than a platform constraint.

If you are on arm64 and want the workflow trigger, you need one x86 node. That is a real cost
of choosing Graviton and worth knowing before you design around EdgeConnect.

### EdgeConnect's nested config needs double underscores

`EDGE_CONNECT_OAUTH__CLIENT_ID`, not `EDGE_CONNECT_OAUTH_CLIENT_ID`.

The single-underscore form is **acknowledged in the startup log** — "Environment variable
EDGE_CONNECT_OAUTH_CLIENT_ID will be used" — and then fails with `missing field 'oauth'`.

File-based config is a dead end: we proved with a busybox pod that the config was present at
`/`, world-readable, under both plausible filenames, and EdgeConnect still reported
"no yaml config files were found".

### The profiler needs debugfs and tracefs mounted, not just privileged

`privileged: true` is not sufficient. Without `/sys/kernel/debug` and `/sys/kernel/tracing`
mounted into the container it **fails to start**, not degrade:

```
failed to attach scheduler monitor … neither debugfs nor tracefs are mounted
```

The full set: `/sys/kernel/debug`, `/sys/kernel/tracing`, `/sys/fs/bpf`, `/sys/fs/cgroup`,
`/proc`, and the containerd socket.

### Upstream publishes no runnable eBPF profiler image

`opentelemetry-ebpf-profiler` has no releases, and its only image workflow ships the Debian
*build environment*. The real distribution is in **collector-releases**:
`ghcr.io/open-telemetry/opentelemetry-collector-releases/opentelemetry-collector-ebpf-profiler`.

`--feature-gates=service.profilesSupport` is mandatory, and the `debug` exporter prints
dictionary tables but **not** per-sample stack order — do not judge unwinder correctness from
it.

### How you publish your app decides whether it can be profiled

The unwinder finds the runtime by path pattern `/<version>/libcoreclr.so`. Framework-dependent
works. Self-contained and single-file may not. **NativeAOT is explicitly unsupported.**

Someone "optimizing" the image six months later will silently blind the profiler while the app
keeps working perfectly.

---

## Dynatrace API and IAM

### Four things the UI can do that a token cannot

| Thing | Error you get | Real cause |
|---|---|---|
| Create a Grail bucket | `403 Required permissions not met` | IAM policy, not token scope |
| Create an EdgeConnect config | `403 missing oauth2:clients:manage` | It mints an OAuth client underneath |
| Create a credential vault entry | `403` | No API path with an ingest token |
| Reassign a workflow's actor | *silently ignored* | Fixed at creation |

**`--check-scopes` passing is not a green light.** OAuth scopes are what a token *may request*;
the IAM policy bound to your user is what you are *permitted*. `dtctl` reported `status: ok`
and the API refused the same call.

You cannot fully automate first-time setup. Plan for clicks.

### Workflow identity has three separate requirements

1. The **actor** is whoever `dtctl` authenticated as, permanently — the field is ignored on
   create and cannot be reassigned.
2. That user must be signed in **and** have granted Workflows **Authorization settings** — a
   per-user, one-time consent.
3. The credential vault entry must **not** be owner-only, or the task fails with everything
   else correct.

And: **re-running `dtctl auth login` does not necessarily switch accounts.** An active browser
SSO session is reused silently and the CLI keeps reporting the old identity. Sign out of
Dynatrace SSO first, or use a private window.

### There is no API to attach a Davis event to a problem

Problem membership is decided by Davis correlation alone. The mechanism that works is a
**problem annotation** — `CUSTOM_ANNOTATION` with `annotation.problem_ids`.

Three details:
- It wants the **internal** event id (`event()["event.id"]`), not the `P-…` display id. The
  wrong one fails silently.
- The endpoint returns **201 even when entity mapping failed**. The real status is inside
  `eventIngestResults[]`. Trusting the status code means your annotation lands at environment
  level and nobody ever sees it.
- It is **idempotent on `annotation.id`** — exploit that. Post "capture in progress" at the
  start and overwrite it with the finished link at the end.

Avoid `POST /api/v2/problems/{id}/comments`: it exists, looks right, and renders only in
Problems Classic.

### Workflow expressions are not what you would guess

- `credential_vault()` is **not** a template function. It exists only inside a *Run JavaScript*
  action. The HTTP action reads the vault through a structured `credential` block — which can
  only inject into the standard `Authorization` header, never a custom one.
- `event()` is **undefined on a manual run**, so a workflow written only for the trigger path
  cannot be smoke-tested by clicking Run. Guard it with `{% if event() %}`.
- A `davis-problem` trigger **requires `categories`**, and it is an object, not a list.
- Workflows created via API default to **private**, which hides them from the main list. It
  looks exactly like the create silently failed.

### `dtctl` gotchas

- `dtctl apply` on a workflow **creates rather than updates** — you get duplicates.
- `takeFirst()` will not wrap an expression. `takeFirst(toLong(x))` returns **zero rows**
  rather than erroring.
- Results over 50 KB **spill to a file**; `result.records` is empty and `result.path` holds the
  data. Use `--spill=never` when reading inline.

---

## Operational

### Silent record loss is the default

A session published **7,266 contention records and 733 arrived**. Three causes:

1. Contention was emitted per event instead of aggregated.
2. The OTel SDK's exporter queue defaults to **2,048 records**, and a session publishes its
   whole result in one burst. Everything beyond that is dropped silently.
3. Our own diagnostics listener never attached — `EventListener`'s base constructor can raise
   `OnEventSourceCreated` *before* derived field initializers run, so the buffering list was
   null and the callback threw. The mechanism built to catch silent drops was itself silently
   broken.

**Reconcile published counts against what landed.** It is the only way we found any of this.
An earlier failure had the collector's OTLP receiver wired only to a traces pipeline: every log
record was rejected, the agent reported success because the SDK had queued them, and nothing
anywhere said otherwise.

### ConfigMap propagation is not instant

The gate is a mounted ConfigMap. Measured propagation to the kubelet's view: **95–100 seconds**,
on top of the reader's own reload interval.

Any control plane treating the gate as synchronous with its API response will intermittently
lose the opening seconds of every session.

### The two halves arrive minutes apart

eBPF appears within seconds — those samples already flow, the gate only decides whether they
ship. EventPipe takes 2–3 minutes more: capture, symbol rundown, then parsing ~40 MB of
nettrace.

Seeing one and not the other is normal, and looks exactly like a fault.

### TraceEvent writes a ~9x intermediate to disk

`TraceLog.CreateFromEventPipeDataFile` is not an in-memory path — it writes an `.etlx` at
roughly nine times the nettrace size. Size the sidecar's volume accordingly, and delete it;
leaking them fills the volume and the next session fails for a reason that looks nothing like
disk space.

### Cost is dominated by ingest, but queries are the tail risk

Ingest is ~94% of the bill, retention ~4%. But an unbounded query over a 7-day bucket at 100
pods is **$1.48 per run** — on a one-minute dashboard refresh, ~$2,100/day, which is more per
day than the entire pipeline costs per month.

Bound every query by `profile.session_id`. This is a click-to-open tool, not a wall display.

---

## Things we got wrong and reversed

Recorded because the reversals are the most useful content here:

| We assumed | Actually |
|---|---|
| eBPF gets kernel frames, EventPipe gets managed ones | eBPF gets managed frames too, at 100% |
| One record per unique stack | Grain must be (stack, thread) |
| Davis events can be attached to a problem | They cannot; annotations are the mechanism |
| `dotnet-monitor` has a musl image | It has not since 7.3.4 |
| Session gating needs a new gateway component | Writing one ConfigMap gates both halves |
| The broker must start EventPipe per pod | The agent watches the gate itself |
| Attributes cap near 2,500 bytes | 32,768, and truncation is silent |
| Contention events carry stacks | Only `ContentionStart` does |

The design got **smaller** every time it met reality. That is the pattern worth carrying into
your own adoption: build the smallest thing that could work, then measure what it actually
does.
