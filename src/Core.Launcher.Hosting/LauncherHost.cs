using System.Text.Json;
using Core.Launcher.Hosting.Backend;
using Core.Launcher.Hosting.Builds;
using Core.Launcher.Hosting.Components;
using Core.Launcher.Hosting.Dlls;
using Core.Launcher.Hosting.Logging;
using Core.Launcher.Hosting.Native;
using Core.Launcher.Hosting.Processes;
using Core.Launcher.Hosting.Servers;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Hosting;

/// <summary>A running game client and the account it logged in with.</summary>
public sealed record ClientSession(string Login, RunningGame Game);

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
    private readonly BackendSessions _sessions = new(BackendSessions.CreateHttpClient(), TimeProvider.System);
    private readonly HttpClient _remoteCheck = RemoteBackendCheck.CreateHttpClient();
    private readonly GameLauncher _launcher;
    private readonly Dictionary<int, LogChannel> _serverLogs = [];
    private readonly Dictionary<string, LogChannel> _clientLogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ClientSession> _clients = [];
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

    public DllStore Dlls { get; }

    public NodeRuntime Node { get; }

    public BackendPackage BackendPackage { get; }

    public LauncherUpdater Updater { get; }

    public BackendHost Backend { get; }

    public ServerPool Servers { get; }

    public IReadOnlyList<ClientSession> Clients
    {
        get
        {
            lock (_gate)
            {
                return [.. _clients];
            }
        }
    }

    public ClientSession? ClientFor(string login) => Clients.FirstOrDefault(client => string.Equals(client.Login, login, StringComparison.OrdinalIgnoreCase));

    public bool CheckingForUpdates => !_updates.IsCompleted;

    /// <summary>A newer managed Core-Backend installed while the old one runs; it is used from the backend's next start.</summary>
    public string? BackendUpdateReady { get; private set; }

    public bool GamesRunning => Clients.Count > 0 || Servers.Slots.Any(slot => slot.ProcessId is not null);

    public bool AnythingRunning => GamesRunning || Backend.ProcessId is not null;

    /// <summary>Playing on a friend's backend: no backend runs here, and servers here host for theirs.</summary>
    public bool UsingRemote => Settings.Backend.UseRemote;

    /// <summary>Server <paramref name="instance"/>'s login: CoreServer-&lt;PC&gt;-{n} on a friend's backend (<see cref="ServerSettings.RemoteLoginFor"/>).</summary>
    public string ServerLogin(int instance) => ServerLogin(Settings, instance);

    private static string ServerLogin(LauncherSettings settings, int instance) =>
        settings.Backend.UseRemote ? settings.Servers.RemoteLoginFor(instance, Environment.MachineName) : settings.Servers.LoginFor(instance);

    /// <summary>
    /// Why a friend's backend won't take this PC's servers, when it has said so: its Trusted Hosting IPs don't have this
    /// PC's IP (it answers 403). Null otherwise, and always on this PC's own backend.
    /// </summary>
    public string? HostingRefusal => Settings.Backend.UseRemote && Backend.Endpoint is { } friend && _sessions.Access(friend) == HostingAccess.Refused
        ? RefusalMessage(BackendDescription)
        : null;

    public static string RefusalMessage(string backend) =>
        $"{backend} refused this PC's servers (403): ask your friend to add this PC's public IP under Settings → Trusted Hosting IPs in their admin panel";

    /// <summary>The launcher installs and runs Core-Backend itself (not a folder, an outside one or a friend's).</summary>
    private static bool ManagesBackend(BackendSettings settings) => !settings.UseRemote && settings.Source == BackendSource.Managed;

    public event Action? Changed;

    public LogChannel ClientLog(string login)
    {
        lock (_gate)
        {
            if (!_clientLogs.TryGetValue(login, out var channel))
            {
                channel = new LogChannel($"Client {login}");
                _clientLogs[login] = channel;
            }

            return channel;
        }
    }

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

            if (!ManagesBackend(settings.Backend))
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
    public BackendTarget TargetFor(BackendSettings settings)
    {
        if (settings.UseRemote)
        {
            return BackendEndpoint.TryParse(settings.Remote.Address, out var friend, out var problem)
                ? BackendTarget.Friend(friend, settings.Remote.Name)
                : BackendTarget.FriendProblem(settings.Remote.Address.Length == 0 ? "Enter your friend's backend address (Settings → Backend)" : problem, settings.Remote.Name);
        }

        return settings.Source switch
        {
            BackendSource.External => BackendTarget.Running(settings.Port),
            BackendSource.Folder => new(true, settings.Node, settings.Folder, settings.DataFolder.Length > 0 ? settings.DataFolder : Path.Combine(settings.Folder, "data"), settings.Port),
            _ => BackendPackage.Installed is { } installed && Node.Executable is { } node
                ? new(true, node, installed.Folder, settings.DataFolder.Length > 0 ? settings.DataFolder : Paths.BackendData, settings.Port)
                : new(true, "", "", "", settings.Port, "Core-Backend isn't installed yet; Check for updates installs it"),
        };
    }

    public string BackendDescription
    {
        get
        {
            var backend = Settings.Backend;
            if (backend.UseRemote)
            {
                return $"{(backend.Remote.Name.Length > 0 ? backend.Remote.Name + "'s backend" : "a friend's backend")} at {backend.Remote.Address}";
            }

            return backend.Source switch
            {
                BackendSource.External => $"Already running on port {backend.Port}",
                BackendSource.Folder => $"Folder: {backend.Folder}",
                _ => BackendPackage.Installed is { } installed
                    ? $"Core-Backend {installed.Id}, Node.js {Node.Version}{(BackendUpdateReady is { } ready ? $" ({ready} installed, restart the backend to use it)" : "")}"
                    : "Core-Backend not installed yet",
            };
        }
    }

    public void StartBackend()
    {
        Backend.Configure(TargetFor(Settings.Backend));
        Backend.Start();
    }

    public async Task RestartBackendAsync()
    {
        await Backend.StopAsync();
        if (ManagesBackend(Settings.Backend))
        {
            try
            {
                await UpdateComponentsAsync(null, checkLauncher: false, _shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                Log.Write($"Checking for updates failed: {exception}");
            }
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

        Servers.Configure(KnownBuilds.ServerCount(settings.Servers, settings.FindSelectedBuild()), TimeSpan.FromSeconds(settings.Servers.ReadyTimeoutSeconds));
        // On a friend's backend the servers here host for theirs (Core-Launcher #6), under logins of their own
        // (ServerSettings.RemoteLoginFor), once it trusts this PC's IP.
        Servers.SetEnabled(settings.Servers.Enabled);
        if (previous is not null && ServersMove(previous.Backend, settings.Backend))
        {
            Log.Write("The servers' backend changed, restarting them");
            foreach (var slot in Servers.Slots.Where(slot => slot.ProcessId is not null))
            {
                Servers.Restart(slot.Instance);
            }
        }
    }

    /// <summary>Whether running servers log in to another backend now: between this PC's and a friend's, or another friend's.</summary>
    public static bool ServersMove(BackendSettings previous, BackendSettings current) =>
        previous.UseRemote != current.UseRemote
        || (current.UseRemote && !string.Equals(previous.Remote.Address.Trim(), current.Remote.Address.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Checks a friend's backend address without switching to it (Settings → Test connection).</summary>
    public async Task<RemoteBackendStatus> CheckRemoteAsync(string address, CancellationToken cancellation = default)
    {
        if (!BackendEndpoint.TryParse(address, out var endpoint, out var problem))
        {
            return new RemoteBackendStatus(false, problem);
        }

        var status = await RemoteBackendCheck.CheckAsync(_remoteCheck, endpoint, cancellation);
        Log.Write($"Checked {endpoint}: {status.Message}");
        return status;
    }

    public void SetHosting(bool enabled)
    {
        var updated = SettingsStore.Clone(Settings);
        updated.Servers.Enabled = enabled;
        SaveSettings(updated);
    }

    /// <summary>The registration page of the active backend in use (local or friend's).</summary>
    public string? RegisterUrl => Backend.RegisterUrl ?? (BackendEndpoint.TryParse(Settings.Backend.Remote.Address, out var ep, out _) ? $"{ep.Url}/register" : null);

    /// <summary>Checks authentication requirements for the given username against the active backend.</summary>
    public async Task<AuthRequirements?> GetAuthRequirementsAsync(string username)
    {
        var endpoint = Backend.Endpoint ?? (Settings.Backend.UseRemote && BackendEndpoint.TryParse(Settings.Backend.Remote.Address, out var ep, out _) ? ep : null);
        if (endpoint is null)
        {
            return null;
        }

        return await BackendAuthClient.GetAuthRequirementsAsync(_remoteCheck, endpoint, username);
    }
    /// <summary>
    /// Starts a client logged in as <paramref name="login"/> (the player's name when null), beside any already running;
    /// throws with a message for the user when it can't. One client per account: a second login would end the first
    /// one's session.
    /// </summary>
    public async Task PlayAsync(string? login = null, string? password = null)
    {
        var settings = Settings;
        login = (login ?? settings.PlayerName).Trim();
        password = (password ?? (string.Equals(login, settings.PlayerName, StringComparison.OrdinalIgnoreCase) ? settings.Password : null))?.Trim();
        if (ClientFor(login) is not null)
        {
            throw new InvalidOperationException($"{login} is already in the game");
        }

        if (PasswordValidation.ValidateUsername(login) is { } userProblem)
        {
            throw new InvalidOperationException(userProblem);
        }

        var isDefaultCorePassword = string.Equals(password, LaunchArguments.Password, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(password) && !isDefaultCorePassword &&
            PasswordValidation.Validate(password) is { } passwordProblem)
        {
            throw new InvalidOperationException(passwordProblem);
        }

        var build = settings.FindSelectedBuild() ?? throw new InvalidOperationException("Add a build first (Settings → Builds)");
        if (!KnownBuilds.CanPlay(build))
        {
            throw new InvalidOperationException(KnownBuilds.NotSupported(build, settings.AllowUnsupportedBuilds));
        }

        await _updates;
        if (!Backend.Running)
        {
            StartBackend();
        }

        if (!await Backend.WaitReadyAsync(BackendReadyTimeout) || Backend.Endpoint is not { } backend)
        {
            throw new InvalidOperationException(settings.Backend.UseRemote
                ? $"Can't play on {BackendDescription}: {Backend.Error ?? Backend.State.ToString()}"
                : $"Core-Backend isn't ready: {Backend.Error ?? Backend.State.ToString()}");
        }

        var requirements = await BackendAuthClient.GetAuthRequirementsAsync(_remoteCheck, backend, login);
        var passwordRequired = requirements?.PasswordRequired ?? false;

        string? effectivePassword = null;
        if (passwordRequired)
        {
            if (string.IsNullOrWhiteSpace(password) || isDefaultCorePassword)
            {
                if (requirements is { AccountExists: true, HasPassword: false, RequirePassword: true })
                {
                    throw new InvalidOperationException("This account was created before passwords were required. Please ask your server host for a Password Reset Link to secure your account.");
                }

                throw new InvalidOperationException("A password is required to log into this account.");
            }

            if (PasswordValidation.Validate(password, required: true) is { } requiredProblem)
            {
                throw new InvalidOperationException(requiredProblem);
            }

            effectivePassword = password;
            var authResult = await BackendAuthClient.AuthenticateAsync(_remoteCheck, backend, login, effectivePassword);
            if (!authResult.Succeeded && authResult.StatusCode != 0)
            {
                throw new InvalidOperationException(authResult.ErrorMessage);
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(password) && !isDefaultCorePassword)
            {
                if (PasswordValidation.Validate(password, required: false) is { } customProblem)
                {
                    throw new InvalidOperationException(customProblem);
                }

                effectivePassword = password;
                var authResult = await BackendAuthClient.AuthenticateAsync(_remoteCheck, backend, login, effectivePassword);
                if (!authResult.Succeeded && authResult.StatusCode != 0)
                {
                    throw new InvalidOperationException(authResult.ErrorMessage);
                }
            }
            else
            {
                effectivePassword = LaunchArguments.Password;
            }
        }

        var output = ClientLog(login);
        output.Write($"(launcher) --- {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
        if (settings.Backend.UseRemote)
        {
            output.Write($"(launcher) Playing on {BackendDescription} ({backend.Url})");
        }

        WarnAboutSupport(build, output);

        var session = new ClientSession(login, _launcher.Start(new GameStart($"Client {login}", build, build.ClientDlls, LaunchArguments.Client(build, backend, login, effectivePassword), false, output)));
        lock (_gate)
        {
            _clients.Add(session);
        }

        OnChanged();
        _ = session.Game.Finished.ContinueWith(_ =>
        {
            session.Game.Dispose();
            lock (_gate)
            {
                _clients.Remove(session);
            }

            OnChanged();
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Starts the log with a note when Core doesn't fully support the build: its support is in progress, or it is
    /// unsupported (which only gets this far with Allow unsupported builds on, or the Core DLLs turned on by hand).
    /// </summary>
    private static void WarnAboutSupport(BuildSettings build, LogChannel output)
    {
        if (KnownBuilds.SupportWarning(build) is { } warning)
        {
            output.Write($"(launcher) {warning}");
        }
    }

    public void StopClient(string login) => ClientFor(login)?.Game.Terminate();

    public async ValueTask DisposeAsync()
    {
        Log.Write("Shutting down");
        _shutdown.Cancel();
        await _updates;
        await Servers.StopAllAsync(ShutdownTimeout);
        var clients = Clients;
        foreach (var client in clients)
        {
            client.Game.Terminate();
        }

        await Task.WhenAny(Task.WhenAll(clients.Select(client => client.Game.Finished)), Task.Delay(ShutdownTimeout));
        lock (_gate)
        {
            _clients.RemoveAll(client => client.Game.Finished.IsCompleted);
        }

        await Backend.DisposeAsync();
        Servers.Dispose();
        _sessions.Dispose();
        _remoteCheck.Dispose();
        _http.Dispose();
        _job.Dispose();
        Log.Write("Stopped");
        Log.Dispose();
        BackendLog.Dispose();
    }

    private void OnChanged() => Changed?.Invoke();

    /// <summary>Whether the selected build has Battle Royale, so some servers host it.</summary>
    public bool BuildHasBattleRoyale => Settings.FindSelectedBuild() is { } build && KnownBuilds.HasBattleRoyale(build.Changelist);

    /// <summary>Whether server <paramref name="instance"/> hosts Battle Royale (<see cref="ServerSettings.BattleRoyaleInstances"/>).</summary>
    public bool HostsBattleRoyale(int instance) => BuildHasBattleRoyale && Settings.Servers.HostsBattleRoyale(instance);

    private static bool SameJson<T>(T left, T right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    private sealed class ServerStarter(LauncherHost host) : IServerStarter
    {
        public string? Blocker()
        {
            if (host.Settings.FindSelectedBuild() is not { } build)
            {
                return "no build (Settings → Builds)";
            }

            if (!KnownBuilds.CanHost(build))
            {
                return KnownBuilds.NotSupported(build, host.Settings.AllowUnsupportedBuilds);
            }

            if (host.CheckingForUpdates)
            {
                return "checking for updates";
            }

            if (Backend() is not { } backend)
            {
                return host.Settings.Backend.UseRemote ? $"waiting for {host.BackendDescription}" : "waiting for the backend";
            }

            if (!host.Settings.Backend.UseRemote)
            {
                return null;
            }

            // A friend's backend answers GET /core/api/sessions only to the PCs it trusts to host (Core-Backend #125):
            // ask it first, so a refusal shows here instead of as servers that fail to log in.
            return host._sessions.Access(backend) switch
            {
                HostingAccess.Allowed => null,
                HostingAccess.Refused => RefusalMessage(host.BackendDescription),
                _ => $"asking {host.BackendDescription} whether this PC may host for it",
            };
        }

        /// <summary>
        /// The backend the servers log in to: this PC's, or the friend's in use. The one the settings name, not the previous
        /// one while a switch restarts the backend.
        /// </summary>
        private BackendEndpoint? Backend()
        {
            var settings = host.Settings.Backend;
            if (host.Backend.Endpoint is not { } endpoint || host.Backend.Target.Remote != settings.UseRemote)
            {
                return null;
            }

            return !settings.UseRemote || (BackendEndpoint.TryParse(settings.Remote.Address, out var friend, out _) && friend == endpoint) ? endpoint : null;
        }

        public IServerInstance Start(int instance)
        {
            var settings = host.Settings;
            var build = settings.FindSelectedBuild()!;
            var battleRoyale = host.HostsBattleRoyale(instance);
            var backend = Backend() ?? throw new InvalidOperationException("The backend isn't ready");
            var arguments = LaunchArguments.Server(build, backend, ServerLogin(settings, instance), battleRoyale, settings.Servers.Region);
            var output = host.ServerLog(instance);
            output.Write($"(launcher) --- {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
            if (settings.Backend.UseRemote)
            {
                output.Write($"(launcher) Hosting for {host.BackendDescription} ({backend.Url}) as {ServerLogin(settings, instance)}");
            }

            WarnAboutSupport(build, output);
            WriteServerConfig();
            return host._launcher.Start(new GameStart($"Server {instance}", build, build.ServerDlls, arguments, true, output));
        }

        private void WriteServerConfig()
        {
            try
            {
                if (ServerConfig.WriteEngineIni(host.Paths.ServerSaved))
                {
                    host.Log.Write($"Wrote {ServerConfig.EngineIniPath(host.Paths.ServerSaved)}: servers run without XMPP");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The server still starts; its failed XMPP connection keeps a core busy (CoreV2 #109).
                host.Log.Write($"Couldn't write the servers' Engine.ini ({exception.Message}); they will try XMPP and keep a core busy");
            }
        }

        public SessionCheck Session(int instance, DateTimeOffset startedAt, IReadOnlyList<int> ports) =>
            Backend() is { } backend
                ? host._sessions.Check(backend, ServerLogin(host.Settings, instance), startedAt, ports)
                : SessionCheck.Pending;
    }
}
