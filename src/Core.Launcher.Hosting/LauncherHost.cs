using System.Text.Json;
using Core.Launcher.Hosting.Backend;
using Core.Launcher.Hosting.Components;
using Core.Launcher.Hosting.Dlls;
using Core.Launcher.Hosting.Logging;
using Core.Launcher.Hosting.Native;
using Core.Launcher.Hosting.Processes;
using Core.Launcher.Hosting.Servers;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Hosting;

/// <summary>One line of setup or update progress: which component, and what is happening to it.</summary>
public sealed record ComponentProgress(string Component, string Status, bool Failed = false);

/// <summary>Everything the windows drive: settings, components and their updates, the backend, the client and the server pool.</summary>
public sealed class LauncherHost : IAsyncDisposable
{
    public const string RuntimeComponent = "Node.js";
    public const string BackendComponent = "Core-Backend";
    public const string DllComponent = "Core DLLs";

    private static readonly TimeSpan BackendReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly SettingsStore _store;
    private readonly JobObject _job = new();
    private readonly HttpClient _http = GitHubClient.CreateHttpClient();
    private readonly GameLauncher _launcher;
    private readonly Dictionary<int, LogChannel> _serverLogs = [];
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _updating = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Task _updates = Task.CompletedTask;
    private bool _started;

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

        var github = new GitHubClient(_http);
        Dlls = new DllStore(paths, github, Log.Write);
        Node = new NodeRuntime(paths, _http, Log.Write);
        BackendPackage = new BackendPackage(paths, github, Node, Log.Write);
        Updater = new LauncherUpdater(github, LauncherUpdater.RunningVersion, LauncherUpdater.PublishedExecutable(typeof(LauncherHost).Assembly), Log.Write);
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

    public NodeRuntime Node { get; }

    public BackendPackage BackendPackage { get; }

    public LauncherUpdater Updater { get; }

    public BackendHost Backend { get; }

    public ServerPool Servers { get; }

    public RunningGame? Client { get; private set; }

    public bool CheckingForUpdates => !_updates.IsCompleted;

    /// <summary>A newer managed Core-Backend installed while the old one runs; it is used from the backend's next start.</summary>
    public string? BackendUpdateReady { get; private set; }

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

    /// <summary>Starts hosting: checks every component for updates, then starts the backend and lets the servers start.</summary>
    public void Start()
    {
        Log.Write($"Core Launcher {Updater.Current.ToString(3)}, data in {Paths.Root}");
        LauncherUpdater.CleanUp(Updater.Executable);
        Dlls.CleanUp();
        _started = true;
        Apply(Settings, previous: null);
        Servers.StartTimer();
        CheckForUpdates(startBackend: true);
    }

    public void CheckForUpdates(bool startBackend = false)
    {
        lock (_gate)
        {
            if (_updates.IsCompleted)
            {
                _updates = Task.Run(async () =>
                {
                    try
                    {
                        await UpdateComponentsAsync(null, checkLauncher: true, _shutdown.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        Log.Write($"Checking for updates failed: {exception}");
                    }

                    if (_shutdown.IsCancellationRequested)
                    {
                        return;
                    }

                    if (startBackend)
                    {
                        StartBackend();
                    }

                    OnChanged();
                });
                OnChanged();
            }
        }
    }

    /// <summary>
    /// Installs or updates what the settings need: the Node.js runtime and Core-Backend when the launcher manages the
    /// backend, and the release DLLs. Returns false when the managed backend couldn't be installed.
    /// </summary>
    public async Task<bool> UpdateComponentsAsync(IProgress<ComponentProgress>? progress, bool checkLauncher, CancellationToken cancellation)
    {
        await _updating.WaitAsync(cancellation);
        try
        {
            var settings = Settings;
            if (checkLauncher)
            {
                try
                {
                    await Updater.CheckAsync(cancellation);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
                {
                    Log.Write($"Couldn't check for a launcher update: {exception.Message}");
                }
            }

            Dlls.Configure(settings.Dlls);
            await Dlls.RefreshAsync(Report(progress, DllComponent), cancellation);

            if (settings.Backend.Source != BackendSource.Managed)
            {
                return true;
            }

            try
            {
                await Node.EnsureAsync(Report(progress, RuntimeComponent), cancellation);
                progress?.Report(new ComponentProgress(RuntimeComponent, $"Node.js {Node.Version}"));
                var updated = await BackendPackage.UpdateAsync(settings.Backend.Repository, Report(progress, BackendComponent), cancellation);
                progress?.Report(new ComponentProgress(BackendComponent, $"Core-Backend {BackendPackage.Installed?.Id}"));
                if (updated is not null && Backend.ProcessId is not null)
                {
                    BackendUpdateReady = updated.Id;
                    Log.Write($"Core-Backend {updated.Id} is installed; restart the backend to use it");
                }

                return true;
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException or TaskCanceledException or JsonException)
            {
                Log.Write($"Couldn't install Core-Backend: {exception.Message}");
                var component = Node.Executable is null ? RuntimeComponent : BackendComponent;
                progress?.Report(new ComponentProgress(component, exception.Message, Failed: true));
                return BackendPackage.Installed is not null;
            }
        }
        finally
        {
            _updating.Release();
        }
    }

    private static Progress<string>? Report(IProgress<ComponentProgress>? progress, string component) =>
        progress is null ? null : new Progress<string>(status => progress.Report(new ComponentProgress(component, status)));

    /// <summary>What the backend settings point at right now.</summary>
    public BackendTarget TargetFor(BackendSettings settings) => settings.Source switch
    {
        BackendSource.External => BackendTarget.Running(settings.Port),
        BackendSource.Folder => new(true, settings.Node, settings.Folder, settings.DataFolder.Length > 0 ? settings.DataFolder : Path.Combine(settings.Folder, "data"), settings.Port),
        _ => BackendPackage.Installed is { } installed && Node.Executable is { } node
            ? new(true, node, installed.Folder, settings.DataFolder.Length > 0 ? settings.DataFolder : Paths.BackendData, settings.Port)
            : new(true, "", "", "", settings.Port, "Core-Backend isn't installed yet; Check for updates installs it"),
    };

    public string BackendDescription => Settings.Backend.Source switch
    {
        BackendSource.External => $"Already running on port {Settings.Backend.Port}",
        BackendSource.Folder => $"Folder: {Settings.Backend.Folder}",
        _ => BackendPackage.Installed is { } installed
            ? $"Core-Backend {installed.Id}, Node.js {Node.Version}{(BackendUpdateReady is { } ready ? $" ({ready} installed, restart the backend to use it)" : "")}"
            : "Core-Backend not installed yet",
    };

    public void StartBackend()
    {
        Backend.Configure(TargetFor(Settings.Backend));
        Backend.Start();
    }

    public async Task RestartBackendAsync()
    {
        await Backend.StopAsync();
        if (Settings.Backend.Source == BackendSource.Managed && BackendPackage.Installed is null)
        {
            await UpdateComponentsAsync(null, checkLauncher: false, CancellationToken.None);
        }

        BackendUpdateReady = null;
        StartBackend();
        OnChanged();
    }

    public Task StopBackendAsync() => Backend.StopAsync();

    /// <summary>Saves the settings; once the launcher has started they also take effect (setup only saves them).</summary>
    public void SaveSettings(LauncherSettings updated)
    {
        var previous = Settings;
        _store.Save(updated);
        Settings = updated;
        if (_started)
        {
            Apply(updated, previous);
        }

        Log.Write("Settings saved");
        OnChanged();
    }

    private void Apply(LauncherSettings settings, LauncherSettings? previous)
    {
        Dlls.Configure(settings.Dlls);
        if (previous is not null && !SameJson(previous.Dlls, settings.Dlls))
        {
            CheckForUpdates();
        }

        if (previous is not null && !SameJson(previous.Backend, settings.Backend))
        {
            Log.Write("Backend settings changed, restarting it");
            _ = RestartBackendAsync();
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

        await _updates;
        if (!Backend.Running)
        {
            StartBackend();
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
        _shutdown.Cancel();
        await _updates;
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

            if (host.CheckingForUpdates)
            {
                return "checking for updates";
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
