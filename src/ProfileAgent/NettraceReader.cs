using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace ProfileAgent;

internal sealed record FoldedSample(string Folded, string Hash, int Depth, bool Truncated, int OriginalDepth, int ThreadId)
{
    public long SampleCount { get; set; }
}

/// <summary>A completed garbage collection.</summary>
internal sealed record GcEvent(int Generation, string Reason, string Kind, long DurationNs, double TimestampMs);

/// <summary>A lock contention that actually blocked, with the stack that was waiting.</summary>
internal sealed record ContentionEvent(int ThreadId, long DurationNs, string Folded, string Hash, double TimestampMs);

/// <summary>Allocation attributed to a type, aggregated over the window.</summary>
internal sealed record AllocationSummary(string TypeName, long Bytes, long Ticks);

internal sealed record ParsedTrace(
    IReadOnlyCollection<FoldedSample> CpuSamples,
    IReadOnlyCollection<GcEvent> GarbageCollections,
    IReadOnlyCollection<ContentionEvent> Contentions,
    IReadOnlyCollection<AllocationSummary> Allocations);

/// <summary>
/// Parses a nettrace into the four things only this half can provide.
///
/// CPU samples overlap with what eBPF already produces — kept deliberately, so the
/// two techniques can be compared on identical ground. GC, contention and
/// allocation are the reason the sidecar exists at all: eBPF sees a thread parked
/// and can tell you nothing about why.
/// </summary>
internal static class NettraceReader
{
    public static ParsedTrace Read(string nettracePath, ILoggerLike log)
    {
        // Writes an intermediate .etlx at roughly 9x the nettrace size (#8). Not an
        // in-memory path, which is why the work dir needs real space.
        var etlxPath = TraceLog.CreateFromEventPipeDataFile(nettracePath);

        try
        {
            using var traceLog = TraceLog.OpenOrConvert(etlxPath);
            var source = traceLog.Events.GetSource();

            var cpu = new Dictionary<(string Hash, int Tid), FoldedSample>();
            var gcs = new List<GcEvent>();
            var contentions = new List<ContentionEvent>();
            var allocations = new Dictionary<string, (long Bytes, long Ticks)>();

            var cpuTotal = 0;
            var withoutStack = 0;

            // GCStop does not carry a duration, so starts are paired with stops on
            // (process, count) and the duration is taken from the timestamps.
            var pendingGc = new Dictionary<(int Pid, int Count), GCStartTraceData>();

            source.Clr.GCStart += data =>
            {
                pendingGc[(data.ProcessID, data.Count)] = (GCStartTraceData)data.Clone();
            };

            source.Clr.GCStop += data =>
            {
                if (!pendingGc.Remove((data.ProcessID, data.Count), out var start)) return;
                var durationNs = (long)((data.TimeStampRelativeMSec - start.TimeStampRelativeMSec) * 1_000_000);
                gcs.Add(new GcEvent(
                    Generation: start.Depth,
                    Reason: start.Reason.ToString(),
                    Kind: start.Type.ToString(),
                    DurationNs: durationNs,
                    TimestampMs: start.TimeStampRelativeMSec));
            };

            source.Clr.ContentionStop += data =>
            {
                // Only blocking contention is interesting. A spin that resolved
                // without parking is not a lock problem, and including it would
                // drown the signal we are after.
                if (data.ContentionFlags != ContentionFlags.Managed) return;

                var (folded, _) = Folding.Fold(data.CallStack());
                var (cut, _) = Folding.Truncate(folded);
                contentions.Add(new ContentionEvent(
                    ThreadId: data.ThreadID,
                    DurationNs: (long)data.DurationNs,
                    Folded: cut,
                    Hash: Folding.Hash(folded),
                    TimestampMs: data.TimeStampRelativeMSec));
            };

            source.Clr.GCAllocationTick += data =>
            {
                // One tick per ~100KB allocated, attributed to a type. Aggregated
                // rather than emitted per-event: at allocation-heavy load the raw
                // ticks would dominate the record count for very little extra
                // information.
                var type = string.IsNullOrEmpty(data.TypeName) ? "<unknown>" : data.TypeName;
                var current = allocations.GetValueOrDefault(type);
                allocations[type] = (current.Bytes + data.AllocationAmount64, current.Ticks + 1);
            };

            source.AllEvents += ev =>
            {
                if (ev.ProviderName != "Microsoft-DotNETCore-SampleProfiler") return;
                cpuTotal++;

                var stack = ev.CallStack();
                if (stack is null) { withoutStack++; return; }

                var (folded, depth) = Folding.Fold(stack);
                if (folded.Length == 0) { withoutStack++; return; }

                // Hash the UNCUT stack: two stacks truncated to the same visible
                // prefix are still different stacks.
                var hash = Folding.Hash(folded);
                var (cut, truncated) = Folding.Truncate(folded);
                var key = (hash, ev.ThreadID);

                if (!cpu.TryGetValue(key, out var sample))
                {
                    sample = new FoldedSample(cut, hash, depth, truncated, depth, ev.ThreadID);
                    cpu[key] = sample;
                }
                sample.SampleCount++;
            };

            source.Process();

            log.Info(
                $"parsed {cpuTotal} CPU samples into {cpu.Count} (stack,thread) groups " +
                $"({withoutStack} without a usable stack); " +
                $"{gcs.Count} GCs, {contentions.Count} blocking contentions, " +
                $"{allocations.Count} allocation types");

            var allocSummaries = allocations
                .Select(kv => new AllocationSummary(kv.Key, kv.Value.Bytes, kv.Value.Ticks))
                .OrderByDescending(a => a.Bytes)
                // Long tail of one-off types adds records without adding insight.
                .Take(50)
                .ToList();

            return new ParsedTrace(cpu.Values, gcs, contentions, allocSummaries);
        }
        finally
        {
            TryDelete(etlxPath, log);
        }
    }

    private static void TryDelete(string path, ILoggerLike log)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            // Logged rather than swallowed: the .etlx is ~9x the nettrace, so
            // leaking them fills the volume and the next session fails for a reason
            // that looks nothing like disk space.
            log.Warn($"could not delete intermediate {path}: {ex.Message}");
        }
    }
}
