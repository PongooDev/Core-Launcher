namespace Core.Launcher.Hosting.Discord;

/// <summary>
/// Keeps the player's Discord activity at what the launcher last asked for (<see cref="Show"/>). It connects to Discord
/// only when it has something new to show: when Discord isn't running, or its pipe closed, it stays quiet until the next
/// change and tries again then. Nothing runs between changes. Inert while <see cref="ApplicationId"/> is empty.
/// </summary>
public sealed class DiscordPresence : IAsyncDisposable
{
    /// <summary>
    /// Core's application on the Discord Developer Portal (https://discord.com/developers/applications): the presence
    /// shows under its name ("Playing Core") with its art asset <see cref="LargeImageKey"/>. Created by MrPodge6 for Core
    /// (CoreV2 #467) and approved by the owner. Empty, the presence is off and nothing touches Discord or the backend.
    /// </summary>
    public const string ApplicationId = "1571859629510631545";

    /// <summary>The art asset the activity shows as its large image; Discord shows none while the application has no asset of that name.</summary>
    public const string LargeImageKey = "core";

    private static readonly TimeSpan ClearTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    private readonly string _applicationId;
    private readonly Func<CancellationToken, Task<IDiscordConnection?>> _open;
    private readonly int _processId;
    private readonly Action<string> _log;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private DiscordActivity? _wanted;
    private Task _pump = Task.CompletedTask;
    private IDiscordConnection? _connection;
    private DiscordActivity? _shown;
    private bool _announced;

    public DiscordPresence(string applicationId, Action<string> log)
        : this(applicationId, Environment.ProcessId, log, null)
    {
    }

    /// <param name="open">Opens a connection; null opens Discord's pipe (<see cref="DiscordPipe.OpenAsync(string, Action{string}, CancellationToken)"/>).</param>
    public DiscordPresence(string applicationId, int processId, Action<string> log, Func<CancellationToken, Task<IDiscordConnection?>>? open)
    {
        _applicationId = applicationId.Trim();
        _processId = processId;
        _log = log;
        _open = open ?? (async cancellation => await DiscordPipe.OpenAsync(_applicationId, log, cancellation));
    }

    /// <summary>The launcher has an application id, so it shows a presence at all.</summary>
    public bool Enabled => _applicationId.Length > 0;

    /// <summary>Completes once what was last asked for has been sent, or couldn't be (for tests).</summary>
    public Task Idle
    {
        get
        {
            lock (_gate)
            {
                return _pump;
            }
        }
    }

    /// <summary>
    /// Shows <paramref name="activity"/>, or clears it and lets go of Discord for null. Returns at once; the pipe is written
    /// off the caller's thread. Asking again for what was last asked for does nothing.
    /// </summary>
    public void Show(DiscordActivity? activity)
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (Equals(activity, _wanted) || _stop.IsCancellationRequested)
            {
                return;
            }

            _wanted = activity;
            if (_pump.IsCompleted)
            {
                _pump = Task.Run(PumpAsync);
            }
        }
    }

    /// <summary>Sends what is wanted until what Discord shows is the latest of it; one at a time, ending when there is nothing new.</summary>
    private async Task PumpAsync()
    {
        while (true)
        {
            DiscordActivity? next;
            lock (_gate)
            {
                next = _wanted;
                if (_connection is { Connected: true } ? Equals(next, _shown) : next is null)
                {
                    return;
                }
            }

            try
            {
                await ApplyAsync(next, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_gate)
            {
                // Couldn't send it: wait for the next change before trying Discord again.
                if (_connection is not { Connected: true } && Equals(next, _wanted) && next is not null)
                {
                    return;
                }
            }
        }
    }

    private async Task ApplyAsync(DiscordActivity? activity, CancellationToken cancellation)
    {
        if (_connection is { Connected: false } closed)
        {
            await DropAsync(closed, "Discord Rich Presence: Discord closed its pipe; the presence comes back with the next change once it runs again");
        }

        if (activity is null)
        {
            // Off: clear what Discord shows and let go of the pipe.
            if (_connection is { } connection)
            {
                await TrySendAsync(connection, null, cancellation);
                await DropAsync(connection, null);
            }

            return;
        }

        if (_connection is null)
        {
            var opened = await _open(cancellation);
            if (opened is null)
            {
                return;
            }

            lock (_gate)
            {
                _connection = opened;
                _shown = null;
            }

            if (!_announced)
            {
                _announced = true;
                _log("Discord Rich Presence: connected to Discord");
            }
        }

        if (await TrySendAsync(_connection, activity, cancellation))
        {
            lock (_gate)
            {
                _shown = activity;
            }
        }
        else
        {
            await DropAsync(_connection, null);
        }
    }

    private async Task<bool> TrySendAsync(IDiscordConnection connection, DiscordActivity? activity, CancellationToken cancellation)
    {
        // A Discord that stopped reading its pipe would hold the write: it is dropped like a closed one.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(SendTimeout);
        try
        {
            await connection.SendAsync(DiscordOpcode.Frame, DiscordIpc.SetActivity(_processId, activity, Guid.NewGuid().ToString()), timeout.Token);
            return true;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task DropAsync(IDiscordConnection connection, string? message)
    {
        lock (_gate)
        {
            if (_connection == connection)
            {
                _connection = null;
                _shown = null;
            }
        }

        if (message is not null)
        {
            _log(message);
            _announced = false;
        }

        await connection.DisposeAsync();
    }

    /// <summary>Clears the activity (briefly waiting for Discord) and closes the pipe.</summary>
    public async ValueTask DisposeAsync()
    {
        Task pump;
        lock (_gate)
        {
            pump = _pump;
        }

        await _stop.CancelAsync();
        try
        {
            await pump;
        }
        catch (OperationCanceledException)
        {
        }

        if (_connection is { } connection)
        {
            using var timeout = new CancellationTokenSource(ClearTimeout);
            try
            {
                if (connection.Connected)
                {
                    await TrySendAsync(connection, null, timeout.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }

            await DropAsync(connection, null);
        }

        _stop.Dispose();
    }
}
