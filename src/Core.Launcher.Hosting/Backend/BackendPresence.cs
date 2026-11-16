using System.Net;
using System.Text.Json;

namespace Core.Launcher.Hosting.Backend;

/// <summary>What the player's game is doing, as Core-Backend knows it.</summary>
public enum PlayerActivity
{
    /// <summary>Not asked yet, or the backend can't say (an older one without /core/api/presence).</summary>
    Unknown,

    /// <summary>The game isn't logged in to the backend's XMPP yet (it is still starting) or any more.</summary>
    Offline,

    /// <summary>Logged in: the front end, the lobby.</summary>
    Lobby,

    /// <summary>A gameserver has the player registered in its session.</summary>
    Match,
}

/// <summary>One answer of GET /core/api/presence: the state, and the account's display name when the backend gave it.</summary>
public sealed record PlayerPresence(PlayerActivity Activity, string? DisplayName);

/// <summary>A read of the presence: <see cref="Presence"/> when it answered, else whether it never will (<see cref="Unsupported"/>) or just failed this time.</summary>
public sealed record PresenceRead(PlayerPresence? Presence, bool Unsupported)
{
    public static PresenceRead Failed { get; } = new(null, false);

    public static PresenceRead NotServed { get; } = new(null, true);
}

/// <summary>
/// Core-Backend's GET /core/api/presence?username=&lt;login&gt; (CoreV2 #467), for the Discord Rich Presence. With
/// &amp;after=&lt;state&gt; the backend holds the request until the state is another one, or up to 55 s, so one request
/// is open at a time while the game runs and each answer is a change (or that wait running out).
/// </summary>
public static class BackendPresence
{
    public const string Path = "/core/api/presence";

    /// <summary>Longer than the backend's 55 s wait.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler { UseProxy = false }) { Timeout = RequestTimeout };

    public static string Url(BackendEndpoint backend, string login, PlayerActivity after) =>
        $"{backend.Url}{Path}?username={Uri.EscapeDataString(login.Trim())}{(Name(after) is { } state ? $"&after={state}" : "")}";

    public static async Task<PresenceRead> ReadAsync(HttpClient http, BackendEndpoint backend, string login, PlayerActivity after, CancellationToken cancellation)
    {
        try
        {
            using var response = await http.GetAsync(Url(backend, login, after), cancellation);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.Forbidden)
            {
                return PresenceRead.NotServed;
            }

            if (!response.IsSuccessStatusCode)
            {
                return PresenceRead.Failed;
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellation);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellation);
            return Parse(json.RootElement) is { } presence ? new PresenceRead(presence, false) : PresenceRead.NotServed;
        }
        catch (JsonException)
        {
            return PresenceRead.NotServed;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException || (exception is TaskCanceledException && !cancellation.IsCancellationRequested))
        {
            // The backend is restarting, or the request timed out.
            return PresenceRead.Failed;
        }
    }

    /// <summary>{ displayName, state: "offline" | "lobby" | "match" }; null for anything else.</summary>
    public static PlayerPresence? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var activity = state.GetString() switch
        {
            "offline" => PlayerActivity.Offline,
            "lobby" => PlayerActivity.Lobby,
            "match" => PlayerActivity.Match,
            _ => PlayerActivity.Unknown,
        };
        if (activity == PlayerActivity.Unknown)
        {
            return null;
        }

        var name = root.TryGetProperty("displayName", out var displayName) && displayName.ValueKind == JsonValueKind.String ? displayName.GetString()?.Trim() : null;
        return new PlayerPresence(activity, string.IsNullOrEmpty(name) ? null : name);
    }

    private static string? Name(PlayerActivity activity) => activity switch
    {
        PlayerActivity.Offline => "offline",
        PlayerActivity.Lobby => "lobby",
        PlayerActivity.Match => "match",
        _ => null,
    };
}
