using System.Diagnostics.Tracing;

namespace ProfileAgent;

/// <summary>
/// Forwards the OpenTelemetry SDK's internal EventSource to our log.
///
/// This exists because of a specific failure: the collector had no logs pipeline
/// wired to its OTLP receiver, so every record the agent exported was rejected —
/// and nothing said so. The SDK routes exporter failures to its own EventSource
/// rather than to ILogger, so the agent reported "published 590 records" for
/// records that never left the process.
///
/// A profiler that cannot tell you its own exports are failing is worse than one
/// that does not export at all: the first looks like it works.
///
/// NOTE on the shape below: EventListener's base constructor can raise
/// OnEventSourceCreated BEFORE derived field initializers have run, so this class
/// must not touch any instance field it initialised inline. An earlier version
/// buffered sources into a List created by a field initializer; the callback fired
/// first, hit a null list, and the listener silently never attached — which is why
/// the very drop it was meant to report went unnoticed.
/// </summary>
internal sealed class OtelSelfDiagnostics : EventListener
{
    // Assigned after construction, deliberately: it cannot be relied upon inside
    // OnEventSourceCreated. Everything here tolerates it being null.
    private volatile ILoggerLike? _log;

    public void Attach(ILoggerLike log) => _log = log;

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        // Enabling needs no instance state, so it is safe this early.
        if (eventSource.Name.StartsWith("OpenTelemetry", StringComparison.Ordinal))
        {
            EnableEvents(eventSource, EventLevel.Warning, EventKeywords.All);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        var payload = e.Payload is null ? "" : string.Join(" | ", e.Payload);
        var message = string.IsNullOrEmpty(e.Message) ? e.EventName : e.Message;
        var line = $"otel-sdk[{e.EventSource.Name}] {message} {payload}".Trim();

        var log = _log;
        if (log is null) Console.Error.WriteLine(line);
        else log.Warn(line);
    }
}
