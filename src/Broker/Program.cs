using Broker;
using k8s;

// The single endpoint a Dynatrace workflow calls to say "profile this service for
// 90 seconds because of this problem".
//
// It is deliberately small. Writing one ConfigMap opens the gate for BOTH halves —
// the eBPF connector and the EventPipe agent each watch it — so the broker never
// talks to dotnet-monitor, never enumerates pods to push to, and never fans out
// calls it would have to retry individually.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();

var cfgNamespace = Env("PROFILER_NAMESPACE", "dotnet-profiler");
var cfgMapName = Env("PROFILER_SESSION_CONFIGMAP", "profiler-sessions");
var viewerBase = Env("PROFILER_VIEWER_URL", "https://example.apps.dynatrace.com/ui/apps/profiler");
var dtEndpoint = Env("DT_ENDPOINT", "");
var dtToken = Env("DT_API_TOKEN", "");
var sharedSecret = Env("BROKER_TOKEN", "");

builder.Services.AddSingleton<IKubernetes>(_ =>
    new Kubernetes(KubernetesClientConfiguration.InClusterConfig()));
builder.Services.AddSingleton(sp => new SessionRegistry(
    sp.GetRequiredService<IKubernetes>(),
    cfgNamespace, cfgMapName, viewerBase,
    sp.GetRequiredService<ILogger<SessionRegistry>>()));
builder.Services.AddSingleton(sp => new DynatraceEvents(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
    dtEndpoint, dtToken,
    sp.GetRequiredService<ILogger<DynatraceEvents>>()));

var app = builder.Build();
var log = app.Services.GetRequiredService<ILogger<Program>>();

if (string.IsNullOrEmpty(sharedSecret))
{
    // Refuse to be quietly open. This endpoint mutates cluster state and triggers
    // billable ingest; running it unauthenticated should be a deliberate act, not
    // something that happens because a secret was not wired up.
    log.LogWarning("BROKER_TOKEN is not set — every request will be rejected. Set it, or set BROKER_ALLOW_ANONYMOUS=true to opt out explicitly.");
}
var allowAnonymous = Env("BROKER_ALLOW_ANONYMOUS", "false") == "true";

app.MapGet("/healthz", () => Results.Ok("ok"));

app.MapPost("/sessions", async (
    SessionRequest req, HttpRequest http, SessionRegistry registry, DynatraceEvents events, CancellationToken ct) =>
{
    if (!Authorised(http)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(req.Service)) return Results.BadRequest(new { error = "service is required" });

    var duration = req.DurationSeconds > 0 ? req.DurationSeconds : 90;
    var (state, created, conflict) = await registry.OpenAsync(req with { DurationSeconds = duration }, ct);

    if (conflict is not null)
    {
        // 409 is not an error to surface to a human. It means "already being
        // profiled", and the existing session's annotation already carries the deep
        // link this trigger would have produced.
        return Results.Json(new
        {
            error = "a session is already collecting for this service",
            conflictingSessionId = conflict.SessionId,
            expectedReadyAt = conflict.ExpectedReadyAt,
            viewerUrl = conflict.ViewerUrl,
        }, statusCode: StatusCodes.Status409Conflict);
    }

    if (created)
    {
        // "capture in progress" now; the same annotation.id is overwritten with the
        // finished link when the window closes.
        await events.AnnotateProblemAsync(state,
            $"Continuous profile **in progress** for `{state.Service}` ({duration}s).\n\n[Open profile]({state.ViewerUrl})", ct);
        await events.MarkServiceAsync(state, ct);
    }

    return Results.Accepted($"/sessions/{state.SessionId}", Describe(state));
});

app.MapGet("/sessions", (HttpRequest http, SessionRegistry registry) =>
    !Authorised(http) ? Results.Unauthorized() : Results.Ok(registry.All().Select(Describe)));

app.MapGet("/sessions/{id}", (string id, HttpRequest http, SessionRegistry registry) =>
{
    if (!Authorised(http)) return Results.Unauthorized();
    var s = registry.Get(id);
    return s is null ? Results.NotFound() : Results.Ok(Describe(s));
});

app.MapDelete("/sessions/{id}", async (
    string id, HttpRequest http, SessionRegistry registry, DynatraceEvents events, CancellationToken ct) =>
{
    if (!Authorised(http)) return Results.Unauthorized();
    if (!await registry.CloseAsync(id, ct)) return Results.NotFound();

    var s = registry.Get(id)!;
    await events.AnnotateProblemAsync(s,
        $"Continuous profile **captured** for `{s.Service}`.\n\n[Open profile]({s.ViewerUrl})", ct);
    return Results.Ok(Describe(s));
});

app.Run();
return;


bool Authorised(HttpRequest http)
{
    if (allowAnonymous) return true;
    if (string.IsNullOrEmpty(sharedSecret)) return false;
    return http.Headers.TryGetValue("X-Broker-Token", out var v) &&
           CryptographicEquals(v.ToString(), sharedSecret);
}

// Constant-time compare: this is a bearer secret, and a timing side channel on a
// string compare is a real if unglamorous way to leak one.
static bool CryptographicEquals(string a, string b)
{
    if (a.Length != b.Length) return false;
    var diff = 0;
    for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
    return diff == 0;
}

static object Describe(SessionState s) => new
{
    sessionId = s.SessionId,
    state = s.State,
    service = s.Service,
    startedAt = s.StartedAt,
    expectedReadyAt = s.ExpectedReadyAt,
    viewerUrl = s.ViewerUrl,
    problemEventId = s.ProblemEventId,
};

static string Env(string key, string fallback) =>
    Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;
