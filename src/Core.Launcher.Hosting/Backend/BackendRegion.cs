using System.Net;
using System.Text.Json;
using Core.Launcher.Hosting.Builds;

namespace Core.Launcher.Hosting.Backend;

/// <summary>
/// The region the hosted servers advertise (-McpRegion=): the first of the backend's Server Regions, the admin panel's
/// Matchmaking page (Core-Backend #142), or NA when none is ticked (Core-Launcher #8). Read from GET /core/api/status,
/// which Core-Backend answers without a login and other PCs too, so servers hosted for a friend's backend take its
/// regions. <see cref="Region"/> never blocks: it answers from the latest read and starts a new read in the background
/// when that one is older than <see cref="PollInterval"/>.
/// </summary>
public sealed class BackendRegion : IDisposable
{
    public const string StatusPath = RemoteBackendCheck.StatusPath;

    /// <summary>What a backend without Server Regions (or none ticked) gets: the region an untouched client queues for.</summary>
    public const string DefaultRegion = "NA";

    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private Reading? _latest;
    private Task _refresh = Task.CompletedTask;

    public BackendRegion(HttpClient http, TimeProvider time)
    {
        _http = http;
        _time = time;
    }

    /// <summary>
    /// The region servers hosted for <paramref name="backend"/> advertise, or null until it was first asked. A backend that
    /// couldn't be reached keeps the region it last gave (NA if none).
    /// </summary>
    public string? Region(BackendEndpoint backend)
    {
        lock (_gate)
        {
            var latest = _latest is { } reading && reading.Backend == backend ? reading : null;
            if ((latest is null || _time.GetUtcNow() - latest.ReadAt >= PollInterval) && _refresh.IsCompleted)
            {
                _refresh = Task.Run(() => RefreshAsync(backend));
            }

            return latest?.Region;
        }
    }

    /// <summary>Asks the backend at <paramref name="backend"/> now; <see cref="Region"/> answers with it from then on.</summary>
    public async Task RefreshAsync(BackendEndpoint backend, CancellationToken cancellation = default)
    {
        var readAt = _time.GetUtcNow();
        var region = await ReadAsync(backend, cancellation);
        lock (_gate)
        {
            var previous = _latest is { } reading && reading.Backend == backend ? reading.Region : null;
            _latest = new Reading(backend, readAt, region ?? previous ?? DefaultRegion);
        }
    }

    /// <summary>The first known region of the backend's Server Regions, NA when it has none, or null when it couldn't be read.</summary>
    private async Task<string?> ReadAsync(BackendEndpoint backend, CancellationToken cancellation)
    {
        try
        {
            using var response = await _http.GetAsync(backend.Url + StatusPath, cancellation);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                // An older Core-Backend without the status route answers 404: it has no Server Regions either.
                return response.StatusCode == HttpStatusCode.NotFound ? DefaultRegion : null;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            return FirstRegion(json.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException && !cancellation.IsCancellationRequested)
        {
            // The backend is restarting or busy: ask again at the next check.
            return null;
        }
    }

    /// <summary>The first of a status answer's serverRegions that the client's Matchmaking Region list offers, else NA.</summary>
    public static string FirstRegion(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object || !status.TryGetProperty("serverRegions", out var regions) || regions.ValueKind != JsonValueKind.Array)
        {
            return DefaultRegion;
        }

        return regions.EnumerateArray()
            .Where(region => region.ValueKind == JsonValueKind.String)
            .Select(region => region.GetString()!.Trim().ToUpperInvariant())
            .FirstOrDefault(code => KnownBuilds.ServerRegions.Any(known => known.Code == code)) ?? DefaultRegion;
    }

    public void Dispose() => _http.Dispose();

    private sealed record Reading(BackendEndpoint Backend, DateTimeOffset ReadAt, string Region);
}
