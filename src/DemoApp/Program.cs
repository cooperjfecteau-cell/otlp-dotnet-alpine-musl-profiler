using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// A workload with hotspots worth looking at.
//
// Each endpoint exercises one shape a profiler should be able to distinguish:
// CPU-bound, allocation-heavy, lock-contended, and I/O-bound. If a flame graph
// cannot tell these apart, the pipeline is not doing its job.

var builder = WebApplication.CreateBuilder(args);

var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "dotnet-profiler-demo";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = serviceName,
    pid = Environment.ProcessId,
    endpoints = new[] { "/cpu", "/alloc", "/lock", "/io", "/deep", "/healthz" },
}));

app.MapGet("/healthz", () => Results.Ok("ok"));

// ── CPU-bound ────────────────────────────────────────────────────────────────
// Pure computation, no allocation, no waiting. Should dominate a CPU flame graph.
app.MapGet("/cpu", (int iterations = 200_000) =>
{
    var result = Workloads.CpuBound(iterations);
    return Results.Ok(new { shape = "cpu", iterations, result });
});

// ── Allocation-heavy ─────────────────────────────────────────────────────────
// Churns short-lived objects to force gen0 collections. Invisible to CPU
// sampling as anything but "GC ran"; EventPipe is what makes this legible, which
// is exactly the division of labour the two halves are meant to demonstrate.
app.MapGet("/alloc", (int megabytes = 64) =>
{
    var retained = Workloads.AllocationHeavy(megabytes);
    return Results.Ok(new { shape = "alloc", megabytes, retained });
});

// ── Lock-contended ───────────────────────────────────────────────────────────
// Several threads fighting over one lock. CPU sampling shows threads parked and
// little else; contention events name the actual problem.
app.MapGet("/lock", async (int threads = 8, int holdMicros = 200) =>
{
    var waited = await Workloads.LockContendedAsync(threads, holdMicros);
    return Results.Ok(new { shape = "lock", threads, holdMicros, totalWaitMs = waited });
});

// ── I/O-bound ────────────────────────────────────────────────────────────────
// Mostly waiting. Should be conspicuously ABSENT from a CPU flame graph — a
// profiler that shows this burning CPU is lying, which makes it a useful control.
app.MapGet("/io", async (int delayMs = 150) =>
{
    await Workloads.IoBoundAsync(delayMs);
    return Results.Ok(new { shape = "io", delayMs });
});

// ── Deliberately deep stack ──────────────────────────────────────────────────
// A flame graph of three frames demonstrates nothing. This is 12 frames deep,
// every one marked NoInlining so the chain survives the JIT.
app.MapGet("/deep", (int iterations = 50_000) =>
{
    var result = DeepStack.Level01(iterations);
    return Results.Ok(new { shape = "deep", depth = 12, iterations, result });
});

app.Run();


internal static class Workloads
{
    private static readonly object Gate = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static long CpuBound(int iterations)
    {
        // SHA-256 over a rolling buffer: real work the JIT cannot elide, and
        // recognisable in a stack by name.
        Span<byte> buffer = stackalloc byte[64];
        Span<byte> hash = stackalloc byte[32];
        long acc = 0;

        for (var i = 0; i < iterations; i++)
        {
            BitConverter.TryWriteBytes(buffer, (long)i);
            SHA256.HashData(buffer, hash);
            acc += hash[0];
        }
        return acc;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int AllocationHeavy(int megabytes)
    {
        const int chunk = 64 * 1024;
        var chunks = megabytes * 1024 * 1024 / chunk;
        var survivors = new List<byte[]>();

        for (var i = 0; i < chunks; i++)
        {
            var block = new byte[chunk];
            block[0] = (byte)i;
            // Retain roughly one in sixteen so objects get promoted rather than
            // all dying in gen0 — a pure gen0 churn makes for a boring GC profile.
            if (i % 16 == 0) survivors.Add(block);
        }
        return survivors.Count;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<double> LockContendedAsync(int threads, int holdMicros)
    {
        var sw = Stopwatch.StartNew();
        var tasks = new Task[threads];

        for (var t = 0; t < threads; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (var i = 0; i < 50; i++) HoldTheLock(holdMicros);
            });
        }

        await Task.WhenAll(tasks);
        return sw.Elapsed.TotalMilliseconds;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void HoldTheLock(int holdMicros)
    {
        lock (Gate)
        {
            // Spin rather than sleep: sleeping inside a lock produces waiters but
            // no CPU signal, and we want both halves to have something to show.
            var until = Stopwatch.GetTimestamp() + (holdMicros * Stopwatch.Frequency / 1_000_000);
            while (Stopwatch.GetTimestamp() < until) { }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task IoBoundAsync(int delayMs)
    {
        await Task.Delay(delayMs);
    }
}


/// <summary>
/// Twelve nested frames, each NoInlining, ending in real work.
///
/// The names are deliberately distinctive so a stack can be checked by eye: if
/// intermediate levels are missing from a flame graph, inlining defeated us and
/// the profile is lying about the call path.
/// </summary>
internal static class DeepStack
{
    [MethodImpl(MethodImplOptions.NoInlining)] internal static long Level01(int n) => Level02(n);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Level02(int n) => Level03(n);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Level03(int n) => Level04(n);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Level04(int n) => Level05(n);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Level05(int n) => Level06(n);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Level06(int n) => Level07(n);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Level07(int n) => Level08(n);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Level08(int n) => Level09(n);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Level09(int n) => Level10(n);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Level10(int n) => Level11(n);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Level11(int n) => Level12(n);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Level12(int n) => Workloads.CpuBound(n);
}
