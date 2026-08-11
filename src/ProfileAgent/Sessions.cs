using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProfileAgent;

/// <summary>
/// One profiling window opened by the broker. Same file, same shape the collector's
/// connector reads — deliberately, so the eBPF and EventPipe halves are gated by a
/// single source of truth rather than two mechanisms that can disagree.
/// </summary>
internal sealed record Session
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("service_name")] public string? ServiceName { get; init; }
    [JsonPropertyName("namespace")] public string? Namespace { get; init; }
    [JsonPropertyName("start_unix_nano")] public long StartUnixNano { get; init; }
    [JsonPropertyName("end_unix_nano")] public long EndUnixNano { get; init; }

    public bool Matches(string service) =>
        string.IsNullOrEmpty(ServiceName) || ServiceName == service;

    public bool ActiveAt(long nowNanos) =>
        (StartUnixNano == 0 || nowNanos >= StartUnixNano) &&
        (EndUnixNano == 0 || nowNanos <= EndUnixNano);

    /// <summary>Seconds left, floored at zero — what dotnet-monitor needs up front.</summary>
    public int RemainingSeconds(long nowNanos) =>
        EndUnixNano == 0 ? 0 : (int)Math.Max(0, (EndUnixNano - nowNanos) / 1_000_000_000);
}

internal static class SessionFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Reads the active session set. Fails CLOSED in every error case: a missing
    /// file means no sessions, and a malformed one also means no sessions.
    ///
    /// It must never be read as "profile everything" — that would turn a typo in a
    /// ConfigMap into an unbounded ingest bill and, here, into unbounded EventPipe
    /// buffer pressure on the application container.
    /// </summary>
    public static IReadOnlyList<Session> Read(string path, ILoggerLike log)
    {
        try
        {
            if (!File.Exists(path)) return Array.Empty<Session>();
            var raw = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<Session>();
            return JsonSerializer.Deserialize<List<Session>>(raw, Options) ?? new List<Session>();
        }
        catch (Exception ex)
        {
            log.Warn($"session file unreadable, treating as no sessions: {ex.Message}");
            return Array.Empty<Session>();
        }
    }
}

/// <summary>Minimal logging seam so Sessions has no dependency on the host's logger type.</summary>
internal interface ILoggerLike
{
    void Info(string message);
    void Warn(string message);
}
