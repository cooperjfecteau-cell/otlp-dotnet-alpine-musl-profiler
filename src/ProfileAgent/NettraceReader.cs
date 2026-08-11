using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace ProfileAgent;

internal sealed record FoldedSample(string Folded, string Hash, int Depth, bool Truncated, int OriginalDepth, int ThreadId)
{
    public long SampleCount { get; set; }
}

/// <summary>
/// Parses a nettrace file into (stack, thread) groups matching the eBPF connector's
/// record grain.
/// </summary>
internal static class NettraceReader
{
    /// <summary>
    /// Reads CPU samples and folds them.
    ///
    /// Note the grain: keyed on (stack hash, OS thread id), not stack alone. The
    /// OTLP profiles spec defines Sample identity as {stack, attributes, link} with
    /// thread.id among the attributes, so one stack seen on five threads is five
    /// samples. Merging them would force an arbitrary thread onto the record and
    /// destroy the join to the eBPF half.
    ///
    /// The thread id used is TraceEvent's ThreadID, which #8 verified is the real
    /// Linux TID — the same namespace eBPF reports, so no translation is needed.
    /// </summary>
    public static IReadOnlyCollection<FoldedSample> Read(string nettracePath, ILoggerLike log)
    {
        // CreateFromEventPipeDataFile writes an intermediate .etlx to disk at
        // roughly 9x the nettrace size (#8). It is not an in-memory path, which is
        // why the sidecar needs real scratch space rather than a token emptyDir.
        var etlxPath = TraceLog.CreateFromEventPipeDataFile(nettracePath);

        try
        {
            using var traceLog = TraceLog.OpenOrConvert(etlxPath);
            var groups = new Dictionary<(string Hash, int Tid), FoldedSample>();
            var withoutStack = 0;
            var total = 0;

            foreach (var ev in traceLog.Events)
            {
                if (!IsCpuSample(ev)) continue;
                total++;

                var stack = ev.CallStack();
                if (stack is null) { withoutStack++; continue; }

                var (folded, depth) = Folding.Fold(stack);
                if (folded.Length == 0) { withoutStack++; continue; }

                // Hash the UNCUT stack: two stacks truncated to the same visible
                // prefix are still different stacks, and collapsing them would
                // overstate whichever survived.
                var hash = Folding.Hash(folded);
                var (cut, truncated) = Folding.Truncate(folded);
                var key = (hash, ev.ThreadID);

                if (!groups.TryGetValue(key, out var sample))
                {
                    sample = new FoldedSample(cut, hash, depth, truncated, depth, ev.ThreadID);
                    groups[key] = sample;
                }
                sample.SampleCount++;
            }

            log.Info($"parsed {total} CPU samples into {groups.Count} (stack,thread) groups; {withoutStack} without a usable stack");
            return groups.Values;
        }
        finally
        {
            TryDelete(etlxPath, log);
        }
    }

    private static bool IsCpuSample(TraceEvent ev) =>
        ev.ProviderName == "Microsoft-DotNETCore-SampleProfiler";

    private static void TryDelete(string path, ILoggerLike log)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            // Worth logging rather than swallowing: the .etlx is ~9x the nettrace,
            // so leaking them fills the volume and the next session fails for a
            // reason that looks nothing like disk space.
            log.Warn($"could not delete intermediate {path}: {ex.Message}");
        }
    }
}
