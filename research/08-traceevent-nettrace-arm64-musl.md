# 08 — Does TraceEvent parse nettrace on linux-musl-arm64?

Resolves research ticket #8. Everything below was executed, not inferred.

## Verdict

**WORKS WITH CAVEATS.**

`Microsoft.Diagnostics.Tracing.TraceEvent` 3.2.5 restores, loads, and fully parses nettrace
on `linux-musl-arm64`. No `DllNotFoundException`, no missing native dependency, no
Windows-shaped runtime failure. It produced **41,111 CPU samples with 0 unresolved frames**
and correctly symbolized managed stacks into exactly the `root;mid;leaf` shape the sidecar
folds. `dotnet-trace` also works on musl/arm64, in both launch and attach modes.

The caveats are not about musl or arm64. They are about **thread identity**, and they would
apply equally on glibc/x64:

- **OS thread id: available on every event, including every CPU sample.** Verified against
  `gettid()` and `/proc/self/task`.
- **Managed thread id: NOT reliably available**, and specifically **not available for the
  threads that matter in the sidecar's real attach-mode topology.** Details in
  [Thread identity](#thread-identity-the-real-finding).

The design's stated requirement — "map managed thread id -> OS thread id so EventPipe
samples join to eBPF samples in DQL" — **is based on a false premise.** EventPipe samples
already carry the OS thread id directly. No mapping is needed for that join. See
[What this means for the design](#what-this-means-for-the-design).

## Environment

Built and run inside the EKS cluster (no local Docker/WSL on the dev machine).
Namespace `probe-traceevent`, single pod on `mcr.microsoft.com/dotnet/sdk:9.0-alpine`,
scheduled onto a `t4g.large` (aarch64) node.

```
Linux te-probe 6.1.176-221.360.amzn2023.aarch64 aarch64 Linux
NAME="Alpine Linux"   VERSION_ID=3.23.5
/lib/ld-musl-aarch64.so.1     musl libc (aarch64) Version 1.2.5
.NET SDK 9.0.316 / Host 9.0.18 / Architecture arm64 / RID: linux-musl-arm64
```

Cluster: EKS 1.31, `eks-arm-new`, us-east-1, containerd 2.2.4, Amazon Linux 2023.

### Exact package versions

`Microsoft.Diagnostics.Tracing.TraceEvent` **3.2.5**
(`3.2.5+53829f11630f37c1563fd9242ca1e9c7fc6b5b74`), resolved as latest stable by
`dotnet add package` on 2026-08-11. Full transitive closure as restored on musl/arm64:

```
> Microsoft.Diagnostics.Tracing.TraceEvent      3.2.5
> Microsoft.Diagnostics.NETCore.Client          0.2.510501
> Microsoft.Extensions.DependencyInjection      6.0.0
> Microsoft.Extensions.DependencyInjection.Abstractions 6.0.0
> Microsoft.Extensions.Logging                  6.0.0
> Microsoft.Extensions.Logging.Abstractions     6.0.0
> Microsoft.Extensions.Options                  6.0.0
> Microsoft.Extensions.Primitives               6.0.0
> Microsoft.NETCore.Platforms                   5.0.0
> Microsoft.Win32.Registry                      5.0.0
> System.Collections.Immutable                  9.0.8
> System.Diagnostics.DiagnosticSource           6.0.0
> System.Reflection.Metadata                    9.0.8
> System.Reflection.TypeExtensions              4.7.0
> System.Runtime.CompilerServices.Unsafe        6.1.2
> System.Security.AccessControl                 5.0.0
> System.Security.Principal.Windows             5.0.0
> System.Text.Json                              9.0.8
```

Note `Microsoft.Win32.Registry`, `System.Security.AccessControl`, and
`System.Security.Principal.Windows` in the closure. These are the "Windows-shaped
assumptions" the ticket worried about — but they are **managed** assemblies that no-op on
non-Windows, not native dependencies. Nothing in the nettrace path touches them. Restore
and load were clean. TraceEvent 3.2.5 ships no native assets that matter here; it resolved
to the plain `lib/` asset and ran unmodified.

`dotnet-trace` **9.0.661903** installed as a global tool from NuGet and ran natively.

## Method

1. A CPU-burning .NET app with a deliberately named call tree
   (`AlphaRoot -> BetaMiddle -> {GammaLeafPrime | DeltaLeafHash}`) across three threads.
   It prints its own managed-id -> OS-tid mapping two independent ways (a `gettid()`
   P/Invoke and `/proc/self/task/<tid>/comm`), giving **ground truth** to check
   TraceEvent's answers against rather than trusting them.
2. `dotnet-trace collect` on musl/arm64 to produce real nettrace files — in both
   **launch** mode and **attach-to-running-process** mode.
3. A parser program referencing TraceEvent that runs three passes: raw
   `EventPipeEventSource` decode, `TraceLog` conversion + stack symbolization, and a
   thread-identity pass.

### Note: dotnet-trace profile names changed

`--profile cpu-sampling` **no longer means managed EventPipe sampling** in dotnet-trace 9.x:

```
[ERROR] The specified profile 'cpu-sampling' does not apply to `dotnet-trace collect`.
```

`list-profiles` shows it has been repurposed for the `collect-linux` (kernel/perf) verb.
The managed sampler is now **`dotnet-sampled-thread-time`**:

```
dotnet-sampled-thread-time (collect) - Samples .NET thread stacks (~100 Hz) ...
cpu-sampling (collect-linux)         - Kernel CPU sampling events for measuring CPU usage.
```

Anything in our sidecar or docs that hardcodes `cpu-sampling` for EventPipe will break on
current tooling. The underlying provider is unchanged
(`Microsoft-DotNETCore-SampleProfiler`, keywords `0x0000F00000000000`, Informational).

## Result 1 — parsing and symbolization: clean pass

Launch-mode trace, sampler-only profile (549,694 byte nettrace):

```
=== RUNTIME / ASSEMBLY IDENTITY ===
RuntimeIdentifier : linux-musl-arm64
OSDescription     : Alpine Linux v3.23
ProcessArchitecture: Arm64
FrameworkDescription: .NET 9.0.18
TraceEvent asm    : Microsoft.Diagnostics.Tracing.TraceEvent, Version=3.2.5.0, ...
TraceEvent product: 3.2.5+53829f11630f37c1563fd9242ca1e9c7fc6b5b74

=== PASS 1: raw EventPipeEventSource ===
Total events decoded: 41595
Distinct OS thread ids seen in event headers: 3 -> [392, 404, 405]

=== PASS 2: TraceLog.CreateFromEventPipeDataFile -> symbolized stacks ===
etlx: /work/trace.nettrace.etlx (4801740 bytes) in 461 ms
EventCount=41141 Processes=1 Threads=3 Modules=8

SampleProfiler events=41111 withStack=41111 withoutStack=0 unresolvedFrames=0 distinctFoldedStacks=34
```

**0 without stack, 0 unresolved frames.** The folded output is exactly the shape the
sidecar needs:

```
 17759  Program+<>c__DisplayClass3_1.<Main>b__0();Program.WorkerEntry(value class System.DateTime,int32);Program.AlphaRoot(value class System.DateTime,int32);Program.BetaMiddle(int32);Program.DeltaLeafHash(int32)
  9595  Program+<>c__DisplayClass3_1.<Main>b__0();Program.WorkerEntry(value class System.DateTime,int32);Program.AlphaRoot(value class System.DateTime,int32);Program.BetaMiddle(int32);Program.GammaLeafPrime(int32)
  8502  Program.Main(class System.String[]);Program.WorkerEntry(value class System.DateTime,int32);Program.AlphaRoot(value class System.DateTime,int32);Program.BetaMiddle(int32);Program.DeltaLeafHash(int32)
  4818  Program.Main(class System.String[]);Program.WorkerEntry(value class System.DateTime,int32);Program.AlphaRoot(value class System.DateTime,int32);Program.BetaMiddle(int32);Program.GammaLeafPrime(int32)
   357  Program.Main(class System.String[]);System.Threading.Thread.Sleep(int32)
```

This is correct in substance, not just non-crashing: the call tree matches the source, the
two leaves are distinguished, sample counts are proportional to the work each leaf does,
and per-thread attribution is near-identical across the three burner threads
(13714 / 13702 / 13695 samples) exactly as the workload implies.

Conversion cost is modest — 461 ms and a 4.8 MB etlx for a 550 KB nettrace, ~9x expansion.
Worth noting for sidecar memory/disk sizing: `TraceLog.CreateFromEventPipeDataFile` writes
an intermediate `.etlx` **to disk next to the input**, it is not a pure in-memory path.

### Symbolization survives attach mode

The realistic sidecar topology attaches to an **already-running** process, so most code is
JITted before the session begins and symbol info must come from rundown. It holds up:

```
EventCount=83216 Processes=1 Threads=7 Modules=8
SampleProfiler events=35462 withStack=35462 withoutStack=0 unresolvedFrames=0 distinctFoldedStacks=22

 15092  Program+<>c__DisplayClass3_1.<Main>b__0();...;Program.BetaMiddle(int32);Program.DeltaLeafHash(int32)
  8493  Program+<>c__DisplayClass3_1.<Main>b__0();...;Program.BetaMiddle(int32);Program.GammaLeafPrime(int32)
  7661  Program.Main(class System.String[]);...;Program.BetaMiddle(int32);Program.DeltaLeafHash(int32)
```

Still 0 unresolved frames. **The core sidecar capability is sound on musl/arm64.**

## Thread identity — the real finding

### OS thread id: yes, unconditionally

Every event, including every CPU sample, carries the OS thread id in its header
(`TraceEvent.ThreadID`). These are genuine Linux TIDs, confirmed three ways:

- Launch-mode ground truth from the app itself:
  ```
  TIDMAP name=burn-main managed=1 os_gettid=474
  TIDMAP name=burner-0  managed=6 os_gettid=486
  TIDMAP name=burner-1  managed=7 os_gettid=488
  ```
- `/proc/self/task` agrees (`os_tid=486 comm=burner-0`, `os_tid=488 comm=burner-1`).
- TraceEvent's per-thread sample attribution lands on exactly those TIDs:
  ```
  os_tid=474 samples=11737   os_tid=486 samples=11723   os_tid=488 samples=11723
  ```
- The main thread's TID equals the process id (474 == 474), as Linux requires.

Incidentally, `DllImport("libc", EntryPoint="gettid")` resolved fine under musl 1.2.5 — a
small useful data point if we ever need it in the sidecar.

### Managed thread id: only sometimes, and not when it counts

The sampler-only profile carries **no** managed thread identity at all:

```
--- events carrying a *Managed*Thread* payload field ---
(none found)
```

Enabling the CLR `AppDomainResourceManagement | Threading` keywords
(`Microsoft-Windows-DotNETRuntime:0x1F81D:5`) produces `ThreadCreated`, which does carry
it. **Trap:** the field literally named `ManagedThreadID` is *not* the managed thread id —
it is the native address of the CLR `Thread` object. The real analogue is
**`ManagedThreadIndex`**:

```
ThreadCreated | header.ThreadID(OS)=474 | ManagedThreadID=281472903413856, ..., ManagedThreadIndex=1, OSThreadID=474
ThreadCreated | header.ThreadID(OS)=486 | ManagedThreadID=281472906962576, ..., ManagedThreadIndex=6, OSThreadID=486
ThreadCreated | header.ThreadID(OS)=488 | ManagedThreadID=281472906960272, ..., ManagedThreadIndex=7, OSThreadID=488
```

Against ground truth (`managed=1 -> os=474`, `managed=6 -> os=486`, `managed=7 -> os=488`)
`ManagedThreadIndex` matched `Thread.ManagedThreadId` **3 / 3**. So in launch mode the
mapping is obtainable.

Two problems.

**(a) The index is recycled.** In the same trace, a later thread reused a dead thread's slot:

```
ThreadCreated | header.ThreadID(OS)=485 | ... ManagedThreadIndex=5, OSThreadID=485
ThreadCreated | header.ThreadID(OS)=490 | ... ManagedThreadIndex=5, OSThreadID=490
```

Any such map must be time-scoped, not a flat dictionary. A naive `dict[managed] = os` is
silently wrong for long-lived processes.

**(b) Decisive: it is absent for pre-existing threads.** In attach mode — the topology the
sidecar actually uses — `ThreadCreated` only fires for threads created *after* the session
starts. There is no thread rundown. Ground truth for that run was
`managed=1 -> os=734`, `managed=4 -> os=742`, `managed=5 -> os=743`, and those three
threads absorbed **35,394 of 35,462 samples (99.8%)**:

```
os_tid=734 samples=11798
os_tid=742 samples=11798
os_tid=743 samples=11798
os_tid=737 samples=68
```

But the only `ThreadCreated` events in the trace were:

```
--- thread-related CLR events (name : count) ---
Microsoft-Windows-DotNETRuntime/AppDomainResourceManagement/ThreadCreated       4

ThreadCreated | header.ThreadID(OS)=737 | ... ManagedThreadIndex=6, OSThreadID=737
ThreadCreated | header.ThreadID(OS)=765 | ... ManagedThreadIndex=3, OSThreadID=765
ThreadCreated | header.ThreadID(OS)=767 | ... ManagedThreadIndex=7, OSThreadID=767
ThreadCreated | header.ThreadID(OS)=769 | ... ManagedThreadIndex=8, OSThreadID=769
```

**734, 742 and 743 are absent.** The managed thread id is unavailable for precisely the
threads carrying essentially all the CPU samples. Registering
`ClrRundownTraceEventParser` alongside `ClrTraceEventParser` did not help — EventPipe
rundown covers methods and modules, not threads.

`TraceLog.TraceThread` offers no help either: it exposes `ThreadID` (OS), `ThreadIndex`
(a TraceEvent-internal array index, *not* the CLR's), and a mostly-null `ThreadInfo`.

> Answering the ticket's question directly: **managed-thread-id -> OS-thread-id mapping is
> NOT reliably obtainable.** It works only for threads created during the session, only
> with extra CLR keywords enabled, only via the counter-intuitively-named
> `ManagedThreadIndex`, and only if you time-scope it against index reuse. In attach mode
> it is missing for the threads that matter.

## What this means for the design

This is a critical finding, but **it is not a blocker — it invalidates a requirement rather
than the design.**

The ticket states the design "REQUIRES mapping managed thread id -> OS thread id so that
EventPipe samples can join to eBPF samples in DQL." That requirement is unnecessary:
**EventPipe samples are already stamped with the OS thread id**, which is the same
namespace eBPF reports. The join key is present on both sides with no translation:

```
EventPipe sample  -> TraceEvent.ThreadID = 742   (verified real Linux TID)
eBPF sample       -> pid/tgid TID       = 742
```

So the managed->OS map should be **dropped from the design**, not worked around. Keep the
OS thread id as the correlation key end to end, and do not enable the extra CLR Threading
keywords — they cost overhead (the trace grew 550 KB -> 1.2 MB for the same workload) and
buy nothing we need.

The only thing lost is the ability to label a stack with a *managed* thread id in the UI.
If that is ever wanted, note the OS TID is strictly better anyway: it is stable, unique,
and joins to everything else in the pipeline.

Two smaller carry-forwards:

- Do not hardcode `--profile cpu-sampling`; use `dotnet-sampled-thread-time` (or pin the
  provider explicitly as `Microsoft-DotNETCore-SampleProfiler:0xF00000000000:4`).
- `TraceLog.CreateFromEventPipeDataFile` needs writable scratch space for the `.etlx`
  (~9x the nettrace size). Size the sidecar's `emptyDir` accordingly.

## Fallbacks — not needed

Both fallbacks named in the ticket are moot, because the primary path works:

- **(a) glibc-based sidecar image** — unnecessary. TraceEvent works on musl/arm64 as-is.
  Recommending it would mean giving up musl parity for no benefit, and it would not fix the
  thread-id issue, which is platform-independent.
- **(b) alternative nettrace parser** — unnecessary and strictly worse. The realistic
  alternatives are reimplementing the nettrace format or vendoring a third-party decoder;
  both would have to re-solve stack symbolization from rundown events, which TraceEvent
  already does perfectly here (0 unresolved frames in both launch and attach mode).

**Recommendation: keep `Microsoft.Diagnostics.Tracing.TraceEvent` 3.2.5 in a musl/arm64
sidecar, and remove the managed-thread-id mapping requirement from the design, correlating
on the OS thread id instead.**

## Test program source

### Workload (`burn/Program.cs`)

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

// A deliberately shallow-but-named call tree so that folded stacks are easy to eyeball:
//   AlphaRoot -> BetaMiddle -> {GammaLeafPrime | DeltaLeafHash}
// Thread names are set so we can recover the managed->OS thread id mapping from /proc
// independently of anything TraceEvent tells us (that is our ground truth).
public static class Program
{
    [DllImport("libc", EntryPoint = "gettid")]
    private static extern int GetTidLibc();

    [DllImport("libc.musl-aarch64.so.1", EntryPoint = "gettid")]
    private static extern int GetTidMusl();

    private static int OsTid()
    {
        try { return GetTidLibc(); } catch { }
        try { return GetTidMusl(); } catch { }
        return -1;
    }

    public static void Main(string[] args)
    {
        int seconds = args.Length > 0 ? int.Parse(args[0]) : 20;
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);

        Thread.CurrentThread.Name = "burn-main";

        var threads = new Thread[2];
        for (int i = 0; i < threads.Length; i++)
        {
            int idx = i;
            var t = new Thread(() => WorkerEntry(deadline, idx));
            t.Name = "burner-" + idx;
            t.IsBackground = false;
            threads[i] = t;
            t.Start();
        }

        Thread.Sleep(500);
        DumpProcTaskTable();

        WorkerEntry(deadline, -1);
        foreach (var t in threads) t.Join();
        Console.WriteLine("BURN_DONE");
        Console.Out.Flush();
    }

    private static void WorkerEntry(DateTime deadline, int idx)
    {
        Console.WriteLine("TIDMAP name=" + (Thread.CurrentThread.Name ?? "?") +
                          " managed=" + Environment.CurrentManagedThreadId +
                          " os_gettid=" + OsTid());
        Console.Out.Flush();
        AlphaRoot(deadline, idx);
    }

    // Ground truth without P/Invoke: .NET pushes Thread.Name down to the OS thread name,
    // so /proc/self/task/<tid>/comm identifies which OS tid hosts which named managed thread.
    private static void DumpProcTaskTable()
    {
        Console.WriteLine("--- /proc/self/task ---");
        foreach (string d in Directory.GetDirectories("/proc/self/task"))
        {
            string comm = "?";
            try { comm = File.ReadAllText(Path.Combine(d, "comm")).Trim(); } catch { }
            Console.WriteLine("PROCTASK os_tid=" + Path.GetFileName(d) + " comm=" + comm);
        }
        Console.WriteLine("--- end /proc/self/task ---");
        Console.Out.Flush();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AlphaRoot(DateTime deadline, int idx)
    {
        while (DateTime.UtcNow < deadline) BetaMiddle(idx);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void BetaMiddle(int idx)
    {
        for (int i = 0; i < 40; i++)
        {
            if (((i + idx) & 1) == 0) GammaLeafPrime(200000 + i);
            else DeltaLeafHash(i);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int GammaLeafPrime(int start)
    {
        int found = 0;
        for (int n = start; n < start + 400; n++)
        {
            bool p = n > 1;
            for (int d = 2; (long)d * d <= n; d++) { if (n % d == 0) { p = false; break; } }
            if (p) found++;
        }
        return found;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong DeltaLeafHash(int seed)
    {
        ulong h = 1469598103934665603UL ^ (ulong)seed;
        for (int i = 0; i < 60000; i++) { h ^= (ulong)i; h *= 1099511628211UL; }
        return h;
    }
}
```

### Parser (`parse/Program.cs`)

`parse.csproj` is a plain `net9.0` exe with a single
`<PackageReference Include="Microsoft.Diagnostics.Tracing.TraceEvent" Version="3.2.5" />`.

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

public static class Program
{
    public static int Main(string[] args)
    {
        string path = args.Length > 0 ? args[0] : "/work/trace.nettrace";

        Console.WriteLine("=== RUNTIME / ASSEMBLY IDENTITY ===");
        Console.WriteLine("RuntimeIdentifier : " + RuntimeInformation.RuntimeIdentifier);
        Console.WriteLine("OSDescription     : " + RuntimeInformation.OSDescription);
        Console.WriteLine("ProcessArchitecture: " + RuntimeInformation.ProcessArchitecture);
        Console.WriteLine("FrameworkDescription: " + RuntimeInformation.FrameworkDescription);
        Assembly te = typeof(TraceEvent).Assembly;
        Console.WriteLine("TraceEvent asm    : " + te.FullName);
        Console.WriteLine("TraceEvent path   : " + te.Location);
        Console.WriteLine("TraceEvent product: " +
            FileVersionInfo.GetVersionInfo(te.Location).ProductVersion);
        Console.WriteLine("Input             : " + path + " (" + new FileInfo(path).Length + " bytes)");

        int rc = 0;
        try { PassOneRaw(path); }
        catch (Exception ex) { rc = 1; Console.WriteLine("!!! PASS 1 FAILED: " + ex); }
        try { PassTwoSymbolized(path); }
        catch (Exception ex) { rc = 1; Console.WriteLine("!!! PASS 2 FAILED: " + ex); }
        try { PassThreeThreadIds(path); }
        catch (Exception ex) { rc = 1; Console.WriteLine("!!! PASS 3 FAILED: " + ex); }
        return rc;
    }

    // PASS 1: raw streaming parse. Proves the nettrace decoder itself runs.
    private static void PassOneRaw(string path)
    {
        Console.WriteLine("\n=== PASS 1: raw EventPipeEventSource ===");
        var counts = new Dictionary<string, int>();
        var payloadNames = new Dictionary<string, string[]>();
        var osThreadIds = new HashSet<int>();
        int total = 0;

        using (var src = new EventPipeEventSource(path))
        {
            src.AllEvents += delegate (TraceEvent e)
            {
                total++;
                string key = e.ProviderName + "/" + e.EventName;
                int c; counts.TryGetValue(key, out c); counts[key] = c + 1;
                osThreadIds.Add(e.ThreadID);
                string[] pn;
                try { pn = e.PayloadNames; } catch { pn = new string[0]; }
                if (!payloadNames.ContainsKey(key)) payloadNames[key] = pn;
            };
            src.Process();
        }

        Console.WriteLine("Total events decoded: " + total);
        Console.WriteLine("Distinct OS thread ids seen in event headers: " + osThreadIds.Count +
                          " -> [" + string.Join(", ", osThreadIds.OrderBy(x => x)) + "]");
        foreach (var kv in counts.OrderByDescending(k => k.Value))
            Console.WriteLine(string.Format("{0,-70} {1,7}  [{2}]",
                kv.Key, kv.Value, string.Join(",", payloadNames[kv.Key])));
    }

    // PASS 2: TraceLog conversion -> symbolized managed call stacks.
    // This is the capability the sidecar actually depends on.
    private static void PassTwoSymbolized(string path)
    {
        Console.WriteLine("\n=== PASS 2: TraceLog.CreateFromEventPipeDataFile -> symbolized stacks ===");
        var sw = Stopwatch.StartNew();
        string etlx = TraceLog.CreateFromEventPipeDataFile(path);
        sw.Stop();
        Console.WriteLine("etlx: " + etlx + " (" + new FileInfo(etlx).Length + " bytes) in " +
                          sw.ElapsedMilliseconds + " ms");

        using (var log = new TraceLog(etlx))
        {
            Console.WriteLine("EventCount=" + log.EventCount + " Processes=" + log.Processes.Count +
                              " Threads=" + log.Threads.Count + " Modules=" + log.ModuleFiles.Count);

            foreach (TraceThread t in log.Threads)
                Console.WriteLine("TraceThread ThreadID(OS)=" + t.ThreadID +
                                  " ProcessID=" + t.Process.ProcessID +
                                  " ThreadInfo=" + (t.ThreadInfo ?? "<null>"));

            var folded = new Dictionary<string, int>();
            var foldedByThread = new Dictionary<int, Dictionary<string, int>>();
            int samples = 0, noStack = 0, unresolved = 0;

            foreach (TraceEvent ev in log.Events)
            {
                if (ev.ProviderName != "Microsoft-DotNETCore-SampleProfiler") continue;
                samples++;
                TraceCallStack cs = ev.CallStack();
                if (cs == null) { noStack++; continue; }

                var frames = new List<string>();
                for (TraceCallStack f = cs; f != null; f = f.Caller)
                {
                    string m = f.CodeAddress.FullMethodName;
                    if (string.IsNullOrEmpty(m))
                    {
                        unresolved++;
                        m = "0x" + f.CodeAddress.Address.ToString("x");
                    }
                    frames.Add(m);
                }
                frames.Reverse();
                string key = string.Join(";", frames);

                int c; folded.TryGetValue(key, out c); folded[key] = c + 1;
                Dictionary<string, int> per;
                if (!foldedByThread.TryGetValue(ev.ThreadID, out per))
                { per = new Dictionary<string, int>(); foldedByThread[ev.ThreadID] = per; }
                per.TryGetValue(key, out c); per[key] = c + 1;
            }

            Console.WriteLine("SampleProfiler events=" + samples +
                              " withStack=" + (samples - noStack) + " withoutStack=" + noStack +
                              " unresolvedFrames=" + unresolved +
                              " distinctFoldedStacks=" + folded.Count);

            foreach (var kv in folded.OrderByDescending(k => k.Value).Take(12))
                Console.WriteLine(kv.Value.ToString().PadLeft(6) + "  " + kv.Key);

            foreach (var kv in foldedByThread.OrderByDescending(k => k.Value.Values.Sum()))
                Console.WriteLine("os_tid=" + kv.Key + " samples=" + kv.Value.Values.Sum());
        }
    }

    // PASS 3: managed thread id -> OS thread id.
    // Pass 1 used a bare EventPipeEventSource, which has no templates registered for the
    // CLR provider, so PayloadNames came back empty. Registering ClrTraceEventParser (and
    // the rundown parser) gives TraceEvent the manifests it needs to decode payloads.
    private static void PassThreeThreadIds(string path)
    {
        Console.WriteLine("\n=== PASS 3: managed thread id <-> OS thread id ===");
        var seenByName = new Dictionary<string, int>();
        var dump = new List<string>();
        var managedToOs = new Dictionary<long, long>();

        using (var src = new EventPipeEventSource(path))
        {
            var clr = new Microsoft.Diagnostics.Tracing.Parsers.ClrTraceEventParser(src);

            // The rundown parser's type name has moved between TraceEvent versions; find it.
            Type rt = typeof(TraceEvent).Assembly.GetTypes()
                .FirstOrDefault(x => x.Name.IndexOf("Rundown", StringComparison.Ordinal) >= 0 &&
                                     x.Name.EndsWith("TraceEventParser", StringComparison.Ordinal) &&
                                     !x.IsAbstract);
            Console.WriteLine("Rundown parser type: " + (rt == null ? "<not found>" : rt.FullName));

            Action<TraceEvent> handler = delegate (TraceEvent e)
            {
                string[] pn;
                try { pn = e.PayloadNames; } catch { return; }
                if (pn == null || pn.Length == 0) return;

                int mIdx = -1, oIdx = -1;
                bool anyThread = false;
                for (int i = 0; i < pn.Length; i++)
                {
                    if (pn[i].IndexOf("Thread", StringComparison.OrdinalIgnoreCase) >= 0) anyThread = true;
                    // NB: the field literally called "ManagedThreadID" is a Thread* pointer.
                    // "ManagedThreadIndex" is the real Thread.ManagedThreadId analogue.
                    if (pn[i].IndexOf("Managed", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        pn[i].IndexOf("Thread", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        pn[i].IndexOf("Index", StringComparison.OrdinalIgnoreCase) < 0) mIdx = i;
                    if (pn[i].Equals("OSThreadID", StringComparison.OrdinalIgnoreCase)) oIdx = i;
                }
                if (!anyThread) return;

                string key = e.ProviderName + "/" + e.EventName;
                int n; seenByName.TryGetValue(key, out n); seenByName[key] = n + 1;

                if (n < 20 && dump.Count < 60)
                {
                    var parts = new List<string>();
                    for (int j = 0; j < pn.Length; j++)
                    {
                        object v;
                        try { v = e.PayloadValue(j); } catch { v = "<err>"; }
                        parts.Add(pn[j] + "=" + v);
                    }
                    dump.Add(key + " | header.ThreadID(OS)=" + e.ThreadID + " | " + string.Join(", ", parts));
                }

                if (mIdx >= 0 && oIdx >= 0)
                {
                    try
                    {
                        managedToOs[Convert.ToInt64(e.PayloadValue(mIdx))] =
                            Convert.ToInt64(e.PayloadValue(oIdx));
                    }
                    catch { }
                }
            };

            clr.All += handler;
            if (rt != null)
            {
                object rundown = Activator.CreateInstance(rt, new object[] { src });
                EventInfo ei = rt.GetEvent("All");
                if (ei != null) ei.AddEventHandler(rundown, handler);
            }
            src.Process();
        }

        foreach (var kv in seenByName.OrderByDescending(k => k.Value))
            Console.WriteLine(string.Format("{0,-70} {1,7}", kv.Key, kv.Value));
        foreach (string s in dump) Console.WriteLine(s);

        Console.WriteLine("--- RECOVERED managed -> OS thread id map ---");
        if (managedToOs.Count == 0)
            Console.WriteLine("(EMPTY - no event carried both a managed and an OS thread id)");
        foreach (var kv in managedToOs.OrderBy(k => k.Key))
            Console.WriteLine("managed=" + kv.Key + " -> os=" + kv.Value);
    }
}
```

### Commands used

```sh
dotnet tool install -g dotnet-trace          # -> 9.0.661903
dotnet add package Microsoft.Diagnostics.Tracing.TraceEvent   # -> 3.2.5

# launch mode
dotnet-trace collect --output /work/trace.nettrace \
  --profile dotnet-sampled-thread-time -- /work/burn/bin/Release/net9.0/burn 20

# launch mode + CLR threading keywords
dotnet-trace collect --output /work/trace2.nettrace --show-child-io \
  --profile dotnet-sampled-thread-time \
  --providers Microsoft-Windows-DotNETRuntime:0x1F81D:5 -- .../burn 15

# attach mode (realistic sidecar topology)
dotnet-trace collect --output /work/trace3.nettrace -p "$APPPID" \
  --duration 00:00:00:15 --profile dotnet-sampled-thread-time \
  --providers Microsoft-Windows-DotNETRuntime:0x1F81D:5
```

## Cleanup

All cluster resources created for this experiment were deleted: the `probe-traceevent`
namespace and everything in it (pod `te-probe` and its `emptyDir`). Nothing was left
running. Verification is recorded in the ticket comment. No other namespace was touched,
and nothing was committed or pushed.
