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

// Surface exporter failures. Without this the SDK swallows them into its own
// diagnostic channel, so the agent happily reports "published N records" for
// records that never left the process.
using var selfDiag = new OtelSelfDiagnostics();
selfDiag.Attach(log);

using var otelFactory = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
{
    o.SetResourceBuilder(resource);
    o.AddOtlpExporter((_, processor) =>
    {
        // The default queue is 2048 records. A session publishes its entire result
        // in one burst — thousands of records in a tight loop — so the default
        // silently drops most of them: 7,266 contention records were emitted and
        // 733 arrived. Sized for a large session with headroom.
        processor.BatchExportProcessorOptions.MaxQueueSize = 32768;
        processor.BatchExportProcessorOptions.MaxExportBatchSize = 1024;
        processor.BatchExportProcessorOptions.ScheduledDelayMilliseconds = 1000;
    });
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
        var parsed = NettraceReader.Read(path, log);
        var published = Publish(parsed, session, duration);
        log.Info($"session {session.Id}: published {published} records");
    }
    finally
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}

int Publish(ParsedTrace parsed, Session session, int durationSeconds)
{
    // The SampleProfiler's interval. Used to derive cpu_ns rather than leaving it
    // at zero, which is the defect that made every duration-weighted query on the
    // old pipeline silently return nothing.
    const long sampleIntervalNanos = 1_000_000; // 1ms

    var windowStart = session.StartUnixNano;
    var windowDuration = (long)durationSeconds * 1_000_000_000L;
    var count = 0;

    // Attributes every record carries, whatever its type. Keeping these identical
    // across event types is what lets one DQL query filter a whole session without
    // knowing which producer or which signal it is looking at.
    List<KeyValuePair<string, object?>> Common(string eventType) => new()
    {
        new("log.source", "continuous_profiler"),
        new("profile.schema_version", "otlp-profiles-v1development/1"),
        new("profile.session_id", session.Id),
        new("profile.window_start_ns", windowStart),
        new("profile.window_duration_ns", windowDuration),
        // Distinguishes the two producers when comparing them in DQL. They are
        // meant to agree on CPU; being able to check that is the point.
        new("profile.source", "eventpipe"),
        new("profile.event_type", eventType),
    };

    void Emit(List<KeyValuePair<string, object?>> attrs, string body)
    {
        records.Log(LogLevel.Information, default, attrs, null, (_, _) => body);
        count++;
    }

    foreach (var s in parsed.CpuSamples)
    {
        var attrs = Common("cpu_sample");
        attrs.Add(new("profile.stack.folded", s.Folded));
        attrs.Add(new("profile.stack.hash", s.Hash));
        attrs.Add(new("profile.stack.depth", s.Depth));
        attrs.Add(new("profile.sample_count", s.SampleCount));
        attrs.Add(new("profile.cpu_ns", s.SampleCount * sampleIntervalNanos));
        attrs.Add(new("profile.period_ns", sampleIntervalNanos));
        // The join key to the eBPF half. Same namespace on both sides (#8), so no
        // managed-to-OS translation is needed or attempted.
        attrs.Add(new("thread.id", s.ThreadId));

        if (s.Truncated)
        {
            attrs.Add(new("profile.stack.truncated", true));
            attrs.Add(new("profile.stack.original_depth", s.OriginalDepth));
        }

        Emit(attrs, "profile sample");
    }

    // Everything below is what eBPF structurally cannot produce. It sees a thread
    // parked and can say nothing about why; these say why.

    foreach (var gc in parsed.GarbageCollections)
    {
        var attrs = Common("gc");
        attrs.Add(new("gc.generation", gc.Generation));
        attrs.Add(new("gc.reason", gc.Reason));
        attrs.Add(new("gc.kind", gc.Kind));
        attrs.Add(new("gc.duration_ns", gc.DurationNs));
        attrs.Add(new("profile.event_offset_ms", gc.TimestampMs));
        Emit(attrs, $"gc gen{gc.Generation} {gc.Reason}");
    }

    foreach (var c in parsed.Contentions)
    {
        var attrs = Common("contention");
        attrs.Add(new("contention.count", c.Count));
        attrs.Add(new("contention.total_duration_ns", c.TotalDurationNs));
        attrs.Add(new("contention.max_duration_ns", c.MaxDurationNs));
        // The waiting stack, folded the same way CPU samples are, so contention
        // renders as a flame graph on the same machinery — weighted by wait time
        // rather than sample count.
        attrs.Add(new("profile.stack.folded", c.Folded));
        attrs.Add(new("profile.stack.hash", c.Hash));
        attrs.Add(new("thread.id", c.ThreadId));
        Emit(attrs, "lock contention");
    }

    foreach (var a in parsed.Allocations)
    {
        var attrs = Common("allocation");
        attrs.Add(new("allocation.type", a.TypeName));
        attrs.Add(new("allocation.bytes", a.Bytes));
        attrs.Add(new("allocation.tick_count", a.Ticks));
        Emit(attrs, $"allocation {a.TypeName}");
    }

    return count;
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
