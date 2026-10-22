using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Core.Launcher.Hosting.Servers;

/// <summary>Whether the backend lists a server's matchmaking session yet.</summary>
public enum SessionCheck
{
    /// <summary>Not listed yet, or the backend couldn't be asked this time.</summary>
    Pending,

    /// <summary>The backend lists a session the server made since it started: clients can find it.</summary>
    Listed,

    /// <summary>The backend has no session listing (an older or other backend): there is nothing to wait for.</summary>
    Unsupported,
}

/// <summary>
/// Reads Core-Backend's matchmaking sessions (GET /core/api/sessions, loopback only) so a server only counts as ready
/// once clients can find it. <see cref="Check"/> never blocks: it answers from the latest listing and starts a new read
/// in the background when that one is older than <see cref="PollInterval"/>.
/// </summary>
public sealed class BackendSessions : IDisposable
{
    public const string ListPath = "/core/api/sessions";

    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private Listing? _latest;
    private Task _refresh = Task.CompletedTask;

    public BackendSessions(HttpClient http, TimeProvider time)
    {
        _http = http;
        _time = time;
    }

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>
    /// Whether the backend on <paramref name="backendPort"/> lists a session owned by <paramref name="login"/> (or, when
    /// the session names no owner, on one of <paramref name="ports"/>) that was updated at or after <paramref name="since"/>,
    /// the server's start: a session left by an earlier run of the same server doesn't count.
    /// </summary>
    public SessionCheck Check(int backendPort, string login, DateTimeOffset since, IReadOnlyList<int> ports)
    {
        Listing? latest;
        lock (_gate)
        {
            latest = _latest is { } listing && listing.Port == backendPort ? listing : null;
            if ((latest is null || _time.GetUtcNow() - latest.ReadAt >= PollInterval) && _refresh.IsCompleted)
            {
                _refresh = Task.Run(() => RefreshAsync(backendPort));
            }
        }

        if (latest is null)
        {
            return SessionCheck.Pending;
        }

        if (latest.Sessions is not { } sessions)
        {
            return latest.Unsupported ? SessionCheck.Unsupported : SessionCheck.Pending;
        }

        return sessions.Any(session => Owns(session, login, ports) && (session.LastUpdated is not { } updated || updated >= since))
            ? SessionCheck.Listed
            : SessionCheck.Pending;
    }

    private static bool Owns(Session session, string login, IReadOnlyList<int> ports) =>
        session.OwnerName.Length > 0
            ? string.Equals(session.OwnerName, login, StringComparison.OrdinalIgnoreCase)
            : session.ServerPort > 0 && ports.Contains(session.ServerPort);

    /// <summary>Reads the listing now; <see cref="Check"/> uses it from then on.</summary>
    public async Task RefreshAsync(int backendPort, CancellationToken cancellation = default)
    {
        var listing = await ReadAsync(backendPort, cancellation);
        lock (_gate)
        {
            _latest = listing;
        }
    }

    private async Task<Listing> ReadAsync(int backendPort, CancellationToken cancellation)
    {
        var readAt = _time.GetUtcNow();
        try
        {
            using var response = await _http.GetAsync($"http://127.0.0.1:{backendPort}{ListPath}", cancellation);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.Forbidden)
            {
                return new Listing(backendPort, readAt, null, Unsupported: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new Listing(backendPort, readAt, null, Unsupported: false);
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellation);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellation);
            return json.RootElement.ValueKind == JsonValueKind.Array
                ? new Listing(backendPort, readAt, [.. json.RootElement.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(Parse)], Unsupported: false)
                : new Listing(backendPort, readAt, null, Unsupported: true);
        }
        catch (JsonException)
        {
            return new Listing(backendPort, readAt, null, Unsupported: true);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            // The backend is restarting or busy: ask again at the next check.
            return new Listing(backendPort, readAt, null, Unsupported: false);
        }
    }

    private static Session Parse(JsonElement item)
    {
        var owner = item.TryGetProperty("ownerName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : "";
        var port = item.TryGetProperty("serverPort", out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var value) ? value : 0;
        DateTimeOffset? updated = item.TryGetProperty("lastUpdated", out var text) && text.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(text.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
        return new Session(owner, port, updated);
    }

    public void Dispose() => _http.Dispose();

    private sealed record Session(string OwnerName, int ServerPort, DateTimeOffset? LastUpdated);

    /// <summary>One read of the listing: <see cref="Sessions"/> is null when it failed, <see cref="Unsupported"/> when the backend has no listing.</summary>
    private sealed record Listing(int Port, DateTimeOffset ReadAt, IReadOnlyList<Session>? Sessions, bool Unsupported);
}
