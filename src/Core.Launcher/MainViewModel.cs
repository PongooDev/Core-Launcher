using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Launcher.Hosting;
using Core.Launcher.Hosting.Backend;
using Core.Launcher.Hosting.Builds;
using Core.Launcher.Hosting.Components;
using Core.Launcher.Hosting.Logging;
using Core.Launcher.Hosting.Servers;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher;

public enum LauncherPage
{
    Home,
    Servers,
    Logs,
    Settings,
}

public sealed partial class MainViewModel : ObservableObject
{
    public const string GitHubUrl = "https://github.com/PongooDev/Core-Launcher";
    public const string DiscordUrl = "https://discord.gg/auzEaKs7AS";

    private const int ActivityLines = 6;
    private const int MaxToasts = 4;
    private static readonly TimeSpan ToastLifetime = TimeSpan.FromSeconds(6);

    private readonly LauncherHost _host;
    private readonly DispatcherTimer _clock;
    private readonly Dictionary<int, ServerState> _lastSlots = [];
    private BackendState? _lastBackend;
    private string? _announcedUpdate;
    private Action? _statusAction;
    private bool _refreshQueued;
    private bool _syncing;

    public MainViewModel(LauncherHost host)
    {
        _host = host;
        Logs.Add(new LogTabViewModel(host.Log));
        Logs.Add(new LogTabViewModel(host.BackendLog));
        SelectedLog = Logs[0];
        PlayerName = host.Settings.PlayerName;
        foreach (var line in host.Log.Snapshot().TakeLast(ActivityLines))
        {
            Activity.Add(line);
        }

        host.Log.LineWritten += (_, line) => Application.Current?.Dispatcher.BeginInvoke(() => AddActivity(line));
        host.Changed += QueueRefresh;
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher.CurrentDispatcher);
        _clock.Start();
        Refresh();
    }

    public LauncherHost Host => _host;

    public string Version => $"v{_host.Updater.Current.ToString(3)}";

    public Func<string, string?>? PickFolder { get; set; }

    /// <summary>Asks for the login of another client: (suggested name, validation returning an error or null) → name, or null when cancelled.</summary>
    public Func<string, Func<string, string?>, string?>? PromptLogin { get; set; }

    public ObservableCollection<ClientRowViewModel> Clients { get; } = [];

    public ObservableCollection<BuildSettings> Builds { get; } = [];

    public ObservableCollection<ServerRowViewModel> Servers { get; } = [];

    public ObservableCollection<LogTabViewModel> Logs { get; } = [];

    public ObservableCollection<string> Activity { get; } = [];

    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    /// <summary>Home's "Play on" list: this PC's backend, then each saved friend's.</summary>
    public ObservableCollection<BackendChoice> BackendChoices { get; } = [];

    [ObservableProperty]
    public partial BackendChoice? SelectedBackendChoice { get; set; }

    /// <summary>Playing on a friend's backend: no backend or servers run here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanHost))]
    public partial bool UsingRemote { get; set; }

    public bool CanHost => !UsingRemote;

    [ObservableProperty]
    public partial string HostingOffText { get; set; } = "";

    [ObservableProperty]
    public partial LauncherPage CurrentPage { get; set; }

    [ObservableProperty]
    public partial SettingsViewModel? Settings { get; set; }

    [ObservableProperty]
    public partial BuildSettings? SelectedBuild { get; set; }

    [ObservableProperty]
    public partial string BuildDetail { get; set; } = "";

    [ObservableProperty]
    public partial string PlayerName { get; set; } = "";

    [ObservableProperty]
    public partial bool Hosting { get; set; }

    [ObservableProperty]
    public partial int Instances { get; set; }

    /// <summary>The player's own client (their player name) is running.</summary>
    [ObservableProperty]
    public partial bool ClientRunning { get; set; }

    [ObservableProperty]
    public partial bool AnyClientRunning { get; set; }

    [ObservableProperty]
    public partial bool CanChangeBuild { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayLabel))]
    public partial bool Busy { get; set; }

    public string PlayLabel => Busy ? "STARTING…" : ClientRunning ? "CLOSE GAME" : "PLAY";

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial Brush StatusBrush { get; set; } = Brushes.Gray;

    [ObservableProperty]
    public partial string StatusActionText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasStatusAction { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; } = "";

    [ObservableProperty]
    public partial string ServersSummary { get; set; } = "";

    [ObservableProperty]
    public partial string ServersDetail { get; set; } = "";

    [ObservableProperty]
    public partial Brush ServersBrush { get; set; } = Brushes.Gray;

    [ObservableProperty]
    public partial string BackendSummary { get; set; } = "";

    [ObservableProperty]
    public partial string BackendDetail { get; set; } = "";

    [ObservableProperty]
    public partial Brush BackendBrush { get; set; } = Brushes.Gray;

    [ObservableProperty]
    public partial bool BackendRunning { get; set; }

    [ObservableProperty]
    public partial bool CanOpenAdminPanel { get; set; }

    [ObservableProperty]
    public partial string AdminPanelHint { get; set; } = "";

    [ObservableProperty]
    public partial string DllSummary { get; set; } = "";

    [ObservableProperty]
    public partial string DllDetail { get; set; } = "";

    [ObservableProperty]
    public partial Brush DllBrush { get; set; } = Brushes.Gray;

    [ObservableProperty]
    public partial bool CanCheckForUpdates { get; set; }

    [ObservableProperty]
    public partial string UpdateText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasUpdate { get; set; }

    [ObservableProperty]
    public partial LogTabViewModel? SelectedLog { get; set; }

    private void QueueRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    private void Refresh()
    {
        _syncing = true;
        try
        {
            var settings = _host.Settings;
            if (!Builds.SequenceEqual(settings.Builds))
            {
                Builds.Clear();
                foreach (var build in settings.Builds)
                {
                    Builds.Add(build);
                }
            }

            SelectedBuild = settings.FindSelectedBuild();
            BuildDetail = SelectedBuild is not { } selected ? "No build yet"
                : !File.Exists(selected.ExecutablePath) ? "Build folder not found"
                : string.Join(" · ", new[]
                {
                    BuildVersion.Read(selected.Path)?.Engine,
                    selected.Changelist > 0 ? $"CL {selected.Changelist}" : null,
                    KnownBuilds.IsKnown(selected.Changelist) ? null : "not supported yet",
                }.Where(part => part is not null));
            Hosting = _host.Servers.Enabled;
            Instances = settings.Servers.Instances;
            UsingRemote = settings.Backend.UseRemote;
            HostingOffText = UsingRemote
                ? $"You're playing on {_host.BackendDescription}: its servers host the matches. Switch to your own backend to host here."
                : "Turn on Host servers to keep game servers ready for matches.";
            RefreshBackendChoices(settings.Backend);
            ClientRunning = _host.ClientFor(settings.PlayerName) is not null;
            RefreshClients();
            CanChangeBuild = !_host.GamesRunning;
            CanCheckForUpdates = !_host.CheckingForUpdates;
            OnPropertyChanged(nameof(PlayLabel));

            RefreshServers();
            RefreshBackend();
            RefreshDlls(settings);
            RefreshUpdate();
            RefreshStatus(settings);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RefreshClients()
    {
        var clients = _host.Clients;
        AnyClientRunning = clients.Count > 0;
        while (Clients.Count > clients.Count)
        {
            Clients.RemoveAt(Clients.Count - 1);
        }

        for (var index = 0; index < clients.Count; ++index)
        {
            if (index == Clients.Count)
            {
                Clients.Add(new ClientRowViewModel(_host, ShowClientLog));
            }

            Clients[index].Update(clients[index]);
            EnsureLog(_host.ClientLog(clients[index].Login));
        }
    }

    private void RefreshServers()
    {
        var slots = _host.Servers.Slots;
        while (Servers.Count > slots.Count)
        {
            Servers.RemoveAt(Servers.Count - 1);
        }

        for (var index = 0; index < slots.Count; ++index)
        {
            if (index == Servers.Count)
            {
                Servers.Add(new ServerRowViewModel(_host.Servers, ShowServerLog));
            }

            var slot = slots[index];
            Servers[index].Update(slot);
            EnsureServerLog(slot.Instance);

            _lastSlots.TryGetValue(slot.Instance, out var previous);
            if (slot.State == ServerState.Ready && previous != ServerState.Ready)
            {
                Toast($"Server {slot.Instance} is ready{(slot.Ports.Count > 0 ? $" on UDP {slot.Ports[0]}" : "")}", "GoodBrush");
            }
            else if (previous == ServerState.Ready && slot.State != ServerState.Ready && _host.Servers.Enabled)
            {
                Toast($"Server {slot.Instance} stopped; it will restart", "BusyBrush");
            }

            _lastSlots[slot.Instance] = slot.State;
        }

        var ready = slots.Count(slot => slot.State == ServerState.Ready);
        ServersSummary = _host.Servers.Enabled ? $"{ready} / {slots.Count} ready" : "Off";
        ServersDetail = UsingRemote ? "Off while you play on a friend's backend"
            : !_host.Servers.Enabled ? "Hosting is off"
            : slots.FirstOrDefault(slot => slot.State == ServerState.Starting) is { } booting ? $"Server {booting.Instance} is booting"
            : slots.FirstOrDefault(slot => slot.State == ServerState.Registering) is { } registering ? $"Server {registering.Instance} is registering its matchmaking session"
            : slots.FirstOrDefault(slot => slot.ProcessId is null) is { } waiting ? Capitalize(waiting.Note)
            : "All servers are up";
        ServersBrush = Brush(!_host.Servers.Enabled ? "IdleBrush" : slots.Count > 0 && ready == slots.Count ? "GoodBrush" : "BusyBrush");
    }

    private void RefreshBackend()
    {
        var backend = _host.Backend;
        BackendRunning = backend.Running;
        CanOpenAdminPanel = backend.AdminPanelUrl is not null;
        AdminPanelHint = backend.AdminPanelUrl is { } url ? $"Open Core-Backend's admin panel ({url})"
            : _host.Settings.Backend.UseRemote ? "A friend's admin panel only opens on their PC"
            : "The admin panel opens once Core-Backend is running";
        var remote = _host.Settings.Backend.UseRemote;
        (BackendSummary, BackendBrush) = (remote, backend.State) switch
        {
            (true, BackendState.Ready) => ("Connected", Brush("GoodBrush")),
            (true, BackendState.Starting) => ("Connecting…", Brush("BusyBrush")),
            (_, BackendState.Ready) => ("Running", Brush("GoodBrush")),
            (_, BackendState.Starting) => ("Starting…", Brush("BusyBrush")),
            (_, BackendState.Restarting) => ("Restarting…", Brush("BusyBrush")),
            (_, BackendState.Unreachable) => ("Not reachable", Brush("BadBrush")),
            (_, BackendState.Failed) => ("Needs attention", Brush("BadBrush")),
            _ => (_host.CheckingForUpdates ? "Installing…" : "Stopped", Brush(_host.CheckingForUpdates ? "BusyBrush" : "IdleBrush")),
        };
        BackendDetail = remote
            ? backend.RemoteStatus?.Version is { } version ? $"{_host.BackendDescription} · Core-Backend {version}" : _host.BackendDescription
            : backend.State == BackendState.Ready && _host.Settings.Backend.Source != BackendSource.External
            ? $"Port {backend.Port} · {_host.BackendDescription}"
            : _host.BackendDescription;

        if (_lastBackend is { } previous && previous != backend.State)
        {
            if (backend.State == BackendState.Ready)
            {
                Toast(remote ? $"Connected to {_host.BackendDescription}" : "Core-Backend is running", "GoodBrush");
            }
            else if (backend.State is BackendState.Failed or BackendState.Unreachable)
            {
                Toast($"Core-Backend: {backend.Error}", "BadBrush");
            }
        }

        _lastBackend = backend.State;
    }

    private void RefreshDlls(LauncherSettings settings)
    {
        if (settings.Dlls.Source == DllSource.Local)
        {
            DllSummary = "Local folder";
            DllDetail = settings.Dlls.LocalFolder.Length > 0 ? settings.Dlls.LocalFolder : "No folder set";
            DllBrush = Brush(Directory.Exists(settings.Dlls.LocalFolder) ? "GoodBrush" : "BadBrush");
            return;
        }

        DllSummary = _host.CheckingForUpdates && _host.Dlls.ReleaseTag is null ? "Checking…" : _host.Dlls.ReleaseTag ?? "Not downloaded";
        DllDetail = _host.Dlls.ReleaseTag is null ? $"No {settings.Dlls.Repository} release found yet" : $"{settings.Dlls.Repository} release";
        DllBrush = Brush(_host.Dlls.ReleaseTag is null ? "BusyBrush" : "GoodBrush");
    }

    private void RefreshUpdate()
    {
        var update = _host.Updater.Available;
        HasUpdate = update is not null;
        if (!Busy)
        {
            UpdateText = update is null ? ""
                : _host.Updater.Executable is null ? $"Core Launcher {update.Tag} is available on GitHub."
                : $"Core Launcher {update.Tag} is available.";
        }

        if (update is not null && _announcedUpdate != update.Tag)
        {
            _announcedUpdate = update.Tag;
            Toast($"Core Launcher {update.Tag} is available", "AccentBrush");
        }
    }

    /// <summary>One plain sentence about whether the player can play, with the one thing that fixes it when not.</summary>
    private void RefreshStatus(LauncherSettings settings)
    {
        var backend = _host.Backend;
        var build = settings.FindSelectedBuild();
        Action? action = null;
        var actionText = "";
        string text;
        string brush;

        if (_host.Clients is { Count: > 0 } clients)
        {
            (text, brush) = (clients.Count == 1
                ? $"In game as {clients[0].Login} · {Elapsed(clients[0].Game.StartedAt)}"
                : $"{clients.Count} clients in game: {string.Join(", ", clients.Select(client => client.Login))}", "GoodBrush");
        }
        else if (build is null)
        {
            (text, brush) = ("Add your Fortnite build to play", "BusyBrush");
            (actionText, action) = ("Choose build folder", ChooseBuildFolder);
        }
        else if (!File.Exists(build.ExecutablePath))
        {
            (text, brush) = ($"The {build.Name} folder can't be found", "BadBrush");
            (actionText, action) = ("Choose build folder", ChooseBuildFolder);
        }
        else if (!KnownBuilds.CanPlay(build))
        {
            (text, brush) = (KnownBuilds.NotSupported(build), "BusyBrush");
            (actionText, action) = ("Choose another build", ChooseBuildFolder);
        }
        else if (settings.Backend.UseRemote && backend.State is BackendState.Failed or BackendState.Unreachable)
        {
            (text, brush) = (backend.Error ?? $"{_host.BackendDescription} isn't reachable", "BadBrush");
            (actionText, action) = backend.State == BackendState.Failed
                ? ("Change address", () => OpenSettingsTab("2"))
                : ("Use my own backend", new Action(UseOwnBackend));
        }
        else if (settings.Backend.UseRemote && backend.State != BackendState.Ready)
        {
            (text, brush) = ($"Connecting to {_host.BackendDescription}…", "BusyBrush");
        }
        else if (backend.State == BackendState.Failed && backend.Error is { } error)
        {
            (text, brush) = (error, "BadBrush");
            if (error.Contains("already in use", StringComparison.Ordinal))
            {
                text = $"Another program is using port {_host.Backend.Target.Port}, maybe a Core-Backend you started yourself";
                (actionText, action) = ("Use the running backend", UseRunningBackend);
            }
            else if (error.Contains("isn't installed", StringComparison.Ordinal))
            {
                (actionText, action) = ("Install Core-Backend", () => _host.CheckForUpdates(startBackend: true));
            }
            else
            {
                (actionText, action) = ("Try again", () => _ = _host.RestartBackendAsync());
            }
        }
        else if (backend.State == BackendState.Unreachable)
        {
            (text, brush) = (backend.Error ?? "Core-Backend isn't reachable", "BadBrush");
            (actionText, action) = ("Let the launcher run it", UseManagedBackend);
        }
        else if (_host.CheckingForUpdates)
        {
            (text, brush) = ("Checking for updates…", "BusyBrush");
        }
        else if (backend.State != BackendState.Ready)
        {
            (text, brush) = ("Starting Core-Backend…", "BusyBrush");
        }
        else if (settings.Dlls.Source == DllSource.Release && _host.Dlls.ReleaseTag is null)
        {
            (text, brush) = ("Waiting for the Core DLLs: no release found yet", "BusyBrush");
            (actionText, action) = ("Check again", () => _host.CheckForUpdates());
        }
        else if (PlayerName.Trim().Length == 0)
        {
            (text, brush) = ("Enter a player name to play", "BusyBrush");
        }
        else if (settings.Backend.UseRemote)
        {
            (text, brush) = ($"Ready to play on {_host.BackendDescription}", "GoodBrush");
        }
        else if (_host.Servers.Enabled && !_host.Servers.Slots.Any(slot => slot.State == ServerState.Ready))
        {
            (text, brush) = ("Ready to play · servers are starting", "GoodBrush");
        }
        else
        {
            (text, brush) = ("Ready to play", "GoodBrush");
        }

        Status = text;
        StatusBrush = Brush(brush);
        StatusActionText = actionText;
        HasStatusAction = action is not null;
        _statusAction = action;
    }

    private void EnsureServerLog(int instance) => EnsureLog(_host.ServerLog(instance));

    private void EnsureLog(LogChannel channel)
    {
        if (Logs.All(tab => tab.Channel != channel))
        {
            Logs.Add(new LogTabViewModel(channel));
        }
    }

    private void ShowClientLog(string login)
    {
        var channel = _host.ClientLog(login);
        SelectedLog = Logs.FirstOrDefault(tab => tab.Channel == channel) ?? SelectedLog;
        CurrentPage = LauncherPage.Logs;
    }

    private void ShowServerLog(int instance)
    {
        var channel = _host.ServerLog(instance);
        SelectedLog = Logs.FirstOrDefault(tab => tab.Channel == channel) ?? SelectedLog;
        CurrentPage = LauncherPage.Logs;
    }

    private void AddActivity(string line)
    {
        Activity.Add(line);
        while (Activity.Count > ActivityLines)
        {
            Activity.RemoveAt(0);
        }
    }

    private void Toast(string text, string brush)
    {
        var toast = new ToastViewModel(text, Brush(brush));
        Toasts.Add(toast);
        while (Toasts.Count > MaxToasts)
        {
            Toasts.RemoveAt(0);
        }

        var timer = new DispatcherTimer { Interval = ToastLifetime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Toasts.Remove(toast);
        };
        timer.Start();
    }

    partial void OnCurrentPageChanged(LauncherPage value)
    {
        Settings = value == LauncherPage.Settings ? new SettingsViewModel(_host, OnSettingsFinished) : null;
    }

    private void OnSettingsFinished(bool saved)
    {
        Toast(saved ? "Settings saved" : "Changes discarded", saved ? "GoodBrush" : "IdleBrush");
        Settings = new SettingsViewModel(_host, OnSettingsFinished);
        PlayerName = _host.Settings.PlayerName;
        Refresh();
    }

    partial void OnSelectedBuildChanged(BuildSettings? value)
    {
        if (_syncing || value is null || value.Name == _host.Settings.SelectedBuild)
        {
            return;
        }

        var updated = SettingsStore.Clone(_host.Settings);
        updated.SelectedBuild = value.Name;
        _host.SaveSettings(updated);
    }

    private void RefreshBackendChoices(BackendSettings backend)
    {
        var choices = new List<BackendChoice> { new("My own backend", null) };
        choices.AddRange(backend.SavedRemotes.Select(remote => new BackendChoice(remote.DisplayName, remote)));
        if (backend.UseRemote && !backend.SavedRemotes.Any(remote => BackendChoice.Same(remote, backend.Remote)))
        {
            choices.Add(new BackendChoice(backend.Remote.DisplayName, backend.Remote));
        }

        if (!BackendChoices.SequenceEqual(choices))
        {
            BackendChoices.Clear();
            foreach (var choice in choices)
            {
                BackendChoices.Add(choice);
            }
        }

        SelectedBackendChoice = backend.UseRemote
            ? BackendChoices.Skip(1).FirstOrDefault(choice => BackendChoice.Same(choice.Remote!, backend.Remote))
            : BackendChoices[0];
    }

    partial void OnSelectedBackendChoiceChanged(BackendChoice? value)
    {
        if (_syncing || value is null)
        {
            return;
        }

        var current = _host.Settings.Backend;
        if (value.Remote is null ? !current.UseRemote : current.UseRemote && BackendChoice.Same(value.Remote, current.Remote))
        {
            return;
        }

        var updated = SettingsStore.Clone(_host.Settings);
        updated.Backend.UseRemote = value.Remote is not null;
        if (value.Remote is not null)
        {
            updated.Backend.Remote = value.Remote.Copy();
        }

        _host.SaveSettings(updated);
        Toast(value.Remote is null ? "Playing on your own backend" : $"Playing on {value.Label}", "AccentBrush");
    }

    private void UseOwnBackend()
    {
        var updated = SettingsStore.Clone(_host.Settings);
        updated.Backend.UseRemote = false;
        _host.SaveSettings(updated);
    }

    partial void OnHostingChanged(bool value)
    {
        if (!_syncing && value != _host.Servers.Enabled)
        {
            _host.SetHosting(value);
        }
    }

    [RelayCommand]
    private void SavePlayerName()
    {
        var name = PlayerName.Trim();
        if (name.Contains('"'))
        {
            Message = "A player name can't contain quotes";
            return;
        }

        Message = "";
        if (name != _host.Settings.PlayerName)
        {
            var updated = SettingsStore.Clone(_host.Settings);
            updated.PlayerName = name;
            _host.SaveSettings(updated);
        }
    }

    [RelayCommand]
    private void ChangeInstances(string delta)
    {
        var updated = SettingsStore.Clone(_host.Settings);
        updated.Servers.Instances = Math.Clamp(updated.Servers.Instances + int.Parse(delta), 1, SettingsStore.MaxInstances);
        if (updated.Servers.Instances != _host.Settings.Servers.Instances)
        {
            _host.SaveSettings(updated);
        }
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (ClientRunning)
        {
            _host.StopClient(_host.Settings.PlayerName);
            return;
        }

        SavePlayerName();
        if (Message.Length > 0)
        {
            return;
        }

        Busy = true;
        try
        {
            await _host.PlayAsync();
        }
        catch (Exception exception)
        {
            Message = exception.Message;
            _host.Log.Write($"Play: {exception.Message}");
        }
        finally
        {
            Busy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task LaunchAnotherClientAsync()
    {
        var suggestion = Enumerable.Range(2, 99).Select(number => $"{_host.Settings.PlayerName}{number}").First(name => _host.ClientFor(name) is null);
        if (PromptLogin?.Invoke(suggestion, ValidateLogin) is not { } login)
        {
            return;
        }

        Message = "";
        try
        {
            await _host.PlayAsync(login);
            Toast($"Starting a client as {login}", "GoodBrush");
        }
        catch (Exception exception)
        {
            Message = exception.Message;
            _host.Log.Write($"Play as {login}: {exception.Message}");
        }
        finally
        {
            Refresh();
        }
    }

    private string? ValidateLogin(string login) =>
        login.Trim().Length == 0 ? "Enter a username"
        : login.Contains('"') ? "A username can't contain quotes"
        : _host.ClientFor(login.Trim()) is not null ? $"{login.Trim()} is already in the game"
        : null;

    [RelayCommand]
    private void RunStatusAction() => _statusAction?.Invoke();

    [RelayCommand]
    private void Navigate(LauncherPage page) => CurrentPage = page;

    [RelayCommand]
    private void OpenSettingsTab(string tab)
    {
        CurrentPage = LauncherPage.Settings;
        if (Settings is not null)
        {
            Settings.SelectedTab = int.Parse(tab);
        }
    }

    [RelayCommand]
    private async Task RestartBackendAsync()
    {
        await _host.RestartBackendAsync();
        Refresh();
    }

    [RelayCommand]
    private async Task StopBackendAsync()
    {
        await _host.StopBackendAsync();
        Refresh();
    }

    [RelayCommand]
    private void CheckForUpdates() => _host.CheckForUpdates();

    [RelayCommand]
    private void OpenAdminPanel()
    {
        if (_host.Backend.AdminPanelUrl is { } url)
        {
            Open(url);
        }
    }

    [RelayCommand]
    private void CopyLog()
    {
        if (SelectedLog is { } log)
        {
            Clipboard.SetText(string.Join(Environment.NewLine, log.Channel.Snapshot()));
            Toast($"{log.Name} log copied", "GoodBrush");
        }
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        Directory.CreateDirectory(_host.Paths.Logs);
        Open(_host.Paths.Logs);
    }

    [RelayCommand]
    private static void OpenLink(string url) => Open(url);

    [RelayCommand]
    private async Task UpdateLauncherAsync()
    {
        if (_host.Updater.Available is not { } release)
        {
            return;
        }

        if (_host.Updater.Executable is not { } executable)
        {
            Open($"https://github.com/{LauncherUpdater.Repository}/releases/latest");
            return;
        }

        if (_host.GamesRunning &&
            MessageBox.Show(Application.Current.MainWindow, "Updating restarts the launcher, which stops the game and the servers. Update now?", "Core Launcher",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        Busy = true;
        try
        {
            await _host.Updater.InstallAsync(release, new Progress<string>(status => UpdateText = $"Updating to {release.Tag}: {status}"), CancellationToken.None);
            ((App)Application.Current).RestartAfterUpdate(executable);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Toast($"Update failed: {exception.Message}", "BadBrush");
            _host.Log.Write($"Launcher update failed: {exception.Message}");
            Busy = false;
        }
    }

    private async void ChooseBuildFolder()
    {
        if (PickFolder?.Invoke("Pick your Fortnite build folder (the one with Engine and FortniteGame)") is not { } folder)
        {
            return;
        }

        var detected = await Task.Run(() => BuildDetector.Detect(folder));
        var (description, brush) = BuildMessages.Describe(detected);
        if (detected is null)
        {
            Toast(description, brush);
            return;
        }

        var updated = SettingsStore.Clone(_host.Settings);
        var build = updated.FindSelectedBuild();
        if (build is not null && !File.Exists(build.ExecutablePath) && KnownBuilds.Create(detected.Folder, detected.Version).Name == build.Name)
        {
            build.Path = detected.Folder;
        }
        else
        {
            build = KnownBuilds.Create(detected.Folder, detected.Version);
            if (updated.Builds.All(existing => !string.Equals(existing.Name, build.Name, StringComparison.OrdinalIgnoreCase)))
            {
                updated.Builds.Add(build);
            }
        }

        updated.SelectedBuild = build.Name;
        _host.SaveSettings(updated);
        Toast(KnownBuilds.IsKnown(build.Changelist) ? $"Using {build.Name}" : description, brush);
    }

    private void UseRunningBackend()
    {
        var updated = SettingsStore.Clone(_host.Settings);
        updated.Backend.UseRemote = false;
        updated.Backend.Source = BackendSource.External;
        _host.SaveSettings(updated);
    }

    private void UseManagedBackend()
    {
        var updated = SettingsStore.Clone(_host.Settings);
        updated.Backend.UseRemote = false;
        updated.Backend.Source = BackendSource.Managed;
        _host.SaveSettings(updated);
    }

    private static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    internal static string Elapsed(DateTime startedAt)
    {
        var elapsed = DateTime.UtcNow - startedAt;
        return elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m" : elapsed.TotalMinutes >= 1 ? $"{elapsed.Minutes}m {elapsed.Seconds}s" : $"{elapsed.Seconds}s";
    }

    internal static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}

public sealed partial class ServerRowViewModel(ServerPool pool, Action<int> showLog) : ObservableObject
{
    [ObservableProperty]
    public partial int Instance { get; set; }

    [ObservableProperty]
    public partial string State { get; set; } = "";

    [ObservableProperty]
    public partial Brush StateBrush { get; set; } = Brushes.Gray;

    [ObservableProperty]
    public partial string Headline { get; set; } = "";

    [ObservableProperty]
    public partial string Details { get; set; } = "";

    [ObservableProperty]
    public partial bool CanRestart { get; set; }

    [ObservableProperty]
    public partial bool Booting { get; set; }

    public void Update(ServerSlotView slot)
    {
        Instance = slot.Instance;
        (State, StateBrush) = slot.State switch
        {
            ServerState.Ready => ("Ready", MainViewModel.Brush("GoodBrush")),
            ServerState.Starting => ("Booting", MainViewModel.Brush("BusyBrush")),
            ServerState.Registering => ("Registering", MainViewModel.Brush("BusyBrush")),
            ServerState.Stopping => ("Stopping", MainViewModel.Brush("BusyBrush")),
            _ => ("Waiting", MainViewModel.Brush("IdleBrush")),
        };
        Booting = slot.State is ServerState.Starting or ServerState.Registering;
        Headline = slot.State switch
        {
            ServerState.Ready => slot.Ports.Count > 0 ? $"Ready on UDP {string.Join(", ", slot.Ports)}" : "Ready",
            _ => slot.Note.Length > 0 ? char.ToUpperInvariant(slot.Note[0]) + slot.Note[1..] : State,
        };

        var parts = new List<string>();
        if (slot.ProcessId is { } pid)
        {
            parts.Add($"pid {pid}");
        }

        if (slot.Since is { } since)
        {
            parts.Add($"up {MainViewModel.Elapsed(since.UtcDateTime)}");
        }

        parts.Add(slot.Restarts == 1 ? "1 restart" : $"{slot.Restarts} restarts");
        Details = string.Join(" · ", parts);
        CanRestart = slot.ProcessId is not null && slot.State != ServerState.Stopping;
    }

    [RelayCommand]
    private void Restart() => pool.Restart(Instance);

    [RelayCommand]
    private void ViewLog() => showLog(Instance);
}

public sealed partial class ClientRowViewModel(LauncherHost host, Action<string> showLog) : ObservableObject
{
    [ObservableProperty]
    public partial string Login { get; set; } = "";

    [ObservableProperty]
    public partial string Details { get; set; } = "";

    public void Update(ClientSession session)
    {
        Login = session.Login;
        Details = $"pid {session.Game.Id} · up {MainViewModel.Elapsed(session.Game.StartedAt)}";
    }

    [RelayCommand]
    private void Close() => host.StopClient(Login);

    [RelayCommand]
    private void ViewLog() => showLog(Login);
}

public sealed class LogTabViewModel(LogChannel channel)
{
    public LogChannel Channel { get; } = channel;

    public string Name => Channel.Name;
}

public sealed record ToastViewModel(string Text, Brush Brush);

/// <summary>One entry of Home's "Play on" list: this PC's backend (no <see cref="Remote"/>) or a friend's.</summary>
public sealed record BackendChoice(string Label, RemoteBackend? Remote)
{
    public static bool Same(RemoteBackend left, RemoteBackend right) =>
        string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(left.Address, right.Address, StringComparison.OrdinalIgnoreCase);

    public bool Equals(BackendChoice? other) =>
        other is not null && Label == other.Label && (Remote is null ? other.Remote is null : other.Remote is not null && Same(Remote, other.Remote));

    public override int GetHashCode() => HashCode.Combine(Label, Remote?.Address);
}
