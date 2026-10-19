using System.Text.Json;
using Core.Launcher.Hosting.Backend;
using Core.Launcher.Hosting.Dlls;
using Core.Launcher.Hosting.Logging;
using Core.Launcher.Hosting.Native;
using Core.Launcher.Hosting.Processes;
using Core.Launcher.Hosting.Servers;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Hosting;

/// <summary>Everything the window drives: settings, the backend, the client and the server pool.</summary>
public sealed class LauncherHost : IAsyncDisposable
{
    private static readonly TimeSpan BackendReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly SettingsStore _store;
    private readonly JobObject _job = new();
    private readonly HttpClient _http = ReleaseClient.CreateHttpClient();
    private readonly GameLauncher _launcher;
    private readonly Dictionary<int, LogChannel> _serverLogs = [];
    private readonly Lock _gate = new();
    private Task _dllRefresh = Task.CompletedTask;

    public LauncherHost(LauncherPaths paths)
    {
        Paths = paths;
        _store = new SettingsStore(paths.Settings);
        Log = new LogChannel("Launcher", Path.Combine(paths.Logs, "Launcher.log"), timestamp: true);
        BackendLog = new LogChannel("Backend", Path.Combine(paths.Logs, "Backend.log"));
        ClientLog = new LogChannel("Client");

        try
        {
            Settings = _store.Load();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            Log.Write($"Couldn't read {paths.Settings} ({exception.Message}); starting from the defaults");
            Settings = new LauncherSettings();
        }

        Dlls = new DllStore(paths, new ReleaseClient(_http), Log.Write);
        Backend = new BackendHost(_job, BackendLog, Log);
        _launcher = new GameLauncher(_job, Dlls, paths, Log);
        Servers = new ServerPool(new ServerStarter(this), Log, TimeProvider.System);

        Backend.Changed += OnChanged;
        Servers.Changed += OnChanged;
    }

    public LauncherPaths Paths { get; }

    public LauncherSettings Settings { get; private set; }

    public LogChannel Log { get; }

    public LogChannel BackendLog { get; }

    public LogChannel ClientLog { get; }

    public DllStore Dlls { get; }

    public BackendHost Backend { get; }

    public ServerPool Servers { get; }

    public RunningGame? Client { get; private set; }

    public bool RefreshingDlls => !_dllRefresh.IsCompleted;

    public bool GamesRunning => Client is not null || Servers.Slots.Any(slot => slot.ProcessId is not null);

    public bool AnythingRunning => GamesRunning || Backend.ProcessId is not null;

    public event Action? Changed;

    public LogChannel ServerLog(int instance)
    {
        lock (_gate)
        {
            if (!_serverLogs.TryGetValue(instance, out var channel))
            {
                channel = new LogChannel($"Server {instance}");
                _serverLogs[instance] = channel;
            }

            return channel;
        }
    }

    public void Start()
    {
        Log.Write($"Core Launcher {typeof(LauncherHost).Assembly.GetName().Version?.ToString(3)}, data in {Paths.Root}");
        Dlls.CleanUp();
        Apply(Settings, previous: null);
        Servers.StartTimer();
        RefreshDlls();
    }

    public void RefreshDlls()
    {
        lock (_gate)
        {
            if (_dllRefresh.IsCompleted)
            {
                _dllRefresh = Task.Run(async () =>
                {
                    await Dlls.RefreshAsync(CancellationToken.None);
                    OnChanged();
                });
                OnChanged();
            }
        }
    }

    public void SaveSettings(LauncherSettings updated)
    {
        var previous = Settings;
        _store.Save(updated);
        Settings = updated;
        Apply(updated, previous);
        Log.Write("Settings saved");
        OnChanged();
    }

    private void Apply(LauncherSettings settings, LauncherSettings? previous)
    {
        Dlls.Configure(settings.Dlls);
        if (previous is not null && !SameJson(previous.Dlls, settings.Dlls))
        {
            RefreshDlls();
        }

        Backend.Configure(settings.Backend);
        if (previous is null)
        {
            Backend.Start();
        }
        else if (!SameJson(previous.Backend, settings.Backend))
        {
            Log.Write("Backend settings changed, restarting it");
            _ = Backend.RestartAsync();
        }

        Servers.Configure(settings.Servers.Instances, TimeSpan.FromSeconds(settings.Servers.ReadyTimeoutSeconds));
        Servers.SetEnabled(settings.Servers.Enabled);
    }

    public void SetHosting(bool enabled)
    {
        var updated = SettingsStore.Clone(Settings);
        updated.Servers.Enabled = enabled;
        SaveSettings(updated);
    }

    /// <summary>Starts the client; throws with a message for the user when it can't.</summary>
    public async Task PlayAsync()
    {
        if (Client is not null)
        {
            return;
        }

        var settings = Settings;
        var build = settings.FindSelectedBuild() ?? throw new InvalidOperationException("Add a build first (Settings → Builds)");
        if (settings.PlayerName.Length == 0)
        {
            throw new InvalidOperationException("Enter a player name");
        }

        await _dllRefresh;
        if (!Backend.Running)
        {
            Backend.Start();
        }

        if (!await Backend.WaitReadyAsync(BackendReadyTimeout) || Backend.Port is not { } port)
        {
            throw new InvalidOperationException($"Core-Backend isn't ready: {Backend.Error ?? Backend.State.ToString()}");
        }

        ClientLog.Write($"(launcher) --- {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
        var client = _launcher.Start(new GameStart("Client", build, build.ClientDlls, LaunchArguments.Client(build, port, settings.PlayerName), false, ClientLog));
        Client = client;
        OnChanged();
        _ = client.Finished.ContinueWith(_ =>
        {
            client.Dispose();
            Client = null;
            OnChanged();
        }, TaskScheduler.Default);
    }

    public void StopClient() => Client?.Terminate();

    public async ValueTask DisposeAsync()
    {
        Log.Write("Shutting down");
        await Servers.StopAllAsync(ShutdownTimeout);
        if (Client is { } client)
        {
            client.Terminate();
            await Task.WhenAny(client.Finished, Task.Delay(ShutdownTimeout));
        }

        await Backend.StopAsync();
        Servers.Dispose();
        _http.Dispose();
        _job.Dispose();
        Log.Write("Stopped");
        Log.Dispose();
        BackendLog.Dispose();
    }

    private void OnChanged() => Changed?.Invoke();

    private static bool SameJson<T>(T left, T right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    private sealed class ServerStarter(LauncherHost host) : IServerStarter
    {
        public string? Blocker()
        {
            if (host.Settings.FindSelectedBuild() is null)
            {
                return "no build (Settings → Builds)";
            }

            if (host.RefreshingDlls)
            {
                return "checking for DLL updates";
            }

            return host.Backend.State == BackendState.Ready && host.Backend.Port is not null ? null : "waiting for the backend";
        }

        public IServerInstance Start(int instance)
        {
            var settings = host.Settings;
            var build = settings.FindSelectedBuild()!;
            var arguments = LaunchArguments.Server(build, host.Backend.Port!.Value, settings.Servers.LoginFor(instance));
            var output = host.ServerLog(instance);
            output.Write($"(launcher) --- {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
            return host._launcher.Start(new GameStart($"Server {instance}", build, build.ServerDlls, arguments, true, output));
        }
    }
}
