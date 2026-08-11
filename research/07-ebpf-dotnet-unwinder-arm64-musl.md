# 07 — Does the eBPF profiler's .NET unwinder resolve managed frames on Alpine/musl, arm64?

**Ticket:** [#7](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/7)
**Date run:** 2026-08-11
**Method:** live experiment on EKS, not a literature review.

---

## Verdict

**Managed .NET frames are RESOLVED — fully, by name, on Alpine/musl + arm64.**

Not partially. Not "some". The complete managed portion of the stack came back symbolized,
in order, leaf to root, for both a purpose-built test workload and — corroborating,
unplanned — three pre-existing ASP.NET Core services on the same node that also turned out
to be musl-based.

What is *not* resolved is **native** userspace code (musl libc, CoreCLR's own C++, the
apphost, `libhostfxr`/`libhostpolicy`). Those frames are correctly **unwound** — right
module, right file-relative offset, GNU build ID attached — but not **symbolized** by the
agent. That is by design and is not musl-specific: the agent never symbolizes native ELF
locally, it ships module + offset + build ID and defers symbolization to the backend.
Kernel frames *are* symbolized locally (kallsyms), as are Go binaries.

Confidence: **high.** Direct observation, two independent workloads, raw OTLP output
inspected frame by frame.

---

## What was actually run

### Cluster

| | |
|---|---|
| Cluster | `eks-arm-new`, EKS 1.31 (`v1.31.14-eks-8f14419`), us-east-1 |
| Node used | `ip-192-168-44-91.ec2.internal`, t4g.large |
| Arch / kernel | aarch64, `6.1.176-221.360.amzn2023.aarch64` |
| OS / runtime | Amazon Linux 2023.12.20260710, containerd 2.2.4 |
| Prereqs | `/sys/kernel/btf/vmlinux` present, tracefs present, `perf_event_paranoid=2`, `unprivileged_bpf_disabled=1` → ran `privileged: true` |

### Workload under test

Namespace `probe-ebpf`, pod `burner`.

```
image:  mcr.microsoft.com/dotnet/sdk:9.0-alpine
digest: sha256:a63e271360552d87b556a2c1a78cfdfd2519ec33137acdce9ac16cff939f1ac0
```

Confirmed musl/arm64 from inside the container at runtime:

```
NAME="Alpine Linux"
VERSION_ID=3.23.5
aarch64
musl libc (aarch64)
Version 1.2.5
...
burner pid=1 arch=Arm64 rid=linux-musl-arm64 fw=.NET 9.0.18
```

The program is a deliberately deep, distinctly-named managed call chain compiled with
`[MethodImpl(MethodImplOptions.NoInlining)]` so that name resolution is unambiguous:

`Burner.Program.Main` → `QuixoticLevelOne` → `Two` → `Three` → `Four` → `Five` →
`PalindromeHotLoop` (the CPU-burning leaf: `Math.Sqrt`/`Sin`/`Cos` in a 4M-iteration loop).

Built in-container with `dotnet publish -c Release -o /out`, then `exec /out/burner` —
default JIT, tiered compilation on, no `DOTNET_PerfMapEnabled`, no NativeAOT, no
single-file. Runtime resolved to
`/usr/share/dotnet/shared/Microsoft.NETCore.App/9.0.18/libcoreclr.so`, linked against
`libc.musl-aarch64.so.1`.

### Profiler

**Finding, worth recording on its own: upstream `open-telemetry/opentelemetry-ebpf-profiler`
publishes no runnable agent image.** Its only image-publishing workflow
(`.github/workflows/push-docker-image.yml`) pushes `otel/opentelemetry-ebpf-profiler-dev`,
which is the *Debian build environment*, not the agent. The repo has no GitHub releases
(`/releases` returns `[]`), only date-stamped tags. `ghcr.io/open-telemetry/opentelemetry-ebpf-profiler`
does not exist (403 on the GHCR token endpoint; the same probe against a known-good repo
succeeds). A stale DaemonSet already on this cluster from earlier work,
`docker.io/otel/opentelemetry-ebpf-profiler:v0.0.0-20250801`, has been in
`ImagePullBackOff` for 20 days — independent confirmation that that path is a dead end.

**There is, however, an official prebuilt distribution — from the *collector-releases*
repo, not the profiler repo:**

```
ghcr.io/open-telemetry/opentelemetry-collector-releases/opentelemetry-collector-ebpf-profiler:0.157.0
```

This is the shortest path for anyone reproducing this and is what the reference
implementation should use. (I discovered it only after starting a source build — it was
already running on this cluster from prior work. I completed the source build because it
let me pin an exact commit and add a file exporter.)

**What I actually ran** was built from source, natively on the arm64 node:

| | |
|---|---|
| Build image | `docker.io/otel/opentelemetry-ebpf-profiler-dev@sha256:585409de39191c201f91a8e6ec5294556c8f21c7d2ce0b0f96e838f27e399f82` |
| Source | `open-telemetry/opentelemetry-ebpf-profiler`, tag `v0.0.202632`, commit `af6a487f9f0fdec1d94e2fdc028dba3a6f04003b` (2026-08-03) |
| Target | `make otelcol-ebpf-profiler` (Collector distro with the `profiling` receiver embedded) |
| Collector core | v0.157.0 · Go 1.25.0 · GOARCH=arm64 |
| Exporters | `debug` (run 1), contrib `fileexporter` v0.157.0 added to `manifest.yaml` (run 2) |

`make otelcol-ebpf-profiler` builds cleanly on arm64 in that image with no patching. The
eBPF objects compiled for arm64 including both .NET programs:

```
kprobe/unwind_dotnet has 929 instructions
kprobe/unwind_dotnet10 has 919 instructions
perf_event/unwind_dotnet has 929 instructions
perf_event/unwind_dotnet10 has 919 instructions
```

Two gotchas worth carrying forward:

1. The profiles pipeline is gated. Without `--feature-gates=service.profilesSupport` the
   collector refuses to start:
   `Error: invalid configuration: service::pipelines: pipeline "profiles": profiling signal support is at alpha level, gated under the "service.profilesSupport" feature gate`
2. Rust is **not** needed. The `rust-components` target is separate; the dev image ships no
   `cargo` and the build does not want one.

### Manifests

Runner pod (privileged, `hostPID` so it sees the .NET process on the node):

```yaml
apiVersion: v1
kind: Pod
metadata: { name: ebpf-build, namespace: probe-ebpf }
spec:
  nodeName: ip-192-168-44-91.ec2.internal
  hostPID: true
  hostNetwork: true
  dnsPolicy: ClusterFirstWithHostNet
  restartPolicy: Never
  containers:
    - name: build
      image: otel/opentelemetry-ebpf-profiler-dev:latest
      command: ["/bin/bash", "-c", "sleep infinity"]
      securityContext: { privileged: true }
      volumeMounts:
        - { name: bpffs,   mountPath: /sys/fs/bpf, mountPropagation: Bidirectional }
        - { name: debugfs, mountPath: /sys/kernel/debug }
        - { name: work,    mountPath: /work }
  volumes:
    - { name: bpffs,   hostPath: { path: /sys/fs/bpf,       type: Directory } }
    - { name: debugfs, hostPath: { path: /sys/kernel/debug, type: Directory } }
    - { name: work,    emptyDir: {} }
```

Collector config (run 2, the one that produced the raw stacks):

```yaml
receivers:
  profiling:
    samples_per_second: 99
    reporter_interval: 15s
    monitor_interval: 5s
    no_kernel_version_check: true
exporters:
  file:
    path: /work/profiles.json
    format: json
service:
  pipelines:
    profiles: { receivers: [profiling], exporters: [file] }
```

Invocation:
`otelcol-ebpf-profiler --feature-gates=service.profilesSupport --config /work/otelcol2.yaml`,
75 s, 5 reporter batches, 2884 samples node-wide.

All interpreters are enabled by default (`interpreterconfig.AllInterpreters()`); no .NET-
specific flag was needed.

---

## Raw output

### 1. The unwinder attaches to the musl process

Verbatim from the collector's debug log (resource fields trimmed):

```
debug  Dotnet DAC table at 707c18, CDAC header at 6dd0a8
debug  Interpreter data dotnet 9.0.18 for /usr/share/dotnet/shared/Microsoft.NETCore.App/9.0.18/libcoreclr.so (0x1214cd5c049b1cc3)
debug  Attach PID 650387, bias ffff8da3d000
debug  Loading symbol addresses into eBPF map for PID 650387 type 10
debug  Attached to dotnet 9.0.18 interpreter in PID 650387
debug  Found code range list head at ffff8e1453c0
debug  /usr/share/dotnet/shared/Microsoft.NETCore.App/9.0.18/System.Threading.dll -> System.Threading.dll guid ecfd9ad9-fdfc-4433-a36b-728eeaa1802a
```

No errors, no warnings, no "unsupported" anywhere in the .NET path. It walks CoreCLR's DAC
table and code-range list and reads PE metadata GUIDs out of the target's assemblies
exactly as it would on glibc.

### 2. The test workload's stack — decisive

Reconstructed from `/work/profiles.json` (raw OTLP profiles, `stackTable` →
`locationTable` → `functionTable` → `stringTable`):

```
SAMPLE #0  attrs={"thread.name": "burner", "thread.id": "650387", "cpu.logical_number": "0"}
#    mapping            function                           file:line
0    burner.dll         Burner.ZanzibarWidgetFactory.PalindromeHotLoop  burner.dll:0
1    burner.dll         Burner.ZanzibarWidgetFactory.QuixoticLevelFive  burner.dll:0
2    burner.dll         Burner.ZanzibarWidgetFactory.QuixoticLevelFour  burner.dll:0
3    burner.dll         Burner.ZanzibarWidgetFactory.QuixoticLevelThree burner.dll:0
4    burner.dll         Burner.ZanzibarWidgetFactory.QuixoticLevelTwo   burner.dll:0
5    burner.dll         Burner.ZanzibarWidgetFactory.QuixoticLevelOne   burner.dll:0
6    burner.dll         Burner.Program.Main                             burner.dll:0
7    libcoreclr.so      <unsymbolized>                     addr=0x4ac24f
8    libcoreclr.so      <unsymbolized>                     addr=0x311ce3
9    libcoreclr.so      <unsymbolized>                     addr=0x2087cf
10   libcoreclr.so      <unsymbolized>                     addr=0x208b3f
11   libcoreclr.so      <unsymbolized>                     addr=0x22f37b
12   libcoreclr.so      <unsymbolized>                     addr=0x1f56cf
13   libhostpolicy.so   <unsymbolized>                     addr=0x3ad83
14   libhostpolicy.so   <unsymbolized>                     addr=0x3be17
15   libhostfxr.so      <unsymbolized>                     addr=0x2fae7
16   libhostfxr.so      <unsymbolized>                     addr=0x2ee47
17   libhostfxr.so      <unsymbolized>                     addr=0x29427
18   burner             <unsymbolized>                     addr=0x177ab
19   burner             <unsymbolized>                     addr=0x17aa7
20   ld-musl-aarch64.so.1 <unsymbolized>                   addr=0x1ff9b
```

Every managed frame, correct order, complete to `Main`, correct namespace-qualified names.
Below `Main` the stack continues correctly through the JIT/CoreCLR boundary into the host
and out to musl's `ld-musl-aarch64.so.1` — unwound, not symbolized.

The same thing in the `debug` exporter's string-table dump from run 1, showing the
JIT/native transition as the agent interned it:

```
    ld-musl-aarch64.so.1
    libcoreclr.so
    burner.dll
    Burner.ZanzibarWidgetFactory.PalindromeHotLoop
    Burner.ZanzibarWidgetFactory.QuixoticLevelFive
    Burner.ZanzibarWidgetFactory.QuixoticLevelFour
    Burner.ZanzibarWidgetFactory.QuixoticLevelThree
    Burner.ZanzibarWidgetFactory.QuixoticLevelTwo
    Burner.ZanzibarWidgetFactory.QuixoticLevelOne
    Burner.Program.Main
    libhostpolicy.so
    libhostfxr.so
```

### 3. Corroboration — real ASP.NET Core, also musl, unplanned

The node already hosted three pre-existing services from earlier work. They turned out to
be Alpine too:

```
pid=298634 libc=ld-musl-aarch64.so.1 cmd=dotnet BillingService.dll
pid=298664 libc=ld-musl-aarch64.so.1 cmd=dotnet PatientPortalService.dll
pid=298707 libc=ld-musl-aarch64.so.1 cmd=dotnet EhrService.dll
```

A 29-managed-frame stack captured from one of them, verbatim:

```
attrs={"thread.name": ".NET TP Worker", "thread.id": "648590", "cpu.logical_number": "0"}
0    vmlinux                            fpsimd_bind_task_to_cpu
1    vmlinux                            do_notify_resume
2    vmlinux                            el0_interrupt
3    vmlinux                            __el0_irq_handler_common
4    vmlinux                            el0t_64_irq_handler
5    Microsoft.Extensions.Logging.Console.dll  ...SimpleConsoleFormatter.WriteInternal
6    Microsoft.Extensions.Logging.Console.dll  ...SimpleConsoleFormatter.Write
7    Microsoft.Extensions.Logging.Console.dll  ...ConsoleLogger.Log
8    <none>                             [stub: dynamic]
9    Microsoft.Extensions.Logging.dll   Microsoft.Extensions.Logging.Logger.<Log>g__LoggerLog|14_0
10   Microsoft.Extensions.Logging.dll   Microsoft.Extensions.Logging.Logger.Log
11   <none>                             [stub: dynamic]
12   <none>                             [stub: dynamic]
13   Microsoft.Extensions.Logging.Abstractions.dll  ...LoggerMessage/<>c__DisplayClass10_0`1.<Define>g__Log|0
14   Microsoft.AspNetCore.Routing.dll   ...EndpointMiddleware.Invoke
15   PatientPortalService.dll           PatientPortalService.Telemetry.ProfileLoggingMiddleware/<InvokeAsync>d__4.MoveNext
16   System.Private.CoreLib.dll         System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start
17   Microsoft.AspNetCore.Authorization.Policy.dll  ...AuthorizationMiddleware/<Invoke>d__11.MoveNext
...
23   Microsoft.AspNetCore.Server.Kestrel.Core.dll  ...HttpProtocol/<ProcessRequests>d__238`1.MoveNext
...
35   System.Private.CoreLib.dll         System.Threading.ThreadPoolWorkQueue.Dispatch
36   System.Private.CoreLib.dll         System.Threading.PortableThreadPool/WorkerThread.WorkerThreadStart
37   libcoreclr.so                      <unsymbolized> addr=0x4ac24f
...
44   ld-musl-aarch64.so.1               <unsymbolized> addr=0x6246b
```

This is the stronger evidence of the two. It handles async state machines (`<Invoke>d__11.MoveNext`),
generic instantiations (`` `1 ``), compiler-generated lambda display classes
(`<>c__DisplayClass10_0`), local functions (`g__LoggerLog|14_0`), and application code
(`PatientPortalService.Telemetry.ProfileLoggingMiddleware`) — all on musl/arm64. It also
labels JIT stubs it cannot name as `[stub: dynamic]` rather than dropping or corrupting the
frame.

### 4. Symbolization rate by module (75 s, whole node, 9212 frames)

| mapping | symbolized | raw address | % |
|---|---:|---:|---:|
| `ld-musl-aarch64.so.1` | 0 | 2411 | **0%** |
| `otelcol-ebpf-profiler` (Go) | 1749 | 0 | 100% |
| `vmlinux` (kernel) | 1413 | 0 | **100%** |
| `kubelet` (Go) | 1265 | 0 | 100% |
| `containerd` (Go) | 566 | 0 | 100% |
| `libcoreclr.so` | 0 | 344 | **0%** |
| `burner.dll` (our managed code) | 224 | 0 | **100%** |
| `System.Private.CoreLib.dll` | 36 | 0 | **100%** |
| `Microsoft.AspNetCore.Server.Kestrel.Core.dll` | 25 | 0 | **100%** |
| `libhostfxr.so` | 0 | 96 | 0% |
| `libhostpolicy.so` | 0 | 64 | 0% |
| `burner` (apphost) | 0 | 64 | 0% |
| `libclrjit.so` | 0 | 8 | 0% |

Clean split: **managed `.dll` 100%, kernel 100%, Go 100%, native ELF 0%.**

`burner.dll` = 224 frames = exactly 32 samples × 7 managed frames. Nothing dropped.

### 5. Native frames carry build IDs, but debuginfo does not exist

Unsymbolized frames are not lost data — each mapping ships identifiers:

```
ld-musl-aarch64.so.1  {"process.executable.build_id.gnu": "963c504ed9caf79fad3f14548e4c5f4b17448689", "process.executable.build_id.htlhash": "51cbb8845ae118fc5d8296b4e8c68dc8"}
libcoreclr.so         {"process.executable.build_id.gnu": "49065c58d2788bcffcbeed2b54a914a8fbda89bc", ...}
burner.dll            {"process.executable.build_id.gnu": "90284372-28eb-4955-a761-0dddb655b307", ...}
vmlinux               {"process.executable.build_id.gnu": "af3d90de234d6d9795314236d1463283093b8f73", ...}
```

But the binaries themselves are stripped — `.dynsym` and `.gnu_debuglink` only, no
`.symtab`, no `.debug_info`, for `libcoreclr.so`, `ld-musl-aarch64.so.1`, and the apphost
alike. So backend symbolization of the native portion would need an external debuginfo
source keyed on those build IDs, and no public one exists for Microsoft's Alpine runtime
`.so` files. **Treat native-frame symbolization on Alpine as unavailable, not as pending.**

---

## Caveats that matter for the reference implementation

1. **No line numbers.** Every managed frame came back with line `0` (`burner.dll:0`).
   Function-level granularity only. Upstream documents this: line numbers for Release-built
   modules are inaccurate, and inlining information is unavailable in the default
   configuration (`dotnet/runtime#96473`).

2. **Inlined methods are invisible.** Our test only proves the non-inlined case — every
   method carried `[MethodImpl(NoInlining)]` deliberately. Real code will lose inlined
   frames.

3. **Deployment model constrains this — sharply.** The unwinder finds the runtime by
   matching the *path*:
   `dotnetRegex = regexp.MustCompile('/(\d+)\.(\d+).(\d+)/libcoreclr.so$')` (`interpreter/dotnet/dotnet.go`).
   It then rejects anything outside `[6.0.0, 11.0.0)`. That means framework-dependent
   deployments against a shared runtime work (what we tested); **self-contained and
   single-file publishes may not match**, and **NativeAOT is explicitly unsupported**
   ("Currently not supported by this interpreter code"). This is read from source, not
   tested — worth its own probe if the reference implementation intends to support those
   publish modes.

4. **Privileged is required here.** With `unprivileged_bpf_disabled=1` and
   `perf_event_paranoid=2` on AL2023, `privileged: true` was used. CAP_BPF + CAP_PERFMON +
   CAP_SYS_ADMIN was not separately tested.

5. **`hostPID: true` is mandatory** for the agent to see and attach to containerized .NET
   processes.

---

## What this means for the EventPipe sidecar

The headline assumption in the design — "the eBPF profiler's .NET unwinder is unverified on
musl" — resolves **favourably**. The risk register entry can be closed as tested-and-passed.

**Weight comes off the sidecar for CPU profiling.** The DaemonSet alone produces
named, correctly-ordered managed CPU stacks for Alpine/musl arm64 .NET workloads,
whole-node, zero-touch, with no per-pod change. For the "problem → flame graph" journey on
a CPU-bound problem, eBPF is sufficient on its own. The sidecar is no longer load-bearing
for that path, which is the most common path.

**Weight stays on the sidecar for everything else.** eBPF cannot see what it was never
watching:

- **GC** — collections, pause times, generation promotion. Only EventPipe.
- **Thread contention** — lock waits, `Monitor` contention. Only EventPipe.
- **Allocation profiling** — sampled allocation stacks. Only EventPipe.
- **Exceptions.**
- **Line numbers and inlined frames**, per caveats 1 and 2 above.
- **Non-standard publish modes** (self-contained / single-file / NativeAOT), per caveat 3.

So the two-shipper architecture survives, but the *justification* changes. The sidecar is no
longer "the thing that gets us managed frames because eBPF can't"; it is "the thing that
gets us GC, contention, allocation, and line-level detail." That is a narrower and more
honest claim, and it should be rewritten that way in the README's shape table, which
currently credits eBPF with only "kernel and native frames".

It also raises a genuine design question worth its own ticket: if eBPF already yields
managed CPU stacks, is the EventPipe sidecar's **CPU** collection redundant, and should it
be narrowed to GC/contention/allocation only? That would cut sidecar overhead and Grail
volume materially. Cross-refs: this unblocks #9 and #20.

One thing that did **not** improve: the native half. musl, CoreCLR internals, and the
apphost stay as `module + offset + build ID`. The README's honest-limitations section
should gain a line saying native-frame symbolization on Alpine is unavailable in practice
because the shipped binaries are stripped and no public debuginfo exists for them.

---

## Reproducing this

1. Deploy any framework-dependent .NET app on `mcr.microsoft.com/dotnet/sdk:9.0-alpine`
   (or `aspnet:9.0-alpine`) on an arm64 node.
2. Deploy `ghcr.io/open-telemetry/opentelemetry-collector-releases/opentelemetry-collector-ebpf-profiler:0.157.0`
   as a DaemonSet with `privileged: true` and `hostPID: true`.
3. Config: `profiling` receiver → `debug` exporter (`verbosity: detailed`).
4. Start it with `--feature-gates=service.profilesSupport`.
5. Grep the string table for your own type names.

Note that the `debug` exporter prints the dictionary tables (functions, locations, strings)
but **not** per-sample stack ordering. To get ordered stacks you need raw OTLP —
add the contrib `fileexporter` and walk `stackTable → locationTable → functionTable →
stringTable` yourself, which is what produced the excerpts above.

---

## Cleanup

Namespace `probe-ebpf` was deleted, which cascaded the `burner` pod, the `ebpf-build` pod,
the ConfigMap, and the namespace itself. Verified afterwards: `namespaces "probe-ebpf" not
found`, and a cluster-wide pod listing matches nothing named `burner`, `ebpf-build`, or
`probe-ebpf`. Both nodes `Ready`. No cluster-scoped objects were created; no host state was
modified (the hostPath mounts of `/sys/fs/bpf` and `/sys/kernel/debug` were read paths for
the BPF loader, and the agent pinned nothing — `Skip pinning eBPF map to share OTel
span/trace IDs`).

Nothing pre-existing was deleted. The `eks-arm-test` namespace and its `otel-ebpf-profiler`,
`otelcol-profiles`, and application workloads were left in place, including the long-broken
`opentelemetry-ebpf-profiler` DaemonSet that has been in `ImagePullBackOff` for 20 days.

**One self-inflicted side effect, disclosed for accuracy.** My teardown ran
`pkill -f otelcol-ebpf-profiler` from inside a pod with `hostPID: true`, so the pattern also
matched the pre-existing `otel-ebpf-profiler` process on that node and signalled it. It
shut down cleanly (`Received signal from OS: terminated` → `Shutdown complete`, exit code
0) and its DaemonSet restarted it immediately. Confirmed back to `Running`, `ready: true`
on both nodes. No data or configuration was lost; the effect was a ~45 s gap in that node's
profiling. Lesson for the reference implementation's own tooling: never `pkill` by name
from a `hostPID` container — match on PID instead.
