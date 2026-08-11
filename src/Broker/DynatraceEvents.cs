using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Broker;

/// <summary>
/// Pushes the two events that make a profile discoverable from a problem.
///
/// Both go to the same endpoint with the same scope; only eventType and addressing
/// differ. Note there is no API to attach a Davis event to a problem by id —
/// problem membership is decided by Davis correlation alone. The mechanism that
/// does work is a problem ANNOTATION (#5).
/// </summary>
internal sealed class DynatraceEvents(HttpClient http, string endpoint, string token, ILogger<DynatraceEvents> log)
{
    /// <summary>
    /// Annotation on the triggering problem.
    ///
    /// Idempotent on annotation.id, which is exploited deliberately: this is called
    /// once at session start with "capture in progress" and again at the end with
    /// the finished link, overwriting rather than adding a second comment. Retries
    /// are therefore free.
    /// </summary>
    public Task<bool> AnnotateProblemAsync(SessionState s, string description, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(s.ProblemEventId))
        {
            log.LogDebug("no problem event id on {Session}; skipping annotation", s.SessionId);
            return Task.FromResult(true);
        }

        var payload = new
        {
            eventType = "CUSTOM_ANNOTATION",
            title = "Continuous profile captured",
            properties = new Dictionary<string, string>
            {
                // The INTERNAL event id, not the P-… display id. Passing the display
                // id fails silently, which is the kind of bug that takes a day.
                ["annotation.problem_ids"] = s.ProblemEventId,
                ["annotation.id"] = s.SessionId,
                ["annotation.source"] = "otlp-dotnet-alpine-musl-profiler",
                ["annotation.url"] = s.ViewerUrl ?? "",
                ["dt.event.description"] = description,
                // Carried separately from the URL so DQL never has to parse a link.
                ["profiler.session_id"] = s.SessionId,
            },
        };
        return PostAsync(payload, $"annotation for {s.SessionId}", ct);
    }

    /// <summary>
    /// Custom event on the service entity. Renders as a band spanning start to end,
    /// so a 90-second profile shows as a span on entity charts rather than a point.
    /// </summary>
    public Task<bool> MarkServiceAsync(SessionState s, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(s.EntityId))
        {
            log.LogDebug("no entity id on {Session}; skipping service event", s.SessionId);
            return Task.FromResult(true);
        }

        var payload = new
        {
            eventType = "CUSTOM_INFO",
            title = "Continuous profile captured",
            entitySelector = $"type(SERVICE),entityId(\"{s.EntityId}\")",
            startTime = s.StartedAt.ToUnixTimeMilliseconds(),
            endTime = s.ExpectedReadyAt.ToUnixTimeMilliseconds(),
            properties = new Dictionary<string, string>
            {
                ["profiler.session_id"] = s.SessionId,
                ["profiler.viewer_url"] = s.ViewerUrl ?? "",
            },
        };
        return PostAsync(payload, $"service event for {s.SessionId}", ct);
    }

    private async Task<bool> PostAsync(object payload, string what, CancellationToken ct)
    {
        var url = $"{endpoint.TrimEnd('/')}/api/v2/events/ingest";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Api-Token", token);

        try
        {
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("{What} rejected: {Status} {Body}", what, (int)resp.StatusCode, Trim(body));
                return false;
            }

            // The endpoint returns 201 even when entity mapping FAILED — the real
            // status is inside eventIngestResults[]. Trusting the status code means
            // the event silently lands at environment level and nobody ever sees it
            // on the problem. This check is the whole reason for reading the body.
            if (body.Contains("\"status\"", StringComparison.Ordinal) &&
                !body.Contains("\"OK\"", StringComparison.OrdinalIgnoreCase))
            {
                log.LogWarning(
                    "{What} accepted with a non-OK ingest result -- it may have been mapped to environment level instead of the entity: {Body}",
                    what, Trim(body));
                return false;
            }

            log.LogInformation("{What} pushed", what);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "{What} failed to send", what);
            return false;
        }
    }

    private static string Trim(string s) => s.Length <= 400 ? s : s[..400];
}
