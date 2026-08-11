using System.Text;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace ProfileAgent;

/// <summary>
/// Turns a TraceEvent call stack into the same folded-stack shape the eBPF
/// connector emits, so the two halves reassemble in DQL rather than merely
/// coexisting.
///
/// Anything changed here must change in
/// connector/profilestologsconnector/fold.go too. The whole design rests on both
/// producers agreeing on the record shape.
/// </summary>
internal static class Folding
{
    /// <summary>Guard below the platform's silent 32,768-character attribute ceiling.</summary>
    public const int MaxFoldedChars = 30_000;

    /// <summary>
    /// Walks a call stack into a root-first, semicolon-separated frame list.
    ///
    /// TraceEvent's CallStack() is LEAF-first — each frame points at its Caller —
    /// which is the same trap as the OTLP spec storing location_indices leaf-first.
    /// Get it backwards and the flame graph renders upside down while looking
    /// entirely plausible.
    /// </summary>
    public static (string Folded, int Depth) Fold(TraceCallStack? stack)
    {
        if (stack is null) return (string.Empty, 0);

        var frames = new List<string>(32);
        for (var frame = stack; frame is not null; frame = frame.Caller)
        {
            var name = frame.CodeAddress.FullMethodName;
            if (string.IsNullOrEmpty(name))
            {
                // Unresolved managed or native frame. Keep it with the module name
                // rather than dropping it: a silently shortened stack misrepresents
                // the call path, and on Alpine unsymbolised native frames are the
                // norm rather than the exception.
                var module = frame.CodeAddress.ModuleName;
                name = string.IsNullOrEmpty(module)
                    ? "<unknown>"
                    : $"{module}+0x{frame.CodeAddress.Address:x}";
            }
            frames.Add(name);
        }

        frames.Reverse();
        return (string.Join(';', frames), frames.Count);
    }

    /// <summary>
    /// FNV-1a over the folded stack. Must match the Go connector's stackHash byte
    /// for byte, or records from the two halves will not group together.
    ///
    /// Hashes the stack ALONE — never the thread. Records are grained by
    /// (stack, thread), but the flame-graph query collapses across threads by
    /// grouping on this hash.
    /// </summary>
    public static string Hash(string folded)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;

        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(folded))
        {
            hash ^= b;
            hash *= prime;
        }
        return hash.ToString("x");
    }

    /// <summary>
    /// Cuts from the ROOT end, preserving leaf frames — they carry the hotspot,
    /// which is what the graph is read for. Losing outermost frames costs context;
    /// losing innermost costs the answer.
    /// </summary>
    public static (string Folded, bool Truncated) Truncate(string folded, int max = MaxFoldedChars)
    {
        if (folded.Length <= max) return (folded, false);

        var cut = folded[^max..];
        var firstSep = cut.IndexOf(';');
        if (firstSep >= 0 && firstSep + 1 < cut.Length) cut = cut[(firstSep + 1)..];
        return (cut, true);
    }
}
