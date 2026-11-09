using System.Globalization;
using System.Text.Json;
using Core.Launcher.Hosting.Backend;

namespace Core.Launcher.Hosting.Servers;

/// <summary>
/// Reads which builds have players waiting for a server (Core-Backend's GET /core/api/demand, Core-Launcher #18), for
/// <see cref="ServerSwitcher"/>. Like <see cref="BackendSessions"/> it never blocks: <see cref="Current"/> answers from
/// the latest read and starts a new one in the background when that one is older than <see cref="PollInterval"/>. An
/// older backend without the route, or one that can't be reached, has no demand.
/// </summary>
public sealed class BackendDemand : IDisposable
{
    public const string Path = "/core/api/demand";

    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private Reading? _latest;
    private Task _refresh = Task.CompletedTask;

    public BackendDemand(HttpClient http, TimeProvider time)
    {
        _http = http;
        _time = time;
    }

    /// <summary>The latest demand of the backend at <paramref name="backend"/>; empty until it has been read.</summary>
    public IReadOnlyList<BuildDemand> Current(BackendEndpoint backend)
    {
        lock (_gate)
        {
            var latest = _latest is { } read && read.Backend == backend ? read : null;
            if ((latest is null || _time.GetUtcNow() - latest.ReadAt >= PollInterval) && _refresh.IsCompleted)
            {
                _refresh = Task.Run(() => RefreshAsync(backend));
            }

            return latest?.Demand ?? [];
        }
    }

    /// <summary>Reads the demand now; <see cref="Current"/> answers with it from then on.</summary>
    public async Task RefreshAsync(BackendEndpoint backend, CancellationToken cancellation = default)
    {
        var readAt = _time.GetUtcNow();
        IReadOnlyList<BuildDemand> demand;
        try
        {
            using var response = await _http.GetAsync($"{backend.Url}{Path}", cancellation);
            if (!response.IsSuccessStatusCode)
            {
                demand = [];
            }
            else
            {
                await using var body = await response.Content.ReadAsStreamAsync(cancellation);
                using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellation);
                demand = Parse(json.RootElement);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            demand = [];
        }

        lock (_gate)
        {
            _latest = new Reading(backend, readAt, demand);
        }
    }

    /// <summary>[{ build: "4166199", mode: "battleRoyale" or "saveTheWorld", players: 2 }] as <see cref="BuildDemand"/>s; anything else is skipped.</summary>
    public static IReadOnlyList<BuildDemand> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var demand = new List<BuildDemand>();
        foreach (var item in root.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
        {
            var changelist = item.TryGetProperty("build", out var build) ? build.ValueKind switch
            {
                JsonValueKind.String when int.TryParse(build.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
                JsonValueKind.Number when build.TryGetInt32(out var number) => number,
                _ => 0,
            } : 0;
            var players = item.TryGetProperty("players", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var value) ? value : 0;
            var battleRoyale = item.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String && mode.GetString() == "battleRoyale";
            if (changelist > 0 && players > 0)
            {
                demand.Add(new BuildDemand(changelist, battleRoyale, players));
            }
        }

        return demand;
    }

    public void Dispose() => _http.Dispose();

    private sealed record Reading(BackendEndpoint Backend, DateTimeOffset ReadAt, IReadOnlyList<BuildDemand> Demand);
}
