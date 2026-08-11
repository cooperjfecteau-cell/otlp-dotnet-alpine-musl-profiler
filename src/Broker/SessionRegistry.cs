using System.Text.Json;
using System.Text.Json.Serialization;
using k8s;
using k8s.Models;

namespace Broker;

internal sealed record SessionRequest(
    string Service,
    string? Namespace,
    int DurationSeconds,
    string? ProblemEventId,
    string? EntityId);

internal sealed record SessionState
{
    public required string SessionId { get; init; }
    public required string Service { get; init; }
    public required string Namespace { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset ExpectedReadyAt { get; init; }
    public string? ProblemEventId { get; init; }
    public string? EntityId { get; init; }
    public string State { get; set; } = "collecting";
    public IReadOnlyList<string> Pods { get; set; } = Array.Empty<string>();
    public string? ViewerUrl { get; set; }
}

/// <summary>
/// The gate written on disk, in the exact shape both consumers already read: the
/// collector's profilestologs connector and the profile agent.
/// </summary>
internal sealed record GateEntry
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("service_name")] public required string ServiceName { get; init; }
    [JsonPropertyName("namespace")] public string? Namespace { get; init; }
    [JsonPropertyName("start_unix_nano")] public long StartUnixNano { get; init; }
    [JsonPropertyName("end_unix_nano")] public long EndUnixNano { get; init; }
}

/// <summary>
/// Owns session lifecycle and the ConfigMap that gates both halves.
///
/// Writing that one ConfigMap is the whole activation mechanism. The eBPF connector
/// and the EventPipe agent both watch it, so the broker never talks to
/// dotnet-monitor, never enumerates pods to push to, and never fans out N calls it
/// would have to retry individually.
/// </summary>
internal sealed class SessionRegistry(
    IKubernetes kube,
    string configMapNamespace,
    string configMapName,
    string viewerBaseUrl,
    ILogger<SessionRegistry> log)
{
    private readonly Dictionary<string, SessionState> _sessions = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<(SessionState State, bool Created, SessionState? Conflict)> OpenAsync(
        SessionRequest req, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Expire();

            // Idempotent on problemEventId: workflows retry, and a re-opened problem
            // fires again. Returning the existing session is what stops one problem
            // quietly spawning several overlapping profiles, each paying full ingest.
            if (!string.IsNullOrEmpty(req.ProblemEventId))
            {
                var existing = _sessions.Values.FirstOrDefault(s =>
                    s.ProblemEventId == req.ProblemEventId && s.State == "collecting");
                if (existing is not null)
                {
                    log.LogInformation("idempotent hit for problem {Problem}: returning {Session}",
                        req.ProblemEventId, existing.SessionId);
                    return (existing, false, null);
                }
            }

            // One active session per service. The unit is the workload, not the
            // cluster: EventPipe buffer memory is charged to the application
            // container, so two concurrent traces on one workload double the memory
            // pressure on the thing being observed. Two unrelated services share no
            // such overhead, so they are deliberately allowed to run at once.
            var conflict = _sessions.Values.FirstOrDefault(s =>
                s.Service == req.Service && s.State == "collecting");
            if (conflict is not null)
            {
                log.LogInformation("rejecting session for {Service}: {Existing} still collecting",
                    req.Service, conflict.SessionId);
                return (conflict, false, conflict);
            }

            var ns = string.IsNullOrEmpty(req.Namespace) ? configMapNamespace : req.Namespace;
            var id = Ulid.New();
            var now = DateTimeOffset.UtcNow;

            var state = new SessionState
            {
                SessionId = id,
                Service = req.Service,
                Namespace = ns,
                StartedAt = now,
                // Includes deliberate slack. The gate is a mounted ConfigMap, and
                // propagation to the kubelet's view was measured at 95-100s -- the
                // session does not actually begin when this method returns, and
                // pretending otherwise would make every session lose its opening.
                ExpectedReadyAt = now.AddSeconds(req.DurationSeconds + 150),
                ProblemEventId = req.ProblemEventId,
                EntityId = req.EntityId,
                ViewerUrl = $"{viewerBaseUrl.TrimEnd('/')}/session/{id}",
            };

            _sessions[id] = state;
            try
            {
                await WriteGateAsync(ct);
            }
            catch
            {
                // Roll back. Leaving the session in memory after a failed gate write
                // is worse than failing outright: it would block every subsequent
                // request for this service with a 409 that points at a session which
                // is not collecting anything, and nothing would recover it until the
                // hour-long expiry elapsed.
                _sessions.Remove(id);
                throw;
            }
            return (state, true, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> CloseAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_sessions.TryGetValue(id, out var s)) return false;
            // Closes the gate only. An in-flight EventPipe trace is deliberately NOT
            // aborted: terminating one early yields no usable nettrace (#4), so the
            // managed half is left to finish and publish.
            s.State = "processing";
            await WriteGateAsync(ct);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyCollection<SessionState> All()
    {
        lock (_sessions) return _sessions.Values.ToList();
    }

    public SessionState? Get(string id) =>
        _sessions.TryGetValue(id, out var s) ? s : null;

    private void Expire()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var s in _sessions.Values.Where(s => s.State == "collecting" && now > s.ExpectedReadyAt))
        {
            s.State = "ready";
        }
        // Keep an hour of history so a workflow polling after the fact still gets an
        // answer rather than a 404 that looks like the session never existed.
        var cutoff = now.AddHours(-1);
        foreach (var stale in _sessions.Values.Where(s => s.StartedAt < cutoff).ToList())
        {
            _sessions.Remove(stale.SessionId);
        }
    }

    private async Task WriteGateAsync(CancellationToken ct)
    {
        var open = _sessions.Values
            .Where(s => s.State == "collecting")
            .Select(s => new GateEntry
            {
                Id = s.SessionId,
                ServiceName = s.Service,
                Namespace = s.Namespace,
                StartUnixNano = s.StartedAt.ToUnixTimeMilliseconds() * 1_000_000,
                EndUnixNano = s.ExpectedReadyAt.ToUnixTimeMilliseconds() * 1_000_000,
            })
            .ToList();

        var json = JsonSerializer.Serialize(open);

        try
        {
            // Read-modify-write rather than constructing a bare object. Replace
            // needs the complete resource -- metadata.name included -- and passing
            // only Data fails. It also preserves any other keys in the ConfigMap
            // instead of silently deleting them.
            var current = await kube.CoreV1.ReadNamespacedConfigMapAsync(
                configMapName, configMapNamespace, cancellationToken: ct);

            current.Data ??= new Dictionary<string, string>();
            current.Data["sessions.json"] = json;

            await kube.CoreV1.ReplaceNamespacedConfigMapAsync(
                current, configMapName, configMapNamespace, cancellationToken: ct);
            log.LogInformation("gate updated: {Count} open session(s)", open.Count);
        }
        catch (Exception ex)
        {
            // Loud on purpose. If the gate write fails the session exists only in
            // this process's memory: the API returned 202, the caller believes
            // profiling started, and nothing is collecting. Silent failure here is
            // the worst outcome in the whole control plane.
            log.LogError(ex, "FAILED to write the session gate -- no profiling will occur");
            throw;
        }
    }
}
