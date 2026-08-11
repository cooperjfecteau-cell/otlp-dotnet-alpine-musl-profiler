using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ProfileAgent;

/// <summary>
/// Drives dotnet-monitor's HTTP API.
///
/// Two constraints from #4 shape this whole class:
///
///  - A trace CANNOT be terminated early and still yield a usable nettrace, so the
///    duration is fixed at request time. There is no Stop().
///  - Symbol rundown can push the response well past the end of the collection
///    window, which is why "started" and "ready" are different moments and the
///    HTTP timeout must be generously longer than the duration requested.
/// </summary>
internal sealed class DotnetMonitorClient
{
    private readonly HttpClient _http;
    private readonly ILoggerLike _log;

    public DotnetMonitorClient(string baseUrl, ILoggerLike log)
    {
        _log = log;
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            // Deliberately not the profile duration: rundown happens after the
            // window closes and can take minutes on a large process. A timeout
            // equal to the duration would abort exactly the traces worth having.
            Timeout = TimeSpan.FromMinutes(10),
        };
    }

    /// <summary>
    /// Finds the process to profile.
    ///
    /// In Listen mode the runtime dials out to the sidecar's socket, so
    /// dotnet-monitor knows about the app without us telling it. We take the
    /// process that is not ourselves — in a sidecar pod there is exactly one other.
    /// </summary>
    public async Task<int?> FindTargetPidAsync(CancellationToken ct)
    {
        try
        {
            var procs = await _http.GetFromJsonAsync<List<ProcessInfo>>("processes", ct);
            if (procs is null || procs.Count == 0) return null;

            var self = Environment.ProcessId;
            var target = procs.FirstOrDefault(p => p.Pid != self) ?? procs[0];
            return target.Pid;
        }
        catch (Exception ex)
        {
            _log.Warn($"could not list processes: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Collects a trace of fixed duration and returns the raw nettrace bytes.
    ///
    /// Providers are the set established in #4: CPU samples plus GC and contention
    /// in one session. The keyword mask is ClrTraceEventParser.Keywords.Default
    /// minus the very verbose GCHeapSurvivalAndMovement bit.
    /// </summary>
    public async Task<string?> CollectTraceAsync(
        int pid, int durationSeconds, int bufferSizeMb, string outputPath, CancellationToken ct)
    {
        var body = new
        {
            Providers = new object[]
            {
                new
                {
                    Name = "Microsoft-DotNETCore-SampleProfiler",
                    EventLevel = "Informational",
                },
                new
                {
                    Name = "Microsoft-Windows-DotNETRuntime",
                    EventLevel = "Informational",
                    Keywords = "0x410F40B9",
                },
            },
            // Charged against the APPLICATION container's memory limit, not this
            // sidecar's (#4). Too high and profiling OOMKills the workload it is
            // observing, which is the failure nobody would attribute to the profiler.
            BufferSizeInMB = bufferSizeMb,
        };

        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"trace?pid={pid}&durationSeconds={durationSeconds}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        _log.Info($"starting trace pid={pid} duration={durationSeconds}s buffer={bufferSizeMb}MB");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await resp.Content.ReadAsStringAsync(ct);
            _log.Warn($"trace request rejected: {(int)resp.StatusCode} {Trim(detail)}");
            return null;
        }

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(outputPath))
        {
            await src.CopyToAsync(dst, ct);
        }

        var size = new FileInfo(outputPath).Length;
        if (size == 0)
        {
            _log.Warn("trace produced an empty nettrace file");
            return null;
        }

        _log.Info($"trace complete: {size / 1024}KB at {outputPath}");
        return outputPath;
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300];

    private sealed record ProcessInfo(int Pid, string? Name);
}
