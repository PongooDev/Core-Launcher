using System.Diagnostics;
using Core.Launcher.Hosting.Backend;
using Core.Launcher.Hosting.Builds;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Hosting.Discord;

/// <summary>
/// The launcher's Discord Rich Presence (CoreV2 #467): "Logged in as MrPodge6" and what the player is doing, with the time
/// since their game started. It follows the launcher's own events: each <see cref="LauncherHost.Changed"/> works the
/// activity out again (a game starting or exiting, the settings saved) and <see cref="DiscordPresence"/> sends it when it
/// differs. While the player's game runs, whether they're in the lobby or in a match comes from Core-Backend's
/// <see cref="BackendPresence"/>, one long-poll request at a time that the backend answers when that changes. Does nothing
/// without <see cref="DiscordPresence.ApplicationId"/>, or with Settings → Discord off.
/// </summary>
public sealed class RichPresence : IAsyncDisposable
{
    /// <summary>A backend answering the unchanged state sooner than this isn't waiting for a change: the next ask waits this long.</summary>
    private static readonly TimeSpan QuickAnswer = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(60);

    private readonly LauncherHost _host;
    private readonly DiscordPresence _discord;
    private readonly HttpClient _http;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _displayNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failures = [];
    private Game? _game;
    private bool _started;
    private bool _disposed;
    private bool _dirty;
    private bool _refreshing;

    internal RichPresence(LauncherHost host, DiscordPresence discord, HttpClient http)
    {
        _host = host;
        _discord = discord;
        _http = http;
    }

    /// <summary>The launcher has Core's Discord application id; without it the setting does nothing.</summary>
    public bool Available => _discord.Enabled;

    /// <summary>Starts following the launcher (the app calls it once its window is up); nothing happens without an application id.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (!_discord.Enabled || _started || _disposed)
            {
                return;
            }

            _started = true;
        }

        _host.Changed += Refresh;
        Refresh();
    }

    /// <summary>
    /// Works the activity out again and shows it when it changed. Called on any thread, the host's Changed among them
    /// (which it may raise holding its own lock): the work runs on one task at a time, and a call while it runs makes it
    /// go round once more, so the last activity shown is always the latest state's.
    /// </summary>
    public void Refresh()
    {
        lock (_gate)
        {
            if (!_started || _disposed)
            {
                return;
            }

            _dirty = true;
            if (_refreshing)
            {
                return;
            }

            _refreshing = true;
        }

        _ = Task.Run(RefreshAll);
    }

    private void RefreshAll()
    {
        while (true)
        {
            lock (_gate)
            {
                if (!_dirty || _disposed)
                {
                    _refreshing = false;
                    return;
                }

                _dirty = false;
            }

            try
            {
                Update();
            }
            catch (Exception exception)
            {
                // Never let the presence stop following the launcher; say why once per kind of failure.
                if (_failures.Add(exception.GetType().Name))
                {
                    _host.Log.Write($"Discord Rich Presence: {exception.Message}");
                }
            }
        }
    }

    private void Update()
    {
        var settings = _host.Settings;
        var clients = _host.Clients;
        var client = settings.DiscordRichPresence
            ? clients.FirstOrDefault(session => string.Equals(session.Login, settings.PlayerName, StringComparison.OrdinalIgnoreCase)) ?? clients.FirstOrDefault()
            : null;

        Game? stopped = null;
        Game? started = null;
        Game? game;
        PlayerActivity activity;
        string? displayName;
        lock (_gate)
        {
            if (_game is { } current && current.Session != client)
            {
                stopped = current;
                _game = null;
            }

            if (client is not null && _game is null)
            {
                _game = started = new Game(client, VersionOf(client.Game.Build));
            }

            game = _game;
            activity = game?.Activity ?? PlayerActivity.Unknown;
            displayName = _displayNames.GetValueOrDefault((client?.Login ?? settings.PlayerName).Trim());
        }

        stopped?.Stop.Cancel();
        if (started is not null && _host.Backend.Endpoint is { } backend)
        {
            _ = Task.Run(() => WatchAsync(started, backend));
        }

        if (!settings.DiscordRichPresence)
        {
            _discord.Show(null);
            return;
        }

        _discord.Show(PresenceText.Activity(client?.Login ?? settings.PlayerName, displayName,
            game is null ? null : new DateTimeOffset(game.Session.Game.StartedAt, TimeSpan.Zero), game?.Version, activity));
    }

    /// <summary>"6.30" for a build Core knows, else its branch's version ("14.60"); null when neither is known.</summary>
    internal static string? VersionOf(BuildSettings build) =>
        KnownBuilds.VersionOf(build.Changelist) ?? (BuildVersion.Read(build.Path)?.ShortName is { Length: > 0 } version ? version : null);

    /// <summary>
    /// Follows the player's state on the backend while their game runs: each answer is a change (or the backend's wait
    /// running out), so this asks again at once. A backend without the route ends it (the activity says "Playing Fortnite
    /// 6.30" throughout); one that can't be reached is asked again after 5 s, doubling up to 60 s.
    /// </summary>
    private async Task WatchAsync(Game game, BackendEndpoint backend)
    {
        var after = PlayerActivity.Unknown;
        var failures = 0;
        var token = game.Stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var asked = Stopwatch.GetTimestamp();
                var read = await BackendPresence.ReadAsync(_http, backend, game.Session.Login, after, token);
                if (read.Unsupported)
                {
                    return;
                }

                if (read.Presence is not { } presence)
                {
                    ++failures;
                    await Task.Delay(Retry(failures), token);
                    continue;
                }

                failures = 0;
                lock (_gate)
                {
                    game.Activity = presence.Activity;
                    if (presence.DisplayName is { } name)
                    {
                        _displayNames[game.Session.Login.Trim()] = name;
                    }
                }

                Refresh();
                if (presence.Activity == after && Stopwatch.GetElapsedTime(asked) < QuickAnswer)
                {
                    await Task.Delay(QuickAnswer, token);
                }

                after = presence.Activity;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal static TimeSpan Retry(int failures) =>
        TimeSpan.FromTicks(Math.Min(MaxRetry.Ticks, FirstRetry.Ticks << Math.Min(failures - 1, 8)));

    public async ValueTask DisposeAsync()
    {
        Game? game;
        bool started;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            started = _started;
            game = _game;
            _game = null;
        }

        if (started)
        {
            _host.Changed -= Refresh;
        }

        game?.Stop.Cancel();
        await _discord.DisposeAsync();
        _http.Dispose();
    }

    /// <summary>The game the presence is about, and what the backend last said about it.</summary>
    private sealed class Game(ClientSession session, string? version)
    {
        public ClientSession Session { get; } = session;

        public string? Version { get; } = version;

        public CancellationTokenSource Stop { get; } = new();

        public PlayerActivity Activity { get; set; } = PlayerActivity.Unknown;
    }
}
