using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using ProfileAgent;

// EventPipe half of the pipeline.
//
// Watches the same session file the collector's connector reads, and when a session
// opens for this workload: drives dotnet-monitor for a fixed window, parses the
// nettrace with TraceEvent, folds stacks, and exports OTLP logs in the same shape
// the eBPF connector emits — so the two halves reassemble in DQL.
//
// What only this half can provide: line numbers, inlined frames, GC, allocation and
// thread contention. eBPF covers CPU frames on its own (#7).

var cfg = new AgentConfig();
using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss "));
var hostLog = loggerFactory.CreateLogger("agent");
var log = new ConsoleLog(hostLog);

log.Info($"service={cfg.ServiceName} monitor={cfg.MonitorUrl} sessions={cfg.SessionFile}");

var resource = ResourceBuilder.CreateDefault()
    .AddService(cfg.ServiceName)
    .AddAttributes(new Dictionary<string, object>
    {
        ["dt.openpipeline.source"] = "dotnet-profiler",
        ["k8s.pod.name"] = cfg.PodName,
        ["k8s.namespace.name"] = cfg.Namespace,
        ["k8s.node.name"] = cfg.NodeName,
        ["telemetry.sdk.name"] = "otlp-dotnet-alpine-musl-profiler/profile-agent",
    });

using var otelFactory = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
{
    o.SetResourceBuilder(resource);
    o.AddOtlpExporter();
}));
var records = otelFactory.CreateLogger("profile");

var monitor = new DotnetMonitorClient(cfg.MonitorUrl, log);
var handled = new HashSet<string>();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

while (!cts.IsCancellationRequested)
{
    try
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
        var session = SessionFile.Read(cfg.SessionFile, log)
            .FirstOrDefault(s => s.ActiveAt(now) && s.Matches(cfg.ServiceName));

        // One session per pod is enforced by the broker (#14), but handled[] makes
        // the agent idempotent regardless: a ConfigMap re-read must not start a
        // second trace against a session already in flight. Buffer memory is
        // charged to the application container, so a duplicate is not merely waste.
        if (session is not null && handled.Add(session.Id))
        {
            await RunSessionAsync(session, now);
        }
    }
    catch (Exception ex)
    {
        log.Warn($"poll failed: {ex.Message}");
    }

    try { await Task.Delay(cfg.PollInterval, cts.Token); }
    catch (TaskCanceledException) { break; }
}

log.Info("shutting down");
return 0;


async Task RunSessionAsync(Session session, long nowNanos)
{
    var duration = session.RemainingSeconds(nowNanos);
    if (duration <= 0) duration = cfg.DefaultDurationSeconds;

    log.Info($"session {session.Id} active; collecting for {duration}s");

    var pid = await monitor.FindTargetPidAsync(cts.Token);
    if (pid is null)
    {
        // Not fatal to the pipeline: eBPF still covers this window, which is why
        // the broker has a `partial` state rather than only success and failure.
        log.Warn("no target process visible to dotnet-monitor; skipping EventPipe for this session");
        return;
    }

    Directory.CreateDirectory(cfg.WorkDir);
    var nettrace = Path.Combine(cfg.WorkDir, $"{session.Id}.nettrace");

    var path = await monitor.CollectTraceAsync(
        pid.Value, duration, cfg.BufferSizeMb, nettrace, cts.Token);
    if (path is null) return;

    try
    {
        var samples = NettraceReader.Read(path, log);
        Publish(samples, session, duration);
        log.Info($"session {session.Id}: published {samples.Count} records");
    }
    finally
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}

void Publish(IReadOnlyCollection<FoldedSample> samples, Session session, int durationSeconds)
{
    // The sampling interval EventPipe uses for the CPU sample profiler. Used to
    // derive cpu_ns rather than leaving it at zero, which is the defect that made
    // every duration-weighted query on the old pipeline silently return nothing.
    const long sampleIntervalNanos = 1_000_000; // 1ms, the SampleProfiler default

    var windowStart = session.StartUnixNano;
    var windowDuration = (long)durationSeconds * 1_000_000_000L;

    foreach (var s in samples)
    {
        var attrs = new List<KeyValuePair<string, object?>>
        {
            new("log.source", "continuous_profiler"),
            new("profile.schema_version", "otlp-profiles-v1development/1"),
            new("profile.stack.folded", s.Folded),
            new("profile.stack.hash", s.Hash),
            new("profile.stack.depth", s.Depth),
            new("profile.sample_count", s.SampleCount),
            new("profile.cpu_ns", s.SampleCount * sampleIntervalNanos),
            new("profile.period_ns", sampleIntervalNanos),
            new("profile.window_start_ns", windowStart),
            new("profile.window_duration_ns", windowDuration),
            new("profile.session_id", session.Id),
            // The join key to the eBPF half. Same namespace on both sides (#8), so
            // no managed-to-OS translation is needed or attempted.
            new("thread.id", s.ThreadId),
            // Distinguishes the two producers when comparing them in DQL. They are
            // meant to agree; being able to check that is the point.
            new("profile.source", "eventpipe"),
        };

        if (s.Truncated)
        {
            attrs.Add(new("profile.stack.truncated", true));
            attrs.Add(new("profile.stack.original_depth", s.OriginalDepth));
        }

        records.Log(LogLevel.Information, default, attrs, null, static (_, _) => "profile sample");
    }
}


internal sealed class AgentConfig
{
    public string ServiceName { get; } = Env("OTEL_SERVICE_NAME", "unknown-service");
    public string MonitorUrl { get; } = Env("DOTNET_MONITOR_URL", "http://127.0.0.1:52323");
    public string SessionFile { get; } = Env("PROFILER_SESSION_FILE", "/etc/profiler-sessions/sessions.json");
    public string WorkDir { get; } = Env("PROFILER_WORK_DIR", "/var/tmp/profiles");
    public string PodName { get; } = Env("K8S_POD_NAME", "");
    public string Namespace { get; } = Env("K8S_NAMESPACE", "");
    public string NodeName { get; } = Env("K8S_NODE_NAME", "");
    public int BufferSizeMb { get; } = int.TryParse(Env("PROFILER_BUFFER_MB", "128"), out var v) ? v : 128;
    public int DefaultDurationSeconds { get; } = int.TryParse(Env("PROFILE_DURATION_SECONDS", "90"), out var v) ? v : 90;
    public TimeSpan PollInterval { get; } = TimeSpan.FromSeconds(5);

    private static string Env(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;
}

internal sealed class ConsoleLog(ILogger inner) : ILoggerLike
{
    public void Info(string message) => inner.LogInformation("{Message}", message);
    public void Warn(string message) => inner.LogWarning("{Message}", message);
}
