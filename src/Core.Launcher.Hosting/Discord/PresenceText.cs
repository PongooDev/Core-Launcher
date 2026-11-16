using Core.Launcher.Hosting.Backend;

namespace Core.Launcher.Hosting.Discord;

/// <summary>The words of the Discord activity (CoreV2 #467): "Logged in as MrPodge6" over "In Lobby".</summary>
public static class PresenceText
{
    public const string InLauncher = "In the launcher";
    public const string InLobby = "In Lobby";
    public const string InMatch = "In a match";

    /// <summary>
    /// "Logged in as &lt;name&gt;": the backend's display name for the account when known, else the player name. A login
    /// that is an email address (Core-Backend #120) is never shown: only the display name the backend gives for it. Null
    /// when signed out (no player name).
    /// </summary>
    public static string? Details(string playerName, string? displayName)
    {
        var name = !string.IsNullOrWhiteSpace(displayName) ? displayName.Trim()
            : playerName.Trim() is { Length: > 0 } login && !login.Contains('@') ? login
            : null;
        return name is null ? null : $"Logged in as {name}";
    }

    /// <summary>
    /// "In the launcher" with no game running; with one, "In a match" or "In Lobby" as the backend says, otherwise
    /// "Playing Fortnite 6.30" (still starting, or a backend that can't say).
    /// </summary>
    public static string State(bool playing, string? version, PlayerActivity activity) =>
        !playing ? InLauncher
        : activity switch
        {
            PlayerActivity.Match => InMatch,
            PlayerActivity.Lobby => InLobby,
            _ => Playing(version),
        };

    public static string Playing(string? version) => string.IsNullOrWhiteSpace(version) ? "Playing Fortnite" : $"Playing Fortnite {version.Trim()}";

    /// <summary>
    /// The whole activity. <paramref name="gameStarted"/> is when the game started (null with none running): Discord counts
    /// the time since. The image's text names the build while one runs.
    /// </summary>
    public static DiscordActivity Activity(string playerName, string? displayName, DateTimeOffset? gameStarted, string? version, PlayerActivity activity)
    {
        var playing = gameStarted is not null;
        return new DiscordActivity(
            Details(playerName, displayName),
            State(playing, version, activity),
            gameStarted,
            DiscordPresence.LargeImageKey,
            playing && !string.IsNullOrWhiteSpace(version) ? $"Fortnite {version.Trim()}" : "Core");
    }
}
