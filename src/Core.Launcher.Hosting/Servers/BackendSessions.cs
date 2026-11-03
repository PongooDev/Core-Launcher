using System.Globalization;
using System.Net;
using System.Text.Json;
using Core.Launcher.Hosting.Backend;

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

/// <summary>Whether a friend's backend lets this PC host servers for it (Core-Backend #125's Trusted Hosting IPs).</summary>
public enum HostingAccess
{
    /// <summary>Not asked yet, or it couldn't be reached this time.</summary>
    Unknown,

    Allowed,

    /// <summary>It answered 403: this PC's IP isn't one of its Trusted Hosting IPs.</summary>
    Refused,
}

/// <summary>
/// Reads Core-Backend's matchmaking sessions (GET /core/api/sessions: this PC's backend, or a friend's that trusts this
/// PC's IP, which answers anyone else 403) so a server only counts as ready
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
    public SessionCheck Check(int backendPort, string login, DateTimeOffset since, IReadOnlyList<int> ports) =>
        Check(BackendEndpoint.Local(backendPort), login, since, ports);

    /// <summary><see cref="Check(int, string, DateTimeOffset, IReadOnlyList{int})"/> for the backend at <paramref name="backend"/>.</summary>
    public SessionCheck Check(BackendEndpoint backend, string login, DateTimeOffset since, IReadOnlyList<int> ports)
    {
        var latest = Latest(backend);
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

    /// <summary>
    /// How many players the session of the server logged in as <paramref name="login"/> (or, when the session names no
    /// owner, on one of <paramref name="ports"/>) has, from the latest listing of the backend at <paramref name="backend"/>
    /// (Core-Launcher #10). Null when it isn't listed, the listing couldn't be read, or the backend is older and doesn't
    /// count players. Never blocks: like <see cref="Check(BackendEndpoint, string, DateTimeOffset, IReadOnlyList{int})"/>
    /// it reads the listing again in the background when that one is old.
    /// </summary>
    public SessionPlayers? Players(BackendEndpoint backend, string login, IReadOnlyList<int> ports)
    {
        if (Latest(backend)?.Sessions is not { } sessions)
        {
            return null;
        }

        // A server's earlier session can linger until the backend drops it: the newest one is the server's own.
        return sessions.Where(session => Owns(session, login, ports))
            .OrderByDescending(session => session.LastUpdated ?? DateTimeOffset.MinValue)
            .FirstOrDefault()?.Players;
    }

    /// <summary>
    /// Whether the friend's backend at <paramref name="backend"/> lets this PC host for it, from the latest listing (a
    /// 403 refuses it); like <see cref="Check(BackendEndpoint, string, DateTimeOffset, IReadOnlyList{int})"/> it never
    /// blocks and reads the listing again in the background when that one is old.
    /// </summary>
    public HostingAccess Access(BackendEndpoint backend) => Latest(backend) switch
    {
        null => HostingAccess.Unknown,
        { Forbidden: true } => HostingAccess.Refused,
        { Sessions: null, Unsupported: false } => HostingAccess.Unknown,
        _ => HostingAccess.Allowed,
    };

    private Listing? Latest(BackendEndpoint backend)
    {
        lock (_gate)
        {
            var latest = _latest is { } listing && listing.Backend == backend ? listing : null;
            if ((latest is null || _time.GetUtcNow() - latest.ReadAt >= PollInterval) && _refresh.IsCompleted)
            {
                _refresh = Task.Run(() => RefreshAsync(backend));
            }

            return latest;
        }
    }

    private static bool Owns(Session session, string login, IReadOnlyList<int> ports) =>
        session.OwnerName.Length > 0
            ? string.Equals(session.OwnerName, login, StringComparison.OrdinalIgnoreCase)
            : session.ServerPort > 0 && ports.Contains(session.ServerPort);

    /// <summary>Reads the listing now; <see cref="Check"/> uses it from then on.</summary>
    public Task RefreshAsync(int backendPort, CancellationToken cancellation = default) => RefreshAsync(BackendEndpoint.Local(backendPort), cancellation);

    /// <summary>Reads the listing of the backend at <paramref name="backend"/> now.</summary>
    public async Task RefreshAsync(BackendEndpoint backend, CancellationToken cancellation = default)
    {
        var listing = await ReadAsync(backend, cancellation);
        lock (_gate)
        {
            _latest = listing;
        }
    }

    private async Task<Listing> ReadAsync(BackendEndpoint backend, CancellationToken cancellation)
    {
        var readAt = _time.GetUtcNow();
        try
        {
            using var response = await _http.GetAsync($"{backend.Url}{ListPath}", cancellation);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.Forbidden)
            {
                return new Listing(backend, readAt, null, Unsupported: true, Forbidden: response.StatusCode == HttpStatusCode.Forbidden);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new Listing(backend, readAt, null, Unsupported: false);
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellation);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellation);
            return json.RootElement.ValueKind == JsonValueKind.Array
                ? new Listing(backend, readAt, [.. json.RootElement.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(Parse)], Unsupported: false)
                : new Listing(backend, readAt, null, Unsupported: true);
        }
        catch (JsonException)
        {
            return new Listing(backend, readAt, null, Unsupported: true);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            // The backend is restarting or busy: ask again at the next check.
            return new Listing(backend, readAt, null, Unsupported: false);
        }
    }

    private static Session Parse(JsonElement item)
    {
        var owner = item.TryGetProperty("ownerName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : "";
        var port = item.TryGetProperty("serverPort", out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var value) ? value : 0;
        DateTimeOffset? updated = item.TryGetProperty("lastUpdated", out var text) && text.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(text.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
        return new Session(owner, port, updated, ParsePlayers(item));
    }

    /// <summary>
    /// A listed session's currentPlayers and maxPlayers (Core-Backend's sessionSummary); null when the backend doesn't
    /// send them, so an older backend shows no player count.
    /// </summary>
    public static SessionPlayers? ParsePlayers(JsonElement session)
    {
        static int? Count(JsonElement item, string name) =>
            item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var count) && count >= 0 ? count : null;

        return Count(session, "currentPlayers") is { } current ? new SessionPlayers(current, Count(session, "maxPlayers") ?? 0) : null;
    }

    public void Dispose() => _http.Dispose();

    private sealed record Session(string OwnerName, int ServerPort, DateTimeOffset? LastUpdated, SessionPlayers? Players);

    /// <summary>One read of the listing: <see cref="Sessions"/> is null when it failed, <see cref="Unsupported"/> when the backend has no listing.</summary>
    // Forbidden: it answered 403, which a friend's backend answers a PC it doesn't trust to host.
    private sealed record Listing(BackendEndpoint Backend, DateTimeOffset ReadAt, IReadOnlyList<Session>? Sessions, bool Unsupported, bool Forbidden = false);
}

/// <summary>The players in a server's matchmaking session; <see cref="Max"/> is 0 when the backend doesn't say.</summary>
public sealed record SessionPlayers(int Current, int Max)
{
    /// <summary>"Empty", "1 / 4 players", "3 players": shown on the server's card.</summary>
    public string Describe() => Current == 0 ? "Empty"
        : Max > 0 ? $"{Current} / {Max} players"
        : Current == 1 ? "1 player" : $"{Current} players";
}
