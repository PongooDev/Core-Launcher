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
using Core.Launcher.Hosting.Components;
using Core.Launcher.Hosting.Logging;
using Core.Launcher.Hosting.Servers;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly LauncherHost _host;
    private readonly DispatcherTimer _clock;
    private bool _refreshQueued;
    private bool _syncing;

    public MainViewModel(LauncherHost host)
    {
        _host = host;
        Logs.Add(new LogTabViewModel(host.Log));
        Logs.Add(new LogTabViewModel(host.BackendLog));
        Logs.Add(new LogTabViewModel(host.ClientLog));
        SelectedLog = Logs[0];
        PlayerName = host.Settings.PlayerName;

        host.Changed += QueueRefresh;
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher.CurrentDispatcher);
        _clock.Start();
        Refresh();
    }

    public LauncherHost Host => _host;

    public ObservableCollection<BuildSettings> Builds { get; } = [];

    public ObservableCollection<ServerRowViewModel> Servers { get; } = [];

    public ObservableCollection<LogTabViewModel> Logs { get; } = [];

    [ObservableProperty]
    public partial BuildSettings? SelectedBuild { get; set; }

    [ObservableProperty]
    public partial string PlayerName { get; set; } = "";

    [ObservableProperty]
    public partial bool Hosting { get; set; }

    [ObservableProperty]
    public partial bool ClientRunning { get; set; }

    [ObservableProperty]
    public partial bool CanChangeBuild { get; set; }

    [ObservableProperty]
    public partial string ClientStatus { get; set; } = "";

    [ObservableProperty]
    public partial string BackendStatus { get; set; } = "";

    [ObservableProperty]
    public partial Brush BackendBrush { get; set; } = Brushes.Gray;

    [ObservableProperty]
    public partial bool BackendRunning { get; set; }

    [ObservableProperty]
    public partial string DllStatus { get; set; } = "";

    [ObservableProperty]
    public partial string BackendDescription { get; set; } = "";

    [ObservableProperty]
    public partial bool CanCheckForUpdates { get; set; }

    [ObservableProperty]
    public partial string UpdateText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasUpdate { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; } = "";

    [ObservableProperty]
    public partial bool Busy { get; set; }

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
            Hosting = _host.Servers.Enabled;

            var client = _host.Client;
            ClientRunning = client is not null;
            ClientStatus = client is null ? "Not running" : $"Running, pid {client.Id}, {Uptime(client.StartedAt)}";

            RefreshBackend();
            RefreshServers();

            CanChangeBuild = !_host.GamesRunning;
            CanCheckForUpdates = !_host.CheckingForUpdates && !Busy;
            DllStatus = _host.CheckingForUpdates ? "Checking for updates…" : _host.Dlls.Describe();
            BackendDescription = _host.BackendDescription;

            var update = _host.Updater.Available;
            HasUpdate = update is not null;
            if (!Busy)
            {
                UpdateText = update is null ? ""
                    : _host.Updater.Executable is null ? $"Core Launcher {update.Tag} is available on GitHub (this build can't update itself)."
                    : $"Core Launcher {update.Tag} is available.";
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RefreshBackend()
    {
        var backend = _host.Backend;
        BackendRunning = backend.Running;
        (BackendStatus, BackendBrush) = backend.State switch
        {
            BackendState.Ready => ($"Listening on port {backend.Port}{(backend.ProcessId is { } pid ? $", pid {pid}" : " (started outside the launcher)")}", Brush("GoodBrush")),
            BackendState.Starting => ("Starting…", Brush("BusyBrush")),
            BackendState.Restarting => ("Exited, restarting…", Brush("BusyBrush")),
            BackendState.Unreachable => (backend.Error ?? "Unreachable", Brush("BadBrush")),
            BackendState.Failed => (backend.Error ?? "Failed", Brush("BadBrush")),
            _ => ("Stopped", Brush("IdleBrush")),
        };
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
                Servers.Add(new ServerRowViewModel(_host.Servers));
            }

            Servers[index].Update(slots[index]);
            EnsureServerLog(slots[index].Instance);
        }
    }

    private void EnsureServerLog(int instance)
    {
        var channel = _host.ServerLog(instance);
        if (Logs.All(tab => tab.Channel != channel))
        {
            Logs.Add(new LogTabViewModel(channel));
        }
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

    partial void OnHostingChanged(bool value)
    {
        if (!_syncing && value != _host.Servers.Enabled)
        {
            _host.SetHosting(value);
        }
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        Message = "";
        Busy = true;
        try
        {
            if (PlayerName.Trim() != _host.Settings.PlayerName)
            {
                var updated = SettingsStore.Clone(_host.Settings);
                updated.PlayerName = PlayerName.Trim();
                _host.SaveSettings(updated);
            }

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
    private void StopClient() => _host.StopClient();

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
    private async Task UpdateLauncherAsync()
    {
        if (_host.Updater.Available is not { } release)
        {
            return;
        }

        if (_host.Updater.Executable is not { } executable)
        {
            Process.Start(new ProcessStartInfo($"https://github.com/{LauncherUpdater.Repository}/releases/latest") { UseShellExecute = true });
            return;
        }

        if (_host.GamesRunning &&
            MessageBox.Show(Application.Current.MainWindow, "Updating restarts the launcher, which stops the game and the servers. Update now?", "Core Launcher",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        Busy = true;
        Message = "";
        try
        {
            await _host.Updater.InstallAsync(release, new Progress<string>(status => UpdateText = $"Updating to {release.Tag}: {status}"), CancellationToken.None);
            ((App)Application.Current).RestartAfterUpdate(executable);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Message = $"Update failed: {exception.Message}";
            _host.Log.Write($"Launcher update failed: {exception.Message}");
            Busy = false;
        }
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        Directory.CreateDirectory(_host.Paths.Logs);
        Process.Start(new ProcessStartInfo(_host.Paths.Logs) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var window = new SettingsWindow(new SettingsViewModel(_host)) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
        Refresh();
    }

    private static string Uptime(DateTime startedAt)
    {
        var elapsed = DateTime.UtcNow - startedAt;
        return elapsed.TotalHours >= 1 ? $"up {(int)elapsed.TotalHours}h {elapsed.Minutes}m" : $"up {elapsed.Minutes}m {elapsed.Seconds}s";
    }

    internal static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}

public sealed partial class ServerRowViewModel(ServerPool pool) : ObservableObject
{
    [ObservableProperty]
    public partial int Instance { get; set; }

    [ObservableProperty]
    public partial string State { get; set; } = "";

    [ObservableProperty]
    public partial Brush StateBrush { get; set; } = Brushes.Gray;

    [ObservableProperty]
    public partial string Details { get; set; } = "";

    [ObservableProperty]
    public partial string Note { get; set; } = "";

    [ObservableProperty]
    public partial bool CanRestart { get; set; }

    public void Update(ServerSlotView slot)
    {
        Instance = slot.Instance;
        (State, StateBrush) = slot.State switch
        {
            ServerState.Ready => ("Ready", MainViewModel.Brush("GoodBrush")),
            ServerState.Starting => ("Booting", MainViewModel.Brush("BusyBrush")),
            ServerState.Stopping => ("Stopping", MainViewModel.Brush("BusyBrush")),
            _ => ("Waiting", MainViewModel.Brush("IdleBrush")),
        };

        var parts = new List<string>();
        if (slot.ProcessId is { } pid)
        {
            parts.Add($"pid {pid}");
        }

        if (slot.Since is { } since)
        {
            var elapsed = DateTimeOffset.UtcNow - since;
            parts.Add(elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m" : $"{elapsed.Minutes}m {elapsed.Seconds}s");
        }

        parts.Add(slot.Restarts == 1 ? "1 restart" : $"{slot.Restarts} restarts");
        Details = string.Join(" · ", parts);
        Note = slot.Note;
        CanRestart = slot.ProcessId is not null && slot.State != ServerState.Stopping;
    }

    [RelayCommand]
    private void Restart() => pool.Restart(Instance);
}

public sealed class LogTabViewModel(LogChannel channel)
{
    public LogChannel Channel { get; } = channel;

    public string Name => Channel.Name;
}
