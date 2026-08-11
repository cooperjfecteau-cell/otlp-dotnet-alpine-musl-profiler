using System.Diagnostics.Tracing;

namespace ProfileAgent;

/// <summary>
/// Forwards the OpenTelemetry SDK's internal EventSource to our log.
///
/// This exists because of a specific failure: the collector had no logs pipeline
/// wired to its OTLP receiver, so every record the agent exported was rejected —
/// and nothing said so. The SDK routes exporter failures to its own EventSource
/// rather than to ILogger, so the agent reported "published 590 records" for
/// records that never left the process, and the absence was only noticed by
/// querying the backend and finding nothing there.
///
/// A profiler that cannot tell you its own exports are failing is worse than one
/// that does not export at all: the first looks like it works.
/// </summary>
internal sealed class OtelSelfDiagnostics : EventListener
{
    private readonly ILoggerLike _log;

    // Set before base construction completes, because OnEventSourceCreated can fire
    // during the base constructor — before our own field assignment would have run.
    public OtelSelfDiagnostics(ILoggerLike log)
    {
        _log = log;
        foreach (var source in _pending) Enable(source);
        _pending.Clear();
    }

    private readonly List<EventSource> _pending = new();

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (!eventSource.Name.StartsWith("OpenTelemetry", StringComparison.Ordinal)) return;

        if (_log is null) _pending.Add(eventSource);
        else Enable(eventSource);
    }

    private void Enable(EventSource source) =>
        EnableEvents(source, EventLevel.Warning, EventKeywords.All);

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (_log is null) return;

        var payload = e.Payload is null ? "" : string.Join(" | ", e.Payload);
        var message = string.IsNullOrEmpty(e.Message) ? e.EventName : e.Message;
        _log.Warn($"otel-sdk[{e.EventSource.Name}] {message} {payload}".Trim());
    }
}
