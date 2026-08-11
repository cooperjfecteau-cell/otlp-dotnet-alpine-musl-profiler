# 04 — dotnet-monitor on Alpine/musl and arm64

Resolves wayfinder research ticket
[#4](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/4).
Feeds build tickets #14 and #19.

Target: Kubernetes 1.31 on EKS, t4g.large, **arm64 (aarch64)**, Amazon Linux 2023,
kernel 6.1.176. Profiled app on `mcr.microsoft.com/dotnet/aspnet:9.0-alpine` (musl).

---

## Verdict

**There is no Alpine/musl `dotnet-monitor` image, and there has not been one since
7.3.4.** The last Alpine tag Microsoft published is `7.3.4-alpine-arm64v8`. Every
currently-supported line (8.1, 9.0, 10.0, `latest`) ships **only** Azure Linux 3.0
distroless or Ubuntu 22.04 chiseled. Both are glibc. Do not go looking for
`10.0-alpine`; it does not exist and is not coming.

**This does not block the design.** The sidecar never links against the application's
libc. The boundary between them is a Unix domain socket carrying the .NET diagnostic
IPC binary protocol — a byte stream, not an ABI. A glibc Azure Linux sidecar drives an
EventPipe session inside a musl Alpine application without any compatibility shim. The
one dotnet-monitor feature that *would* break across that boundary is memory dumps
(`/dump`, `/gcdump`), which need matching libc/OS to be analysed. **We collect traces,
not dumps.** That feature is unused and should be switched off.

**arm64 is fully supported.** Every current tag is a two-platform manifest list with
`linux/amd64` and `linux/arm64`. Verified below against the registry, not against docs
prose.

**Headline configuration:** dotnet-monitor runs as a **native sidecar** (init container
with `restartPolicy: Always`) in `DiagnosticPort.ConnectionMode: Listen`, owning
`/diag/dotnet-monitor.sock` on a shared `emptyDir`. The app sets
`DOTNET_DiagnosticPorts=/diag/dotnet-monitor.sock,nosuspend`, which puts the runtime in
its only supported role on CoreCLR — **connect** (reverse). dotnet-monitor binds
`http://127.0.0.1:52323` with `--no-auth`; loopback in a pod network namespace is
reachable only from inside the pod, so the broker never talks to it directly — our
profile-agent container in the same pod does, and that is where the broker-facing auth
lives. The 90-second window is a single
`POST /trace?uid=<uid>&durationSeconds=90` with an explicit provider list; the nettrace
comes back as a chunked `application/octet-stream` body.

---

## 1. Image inventory — verified against the registry

Method (all anonymous, no Docker daemon):

```powershell
# full tag list
curl.exe -s https://mcr.microsoft.com/v2/dotnet/monitor/tags/list
# platform coverage of a tag
curl.exe -s -H "Accept: application/vnd.docker.distribution.manifest.list.v2+json" `
  https://mcr.microsoft.com/v2/dotnet/monitor/manifests/10.0
```

Cross-checked against the build definitions in `dotnet/dotnet-docker` (`src/monitor/**`,
`src/monitor-base/**`), which are the ground truth for what gets built at all.

### What exists today

| Tag | Base OS | libc | linux/amd64 | linux/arm64 |
|---|---|---|---|---|
| `latest`, `10`, `10.0`, `10.0.3` | Azure Linux 3.0 distroless | glibc | yes | **yes** |
| `9`, `9.0`, `9.0.5` | Azure Linux 3.0 distroless | glibc | yes | **yes** |
| `8`, `8.1`, `8.1.3` (= `-ubuntu-chiseled`) | Ubuntu 22.04 chiseled | glibc | yes | **yes** |
| `8.1.3-azurelinux-distroless` | Azure Linux 3.0 distroless | glibc | yes | yes |
| **any `-alpine`** | — | — | **does not exist above 7.3.4** | **does not exist above 7.3.4** |

Confirmed manifest lists (both platforms present, no `os.version`, no variant):

```
10.0     -> linux/amd64 sha256:dd8a0620…  linux/arm64 sha256:0276678f…
8.1.3    -> linux/amd64 sha256:fda9b4a4…  linux/arm64 sha256:b81bca15…
7.3.4-alpine -> linux/amd64 sha256:a41a284a…  linux/arm64 sha256:fd8733cb…
```

The directory listing in `dotnet/dotnet-docker` is unambiguous — there is no Alpine
variant to build:

```
src/monitor/10.0/azurelinux-distroless/{amd64,arm64v8}/Dockerfile
src/monitor/9.0/azurelinux-distroless/{amd64,arm64v8}/Dockerfile
src/monitor/8.1/azurelinux-distroless/{amd64,arm64v8}/Dockerfile
src/monitor/8.1/ubuntu-chiseled/{amd64,arm64v8}/Dockerfile
```

### Alpine died with the 7.x line

Alpine tags run `6.0.0` → `7.3.4`, arm64 arriving at `6.2`. The 8.x line replaced the
Alpine variant with Ubuntu chiseled and Azure Linux distroless and never brought it
back. Nothing in the repo announces this; it is visible only in the tag list and the
build tree, which is why this ticket verified rather than read.

### Microsoft still builds dotnet-monitor components for musl-arm64

Worth recording, because it changes the cost of the fallback if we ever need one. The
egress extension tarballs are published per-RID, and `linux-musl-arm64` is among them:

```
GET .../monitor/9.0.5/dotnet-monitor-egress-s3storage-9.0.5-linux-musl-arm64.tar.gz -> 200
GET .../monitor/9.0.5/dotnet-monitor-egress-s3storage-9.0.5-linux-arm64.tar.gz       -> 200
```

The `dotnet-monitor` .NET global tool is also on NuGet (latest `10.0.3`), and the
dotnet-monitor repo carries a `.devcontainer/musl/Dockerfile` — they develop and test on
musl. So "no musl image" is a packaging decision, not a portability limit.

### Fallback if a musl sidecar is ever mandated

Only relevant if an adopter's policy forbids non-Alpine images in a pod. Ranked:

1. **Use the glibc image (recommended, and what this reference implementation does).**
   The sidecar is *our* container; only the app image is the adopter's. There is no
   technical reason for them to match.
2. **Build our own Alpine sidecar**: `FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine-arm64v8`
   plus `dotnet tool install --tool-path /app dotnet-monitor`. The tool is portable IL
   and runs on any RID with a matching runtime. Costs us a build pipeline and CVE
   surface we would otherwise inherit from Microsoft.
3. **Drop dotnet-monitor entirely** and have our profile-agent use
   `Microsoft.Diagnostics.NETCore.Client` (`DiagnosticsClient` + `EventPipeSession`)
   against the same socket. Removes the HTTP API, the auth question, and one container.
   See §9.

### Version to pin

Pin `mcr.microsoft.com/dotnet/monitor:10.0.3`. The 9.x line tracks .NET 9, which left
support in May 2026. dotnet-monitor 10 monitors **.NET Core 3.1 and .NET 5+** targets —
its own runtime version and the target's are independent — so a .NET 10-based sidecar
monitoring a .NET 9 app is the supported configuration, not a stretch.

---

## 2. Diagnostic port: listen vs connect

Two independent role settings that are easy to confuse because both use the word
"connect".

**dotnet-monitor's role** (`DiagnosticPort.ConnectionMode`):

| Mode | Who listens | Feature support |
|---|---|---|
| `Connect` (default) | the **app**, on `/tmp/dotnet-diagnostic-{pid}-{starttime}-socket` | `/trace` works, but **no** collection rules, triggers, `/stacks`, `/exceptions`, `/parameters` |
| `Listen` | **dotnet-monitor**, on a path we choose | everything |

**The runtime's role** (`DOTNET_DiagnosticPorts`): on CoreCLR there is only one option.
From the .NET diagnostic-port reference:

> The complete syntax for a port is `address[,(listen|connect)][,(suspend|nosuspend)]`.
> `connect` is the default if neither `connect` or `listen` is specified (and `listen` is
> only supported by the Mono runtime on Android or iOS). `suspend` is the default if
> neither `suspend` or `nosuspend` is specified.

So on linux-musl-arm64 CoreCLR, `DOTNET_DiagnosticPorts` always means **the runtime
dials out**. The sidecar must therefore be the listener. `Listen` is not optional for us
— it is the only mode where the socket sits on a path both containers can agree on
ahead of time, and it is the only mode where a `pid`-based well-known path (which
differs per PID namespace) is irrelevant.

### Exact env var values

App container — one line, no `,listen`, no `,connect` (redundant):

```
DOTNET_DiagnosticPorts=/diag/dotnet-monitor.sock,nosuspend
```

Sidecar container:

```
DOTNETMONITOR_DiagnosticPort__ConnectionMode=Listen
DOTNETMONITOR_Storage__DefaultSharedPath=/diag
```

`Storage__DefaultSharedPath` (7.0+) makes `DiagnosticPort__EndpointName` unnecessary: the
combination of a default shared path plus `Listen` auto-creates a socket named
**`dotnet-monitor.sock`** under that path. Set it explicitly only if overriding the
default name. Multiple ports are semicolon-delimited; we need one.

### suspend vs nosuspend

| | `suspend` (the default!) | `nosuspend` |
|---|---|---|
| App startup | blocks until dotnet-monitor connects **and** issues resume | proceeds immediately |
| Startup triggers | full | partial — may miss startup events |
| `/parameters` API | supported | unsupported |
| Failure mode | sidecar crashloop ⇒ **app never starts** | sidecar down ⇒ profiling unavailable, app fine |

**Use `nosuspend`.** Profiling here is on-demand and optional; it must never be able to
take the workload down. We do not use startup triggers (the broker is the trigger) and
we do not use `/parameters`. The runtime retries the connection indefinitely, so the
sidecar can restart under the app without intervention.

Note that `suspend` is the default when the modifier is omitted — omitting it is a
latent outage, so always write it out.

### The default diagnostic port stays open

`DOTNET_DiagnosticPorts` adds ports; it does not replace the default one. The app still
listens on `/tmp/dotnet-diagnostic-{pid}-{starttime}-socket`, so `dotnet-trace`/
`dotnet-dump` attached via `kubectl exec` still work for debugging. Do not set
`DOTNET_EnableDiagnostics=0` on the app container — it would kill both.

---

## 3. Shared volume layout

```
volumes:
  - name: diagvol
    emptyDir: {}          # default medium; see sizing note
```

Mounted at `/diag` in **both** containers. Contents once running:

```
/diag/dotnet-monitor.sock        # UDS, created by dotnet-monitor at startup
/diag/artifacts/                 # only if FileSystem egress is configured (§5)
```

### Permissions — the pitfalls, and why none of them bite by default

**emptyDir mode.** kubelet creates emptyDir directories at **0777** and explicitly
`chmod`s them back to 0777 if a kubelet umask interfered
(`pkg/volume/emptydir/empty_dir.go`, `const perm os.FileMode = 0777`, verified on
`release-1.31`). No `fsGroup` needed for the sidecar to create the socket.

**UID alignment.** Both images already run as the same non-root user:

- `mcr.microsoft.com/dotnet/aspnet:9.0-alpine-arm64v8` → `APP_UID=1654`, user/group `app`
  created with `--uid=1654 --gid=1654` in the Alpine runtime-deps layer.
- `mcr.microsoft.com/dotnet/monitor:10.0` → based on the Azure Linux distroless aspnet
  image, which ends with `USER $APP_UID` where `APP_UID=1654`.

Connecting to a UDS requires **write** permission on the socket file. Because both sides
are uid 1654, the socket dotnet-monitor creates is already owned by the app's uid.
dotnet-monitor's own docs put it plainly: *"Starting with .NET 8.0, both the sample
ASP.NET application and dotnet-monitor run as non-root. If both the application and
dotnet-monitor are 8+, no additional configuration is required."*

**Where it breaks, and the fix.** All of these are real and all are adopter-side:

| Situation | Symptom | Fix |
|---|---|---|
| Adopter overrides `runAsUser` on the app only | app cannot `connect()` to the socket | set the same `runAsUser` on both containers, or set pod-level `fsGroup: 1654` |
| Adopter's app image is pre-.NET-8 or custom-built as root | uid mismatch | pin both to `runAsUser: 1654` |
| A restrictive PSA/Kyverno policy forces a random uid | socket unusable | pod-level `securityContext.fsGroup` + `runAsUser` applied uniformly |
| `readOnlyRootFilesystem: true` on the sidecar | fine — `/diag` is a writable mount | no change |

Belt and braces, and what the pod spec below does: set `runAsUser`/`runAsGroup`/`fsGroup`
to 1654 explicitly at pod level. It costs nothing and removes the whole class of bug.

**Stale sockets.** The image ships `DiagnosticPort__DeleteEndpointOnStartup=true`, so a
sidecar restart cleans up its own socket. Leave it on.

**Sizing.** A default-medium `emptyDir` draws from node ephemeral storage. If FileSystem
egress is used, a 90s trace of a busy service is tens to low hundreds of MB — set
`sizeLimit` and an ephemeral-storage limit, or stream over HTTP and never touch disk.

---

## 4. Container startup ordering

Use a **native sidecar**: dotnet-monitor as an entry in `initContainers` with
`restartPolicy: Always`. The `SidecarContainers` feature gate has been enabled by default
since Kubernetes **1.29** (GA in 1.33), so it is available on our 1.31 cluster.

Why it matters:

- The sidecar is fully started **before** the app container starts, so the socket exists
  by the time the runtime first dials.
- On pod termination, sidecars are stopped **after** the main container, so an in-flight
  trace is not cut off by the app going away first.
- Sidecars do not block Job completion — relevant if anyone profiles a Job.

`nosuspend` makes ordering non-fatal either way; the native sidecar just removes the
first-connection retry delay.

---

## 5. The on-demand 90-second collection

### Endpoints

| Call | Route |
|---|---|
| Discover the target | `GET /processes` |
| Start a fixed-duration trace, custom providers | `POST /trace?uid={uid}&durationSeconds={n}` |
| Start with a built-in profile | `GET /trace?uid={uid}&profile=Cpu&durationSeconds={n}` |
| Operation status | `GET /operations/{operationId}` |
| Graceful stop (7.1+) | `DELETE /operations/{operationId}?stop=true` |
| Cancel | `DELETE /operations/{operationId}` |

`POST /trace` query parameters: `pid` (int), `uid` (guid), `name` (string),
`durationSeconds` (int, default **30**, min `-1` = indefinite, max 2147483647),
`egressProvider` (string), `tags` (string, 7.1+). All optional; with none of
`pid`/`uid`/`name` it targets the *default process*.

The image sets `DefaultProcess__Filters__0__Key=ProcessId` /
`…__Value=1`, so the default process is whatever reports PID 1. That is usually the app,
but **not** if the adopter's image wraps the entrypoint in a shell. Resolve explicitly:

```
GET /processes  ->  [ { "pid": 1, "uid": "cd4da319-fa9e-4987-ac4e-e57b2aac248b" } ]
```

then pass `uid`. `uid` (runtime instance id) is stable across the trace; `pid` can be
reused after a restart.

### Getting the nettrace bytes out

**Primary — stream over HTTP.** Omit `egressProvider` and the trace is written to the
response body:

```
HTTP/1.1 200 OK
Content-Type: application/octet-stream
Transfer-Encoding: chunked
Location: localhost:52323/operations/67f07e40-5cca-4709-9062-26302c484f18
```

The profile-agent pipes that stream straight into TraceEvent's `EventPipeEventSource` —
nettrace never touches disk. This is the shape the design assumes.

**Alternative — FileSystem egress into the shared volume.** Configure a named provider
and the response becomes `202 Accepted` + `Location`:

```
DOTNETMONITOR_Egress__FileSystem__shared__directoryPath=/diag/artifacts
DOTNETMONITOR_Egress__FileSystem__shared__intermediateDirectoryPath=/diag/artifacts-tmp
```

then `POST /trace?uid=…&durationSeconds=90&egressProvider=shared`, poll
`GET /operations/{id}` until `status: Succeeded`, and read `resourceLocation`. Use this
if the artifact must survive a profile-agent restart, or to decouple collection from
export. It writes to the intermediate path first and renames, so a reader watching
`/diag/artifacts` never sees a partial file.

### Two timing traps — both matter for the broker contract

**1. `durationSeconds` is the only way to bound the window.** From the API docs:

> When setting `durationSeconds` to `-1` (indefinite duration), there is currently no way
> to terminate the trace operation that preserves the `.nettrace` file in an accessible
> format. This also applies when prematurely terminating a trace operation that uses a
> finite value for `durationSeconds`.

So the pattern "start indefinite, stop after 90s" **does not work** — it yields no usable
trace. Always pass `durationSeconds=90` up front. `DELETE …?stop=true` is a cancel path
for a session we have decided to abandon, not the normal stop.

**2. The request outlives the window.** Also from the docs:

> After the expiration of the trace duration, completing the request may take a long time
> (up to several minutes) for large applications if
> `EventProvidersConfiguration.RequestRundown` is set to `true`.

Rundown is the runtime replaying its method/type cache so TraceEvent can turn method IDs
into names. **Without it there are no symbols, so no flame graph.** It is on by default
and we want it. Consequence for the build: the profile-agent's HTTP client timeout must
be `duration + generous rundown margin` (start at 5 minutes, measure), and the broker
must treat "collection started" and "artifact ready" as separate states rather than
blocking a Dynatrace workflow for the whole time.

### Worked 90-second call

From the profile-agent container, over pod loopback:

```sh
# 1. resolve the runtime instance
UID=$(curl -sf http://127.0.0.1:52323/processes | jq -r '.[0].uid')

# 2. collect 90s, stream nettrace to stdout
curl -sf -X POST \
  "http://127.0.0.1:52323/trace?uid=${UID}&durationSeconds=90&tags=session-${SESSION_ID}" \
  -H 'Content-Type: application/json' \
  --max-time 600 \
  --data @providers.json \
  --output "/tmp/${SESSION_ID}.nettrace"
```

`tags` (7.1+) stamps the broker's session id onto the operation so
`GET /operations?tags=session-…` can find it later — use it, it is free correlation.

---

## 6. Authentication

### What dotnet-monitor offers

| Mode | Fit here |
|---|---|
| **API key** (`MonitorApiKey`) — a signed JWT; `Authorization: Bearer <jwt>`; configured as `Subject` + `PublicKey`, generated by `dotnet monitor generatekey` | works, but "only use when TLS is enabled" — so it drags in cert management |
| **Azure AD / Entra** | not applicable on EKS |
| **Windows / Negotiate** | Windows only |
| **`--no-auth`** | disables auth on the artifact URLs entirely |

Auth is never enforced on the metrics endpoint (default `http://localhost:52325`).

### Recommendation: `--no-auth` bound to loopback, and the broker never touches it

```
args: ["collect", "--no-auth"]
DOTNETMONITOR_Urls=http://127.0.0.1:52323
DOTNETMONITOR_Metrics__Enabled=false
```

The reasoning, because `--no-auth` is otherwise a red flag:

- Containers in a pod share a network namespace, so `127.0.0.1:52323` is reachable from
  the app and profile-agent containers **and from nothing else**. It is not exposed by
  any Service, it is not routable from another pod, and it is not reachable from the
  node. The broker *cannot* reach it even if it wanted to.
- The threat model dotnet-monitor's warning addresses is *"anything with access to the
  URLs can capture dumps of any process dotnet-monitor can see"*. Here the only things
  with access are two containers that are already inside the same trust boundary as the
  process being profiled. An attacker who can execute in the app container already has
  the memory.
- The broker-facing surface is our profile-agent's own endpoint. That is where
  authentication belongs, and it is already an open question on the map ("Broker
  authentication"). One authenticated hop is better than two, and it keeps the secret in
  code we control rather than in a JWT keypair we have to rotate.
- Because we override `CMD`, `--urls` from the image default (`https://+:52323`) is lost.
  **This must be replaced via `DOTNETMONITOR_Urls`** or dotnet-monitor binds nothing
  useful. The same applies to `--metricUrls`; disabling metrics outright is cleaner than
  re-specifying it.

Hardening that costs nothing and should be in the reference implementation:

- Explicitly `127.0.0.1`, never `+` or `0.0.0.0`, in `DOTNETMONITOR_Urls`.
- A default-deny `NetworkPolicy` on the namespace so the blast radius of a future
  misconfiguration is bounded.
- `--no-http-egress` is **not** wanted — we rely on the HTTP response stream.

### When to switch to API keys

If an adopter needs the broker to call dotnet-monitor directly (no profile-agent in the
pod), then bind `https://+:52323`, provision `MonitorApiKey`, and mount the key as a
**volume** — dotnet-monitor only supports automatic key rotation for secrets mounted as
files, not env vars:

```sh
kubectl create secret generic apikey \
  --from-literal=Authentication__MonitorApiKey__Subject='…' \
  --from-literal=Authentication__MonitorApiKey__PublicKey='…'
# mounted at /etc/dotnet-monitor
```

Record this as the documented alternative; do not build it first.

---

## 7. EventPipe providers for CPU, GC, and contention

### The two providers that matter

**`Microsoft-DotNETCore-SampleProfiler`** — CPU samples. Per Microsoft Learn: *"a .NET
runtime event provider that is used for CPU sampling for managed callstacks. When
enabled, it captures a snapshot of each thread's managed callstack every millisecond. To
enable this capture, you must specify an EventLevel of `Informational` or higher."* No
keywords. The 1 ms interval is **fixed** on .NET 9 — `DOTNET_EventPipeThreadSamplingRate`
only lands in .NET 11 — so per-thread sampling cost is not tunable and belongs in the
overhead measurement the map already flags as unspecified.

**`Microsoft-Windows-DotNETRuntime`** — GC, contention, threading, and the JIT/loader
events TraceEvent needs to resolve symbols. Keywords (from
`ClrTraceEventParser.Keywords`, TraceEvent):

| Keyword | Hex | Gives us |
|---|---|---|
| `GC` | `0x1` | GC start/stop, generations, pause durations, suspension |
| `Loader` | `0x8` | module load — needed for symbol resolution |
| `Jit` | `0x10` | JIT method events — **required** to name managed frames |
| `NGen` | `0x20` | precompiled image loads |
| `StopEnumeration` | `0x80` | rundown of existing methods at session end |
| `Contention` | `0x4000` | **`ContentionStart`/`ContentionStop`** — lock contention, with `DurationNs` |
| `Threading` | `0x10000` | thread pool and threading events |
| `JittedMethodILToNativeMap` | `0x20000` | IL↔native map, improves frame attribution |
| `OverrideAndSuppressNGenEvents` | `0x40000` | (`SupressNGen`) |
| `Type` | `0x80000` | `BulkType` |
| `GCHeapSurvivalAndMovement` | `0x400000` | object survival/movement per GC — **verbose** |
| `GCHeapAndTypeNames` | `0x1000000` | type names in events rather than tokens |
| `Stack` | `0x40000000` | attach call stacks to events |
| `ThreadTransfer` | `0x80000000` | thread pool enqueue/dequeue |
| `Codesymbols` | `0x400000000` | PDBs of dynamically generated assemblies |
| `Compilation` | `0x1000000000` | compilation information |

`Keywords.Default` is the OR of every row above = **`0x14C14FCCBD`** — the value in
Microsoft's own `/trace` example. It already contains GC, Contention, Threading, Stack
and the full JIT/loader symbol set, which is exactly the union this ticket asked for.

`dotnet-monitor`'s built-in `Cpu` profile is precisely
`SampleProfiler@Informational` + `Microsoft-Windows-DotNETRuntime@Informational` with
`Keywords.Default` (`CpuProfileConfiguration.cs`), so `GET /trace?profile=Cpu` and the
explicit body below are equivalent. Use the explicit body anyway: it pins the keywords
into our repo instead of inheriting whatever a future dotnet-monitor decides `Cpu` means,
and it lets us set `BufferSizeInMB`.

### Recommended provider body

`providers.json` — the balanced default. Drops `GCHeapSurvivalAndMovement` (`0x400000`),
which is the single most verbose bit in `Default` and buys nothing for a CPU/GC/contention
flame graph:

```json
{
  "Providers": [
    {
      "Name": "Microsoft-DotNETCore-SampleProfiler",
      "EventLevel": "Informational"
    },
    {
      "Name": "Microsoft-Windows-DotNETRuntime",
      "EventLevel": "Informational",
      "Keywords": "0x410F40B9"
    }
  ],
  "RequestRundown": true,
  "BufferSizeInMB": 128
}
```

`0x410F40B9` = `GC | Loader | Jit | NGen | StopEnumeration | Contention | Threading |
JittedMethodILToNativeMap | OverrideAndSuppressNGenEvents | Type | GCHeapAndTypeNames |
Stack`.

If a session looks lossy or a stack fails to resolve, fall back to the documented
`"0x14C14FCCBD"` before doing anything cleverer — it is the known-good value.

Notes:

- **`RequestRundown` must stay `true`.** It is the default. Without rundown, method IDs
  never resolve to names and the flame graph is unreadable. It is the reason the request
  runs past the 90-second mark (§5).
- **`BufferSizeInMB` is allocated inside the *application* process**, not the sidecar.
  Default is 256 (min 1, max 1024). 128 is a reasonable start for a 90 s window; the app
  container's memory limit must have that much headroom or the app gets OOMKilled by our
  profiling, which would be an embarrassing way to fail. If events are dropped, raise the
  buffer or narrow the keywords — do not raise it blindly.
- **Level.** `Informational` (4) throughout. `Verbose` (5) on
  `Microsoft-Windows-DotNETRuntime` turns on per-allocation and JIT-tracing floods and is
  not survivable under load for 90 s.
- **Contention stacks.** `ContentionStop` carries `DurationNs`, so blocked time is
  measurable rather than inferred. `Stack` is set, and EventPipe additionally associates a
  stack with events in the nettrace stream, so contention frames land in the same fold as
  CPU samples.
- **GC-only variant.** dotnet-monitor's `GcCollect` profile is
  `Microsoft-Windows-DotNETRuntime` + `Microsoft-Windows-DotNETRuntimePrivate`, both at
  `Informational` with keyword `GC` (`0x1`), and it narrows rundown to the GC keyword. Use
  it if a session ever needs to be GC-only and cheap.

---

## 8. Pod spec fragment

Ready to adapt. `PLACEHOLDER` values are the adopter's.

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: PLACEHOLDER-app
spec:
  replicas: 1
  selector:
    matchLabels: { app: PLACEHOLDER-app }
  template:
    metadata:
      labels: { app: PLACEHOLDER-app }
    spec:
      # arm64 nodes only — the whole toolchain is linux/arm64 here.
      nodeSelector:
        kubernetes.io/arch: arm64

      # Pin the uid across every container so the diagnostic socket on the shared
      # emptyDir is writable by both sides. 1654 is the 'app' user baked into both
      # the .NET Alpine runtime images and the dotnet-monitor distroless image;
      # setting it explicitly stops an adopter's PodSecurity policy from silently
      # breaking the socket.
      securityContext:
        runAsNonRoot: true
        runAsUser: 1654
        runAsGroup: 1654
        fsGroup: 1654

      initContainers:
        # Native sidecar (initContainers + restartPolicy: Always, beta-by-default
        # since k8s 1.29). Starting here rather than in `containers` guarantees the
        # diagnostic socket exists before the app's runtime first dials it, and that
        # the sidecar outlives the app during termination so an in-flight trace is
        # not truncated.
        - name: dotnet-monitor
          image: mcr.microsoft.com/dotnet/monitor:10.0.3
          restartPolicy: Always
          imagePullPolicy: IfNotPresent

          # Overriding args discards the image's CMD, which is where --urls and
          # --metricUrls live. They are re-specified via env below; forgetting that
          # leaves dotnet-monitor bound to nothing.
          # --no-auth is safe *only* because Urls is loopback: a pod's network
          # namespace makes 127.0.0.1 unreachable from outside the pod. The broker
          # never talks to this port; it talks to profile-agent.
          args: ["collect", "--no-auth"]

          env:
            # Listen mode: dotnet-monitor owns the socket and the app dials out.
            # On CoreCLR the runtime can only ever be the connecting side, so this
            # is the only workable arrangement.
            - name: DOTNETMONITOR_DiagnosticPort__ConnectionMode
              value: Listen
            # 7.0+: DefaultSharedPath + Listen auto-creates /diag/dotnet-monitor.sock,
            # so DiagnosticPort__EndpointName is unnecessary.
            - name: DOTNETMONITOR_Storage__DefaultSharedPath
              value: /diag
            - name: DOTNETMONITOR_Urls
              value: http://127.0.0.1:52323
            # We ship metrics via OTLP, not via dotnet-monitor's Prometheus endpoint.
            - name: DOTNETMONITOR_Metrics__Enabled
              value: "false"
            - name: DOTNETMONITOR_DiagnosticPort__MaxConnections
              value: "1"

          volumeMounts:
            - { name: diagvol, mountPath: /diag }

          securityContext:
            allowPrivilegeEscalation: false
            readOnlyRootFilesystem: true      # /diag is a writable mount
            capabilities: { drop: ["ALL"] }

          # Microsoft's published minimums. Traces push memory into the *app*
          # process (BufferSizeInMB), not into this container.
          resources:
            requests: { cpu: 50m,  memory: 32Mi }
            limits:   { cpu: 250m, memory: 256Mi }

      containers:
        # ---- the adopter's application, unmodified except for two additions ----
        - name: app
          image: PLACEHOLDER/app:TAG   # e.g. mcr.microsoft.com/dotnet/aspnet:9.0-alpine
          env:
            # The only change the adopter makes to their app. 'nosuspend' is
            # load-bearing: the default is 'suspend', which would block the app's
            # startup until dotnet-monitor connects and resumes it — turning a
            # sidecar crashloop into an application outage.
            - name: DOTNET_DiagnosticPorts
              value: /diag/dotnet-monitor.sock,nosuspend
          volumeMounts:
            - { name: diagvol, mountPath: /diag }
          resources:
            requests: { cpu: 100m, memory: 256Mi }
            # Headroom above the app's own working set for the EventPipe buffer
            # (BufferSizeInMB, allocated in this process) during a collection.
            limits:   { cpu: 1,    memory: 768Mi }

        # ---- ours: broker-facing API, nettrace -> folded stacks -> OTLP logs ----
        - name: profile-agent
          image: PLACEHOLDER/profile-agent:TAG
          ports:
            - { name: broker, containerPort: 8081 }
          env:
            - name: DOTNET_MONITOR_URL
              value: http://127.0.0.1:52323
            - name: PROFILE_DURATION_SECONDS
              value: "90"
            # Must exceed duration + rundown; rundown can add minutes on large apps.
            - name: DOTNET_MONITOR_TIMEOUT_SECONDS
              value: "600"
            - name: POD_NAME
              valueFrom: { fieldRef: { fieldPath: metadata.name } }
            - name: POD_UID
              valueFrom: { fieldRef: { fieldPath: metadata.uid } }
            - name: NODE_NAME
              valueFrom: { fieldRef: { fieldPath: spec.nodeName } }
          # profile-agent does NOT mount diagvol while traces stream over HTTP.
          # Add the mount only if FileSystem egress is adopted (see §5).
          resources:
            requests: { cpu: 100m, memory: 128Mi }
            limits:   { cpu: 1,    memory: 512Mi }

      volumes:
        - name: diagvol
          emptyDir:
            sizeLimit: 512Mi
```

---

## 9. For the build tickets

Settled and safe to build on:

1. Pin `mcr.microsoft.com/dotnet/monitor:10.0.3`. Do not look for an Alpine tag.
   Document in the adoption guide that the sidecar is glibc and that this is fine.
2. `Listen` + `nosuspend` + `/diag/dotnet-monitor.sock` on a shared `emptyDir`.
3. Native sidecar (`initContainers` + `restartPolicy: Always`).
4. `POST /trace?uid=…&durationSeconds=90` with the pinned provider body; resolve `uid`
   from `GET /processes` rather than trusting the PID-1 default filter.
5. Loopback bind + `--no-auth`; broker auth lives on profile-agent.
6. `tags=session-{id}` on every trace request for correlation.

Carry forward as risks, not blockers:

- **Rundown latency is unbounded in principle.** Needs measuring against a
  representative app before the broker's state machine is finalised. Feeds the map's
  "Sampling overhead" gap.
- **`BufferSizeInMB` is charged to the app's memory limit.** An adopter with a tight
  limit can be OOMKilled by our profiling. The adoption doc must state the required
  headroom, and 128 MB is a starting guess, not a measurement.
- **Concurrent sessions.** `DiagnosticPort__MaxConnections: 1` above assumes one app
  process per pod. dotnet-monitor returns `429 Too Many Requests` when trace requests
  pile up — the broker needs to handle that. Ties to the map's open "Concurrent and
  overlapping sessions" question.
- **Adopters who wrap their entrypoint in a shell** break the PID-1 default process
  filter. Resolving via `/processes` covers it; the adoption doc should say why.

Worth a spike before committing to the two-container shape: **drop dotnet-monitor and
talk to the socket directly.** `Microsoft.Diagnostics.NETCore.Client`
(`DiagnosticsClient` + `EventPipeSession`) can start the same EventPipe session over the
same reverse-connect socket, in-process with the TraceEvent parsing we are already doing.
That would remove one container, the HTTP API, the auth question, and the
`--no-auth`-in-a-reference-implementation optics — at the cost of owning the reverse
connection handshake ourselves and losing dotnet-monitor's triggers and operation
tracking (neither of which this design uses). The app-side configuration
(`DOTNET_DiagnosticPorts`, shared emptyDir, uid alignment) is identical either way, so
this decision can be deferred without rework.

---

## Sources

Registry, queried directly (not read from docs):

- `https://mcr.microsoft.com/v2/dotnet/monitor/tags/list`
- `https://mcr.microsoft.com/v2/dotnet/monitor/manifests/{10.0,8.1.3,7.3.4-alpine}` with
  the manifest-list `Accept` header
- `https://builds.dotnet.microsoft.com/dotnet/diagnostics/monitor/9.0.5/…-linux-musl-arm64.tar.gz`
  (HTTP 200 for egress extensions)
- `https://api.nuget.org/v3-flatcontainer/dotnet-monitor/index.json`

Build definitions:

- `dotnet/dotnet-docker` — `src/monitor/**`, `src/monitor-base/9.0/azurelinux-distroless/arm64v8/Dockerfile`
  (ENTRYPOINT/CMD/env), `src/runtime-deps/9.0/alpine3.24/arm64v8/Dockerfile` and
  `src/runtime-deps/9.0/azurelinux3.0-distroless/arm64v8/Dockerfile` (`APP_UID=1654`)

dotnet/dotnet-monitor documentation:

- `documentation/kubernetes.md`, `samples/Kubernetes/deployment.yaml`
- `documentation/configuration/diagnostic-port-configuration.md`
- `documentation/configuration/storage-configuration.md`
- `documentation/configuration/egress-configuration.md`, `documentation/egress.md`
- `documentation/api/trace-get.md`, `trace-custom.md`, `processes-list.md`,
  `operations-get.md`, `operations-list.md`, `operations-stop.md`, `definitions.md`
- `documentation/authentication.md`, `documentation/api-key-setup.md`
- `src/Microsoft.Diagnostics.Monitoring.WebApi/Utilities/TraceUtilities.cs`,
  `Models/TraceProfile.cs`

dotnet/diagnostics:

- `src/Microsoft.Diagnostics.Monitoring.EventPipe/Configuration/CpuProfileConfiguration.cs`
- `src/Microsoft.Diagnostics.Monitoring.EventPipe/Configuration/GcCollectConfiguration.cs`

microsoft/perfview:

- `src/TraceEvent/Parsers/ClrTraceEventParser.cs` — `Keywords` enum and `Default`

Microsoft Learn:

- [Diagnostic port](https://learn.microsoft.com/dotnet/core/diagnostics/diagnostic-port)
- [Well-known event providers in .NET](https://learn.microsoft.com/dotnet/core/diagnostics/well-known-event-providers)

Kubernetes:

- [Sidecar containers](https://kubernetes.io/docs/concepts/workloads/pods/sidecar-containers/)
- `kubernetes/kubernetes` `release-1.31` — `pkg/volume/emptydir/empty_dir.go`
