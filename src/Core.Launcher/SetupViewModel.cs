using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Launcher.Hosting;
using Core.Launcher.Hosting.Builds;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher;

/// <summary>First run: the player's name, the build folder, then everything else installed for them.</summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly LauncherHost _host;
    private DetectedBuild? _detected;
    private Task _detecting = Task.CompletedTask;
    private int _detection;

    public SetupViewModel(LauncherHost host)
    {
        _host = host;
        var settings = host.Settings;
        PlayerName = settings.PlayerName;
        BuildFolder = settings.FindSelectedBuild()?.Path ?? "";
        UseLocalBackend = settings.Backend.Source == BackendSource.Folder;
        BackendFolder = settings.Backend.Folder;
        UseLocalDlls = settings.Dlls.Source == DllSource.Local;
        DllFolder = settings.Dlls.LocalFolder;
    }

    public ObservableCollection<SetupStepViewModel> Steps { get; } = [];

    public Func<string, string?>? PickFolder { get; set; }

    public event Action? Finished;

    [ObservableProperty]
    public partial string PlayerName { get; set; } = "";

    [ObservableProperty]
    public partial string BuildFolder { get; set; } = "";

    [ObservableProperty]
    public partial string BuildStatus { get; set; } = "";

    [ObservableProperty]
    public partial Brush BuildStatusBrush { get; set; } = Brushes.Gray;

    [ObservableProperty]
    public partial bool UseLocalBackend { get; set; }

    [ObservableProperty]
    public partial string BackendFolder { get; set; } = "";

    [ObservableProperty]
    public partial bool UseLocalDlls { get; set; }

    [ObservableProperty]
    public partial string DllFolder { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Editable))]
    public partial bool Installing { get; set; }

    [ObservableProperty]
    public partial bool Installed { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; } = "";

    public bool Editable => !Installing;

    // Reading the exe for its build string can take a moment, so detection runs off the UI thread and only the
    // latest folder's result is shown.
    partial void OnBuildFolderChanged(string value)
    {
        var folder = value.Trim();
        var detection = ++_detection;
        _detected = null;
        if (folder.Length == 0)
        {
            BuildStatus = "Pick the folder that has Engine and FortniteGame in it";
            BuildStatusBrush = MainViewModel.Brush("MutedBrush");
            _detecting = Task.CompletedTask;
            return;
        }

        BuildStatus = "Checking the build…";
        BuildStatusBrush = MainViewModel.Brush("MutedBrush");
        _detecting = DetectAsync(folder, detection);
    }

    private async Task DetectAsync(string folder, int detection)
    {
        var build = await Task.Run(() => BuildDetector.Detect(folder));
        if (detection != _detection)
        {
            return;
        }

        _detected = build;
        var (text, brush) = BuildMessages.Describe(build);
        BuildStatus = text;
        BuildStatusBrush = MainViewModel.Brush(brush);
    }

    [RelayCommand]
    private void BrowseBuild()
    {
        if (PickFolder?.Invoke("Pick the Fortnite build folder (the one with Engine and FortniteGame)") is { } folder)
        {
            BuildFolder = folder;
        }
    }

    [RelayCommand]
    private void BrowseBackend()
    {
        if (PickFolder?.Invoke("Pick your Core-Backend folder") is { } folder)
        {
            BackendFolder = folder;
        }
    }

    [RelayCommand]
    private void BrowseDlls()
    {
        if (PickFolder?.Invoke("Pick the folder with Core.dll, CoreClient.dll and CoreRedirect.dll") is { } folder)
        {
            DllFolder = folder;
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        Message = "";
        if (PlayerName.Trim().Length == 0 || PlayerName.Contains('"'))
        {
            Message = "Enter a player name (no quotes)";
            return;
        }

        await _detecting;
        if (_detected is not { } build)
        {
            Message = "Pick your Fortnite build folder";
            return;
        }

        if (UseLocalBackend && !File.Exists(Path.Combine(BackendFolder, "src", "main.js")))
        {
            Message = "That Core-Backend folder has no src\\main.js";
            return;
        }

        if (UseLocalDlls && !File.Exists(Path.Combine(DllFolder, "Core.dll")))
        {
            Message = "That DLL folder has no Core.dll";
            return;
        }

        var settings = SettingsStore.Clone(_host.Settings);
        settings.PlayerName = PlayerName.Trim();
        var buildSettings = settings.Builds.FirstOrDefault(existing => string.Equals(existing.Path, build.Folder, StringComparison.OrdinalIgnoreCase));
        if (buildSettings is null)
        {
            buildSettings = KnownBuilds.Create(build.Folder, build.Version);
            settings.Builds.Add(buildSettings);
        }

        settings.SelectedBuild = buildSettings.Name;
        settings.Backend.Source = UseLocalBackend ? BackendSource.Folder : BackendSource.Managed;
        settings.Backend.Folder = UseLocalBackend ? BackendFolder.Trim() : settings.Backend.Folder;
        settings.Dlls.Source = UseLocalDlls ? DllSource.Local : DllSource.Release;
        settings.Dlls.LocalFolder = UseLocalDlls ? DllFolder.Trim() : settings.Dlls.LocalFolder;
        _host.SaveSettings(settings);

        Steps.Clear();
        if (!UseLocalBackend)
        {
            Steps.Add(new SetupStepViewModel(LauncherHost.RuntimeComponent));
            Steps.Add(new SetupStepViewModel(LauncherHost.BackendComponent));
        }

        Steps.Add(new SetupStepViewModel(LauncherHost.DllComponent));

        Installing = true;
        try
        {
            var progress = new Progress<ComponentProgress>(update => Steps.FirstOrDefault(step => step.Name == update.Component)?.Report(update.Status, update.Failed));
            var backendReady = await _host.UpdateComponentsAsync(progress, checkLauncher: false, CancellationToken.None);
            foreach (var step in Steps)
            {
                step.Finish();
            }

            if (_host.Settings.Dlls.Source == DllSource.Release && _host.Dlls.ReleaseTag is null)
            {
                Steps.First(step => step.Name == LauncherHost.DllComponent)
                    .Warn("No Core V2 release yet. Play and the servers start once one is published, or pick a DLL folder in Settings.");
            }

            Installed = backendReady;
            Message = backendReady ? "" : "Core-Backend couldn't be installed. Check your connection and try again, or see Launcher.log.";
        }
        finally
        {
            Installing = false;
        }
    }

    [RelayCommand]
    private void Continue()
    {
        var settings = SettingsStore.Clone(_host.Settings);
        settings.SetupComplete = true;
        _host.SaveSettings(settings);
        Finished?.Invoke();
    }
}

public enum StepState
{
    Waiting,
    Working,
    Done,
    Warning,
    Failed,
}

public sealed partial class SetupStepViewModel(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateBrush), nameof(Working))]
    public partial StepState State { get; set; }

    public bool Working => State == StepState.Working;

    [ObservableProperty]
    public partial string Status { get; set; } = "Waiting";

    public Brush StateBrush => MainViewModel.Brush(State switch
    {
        StepState.Done => "GoodBrush",
        StepState.Working => "BusyBrush",
        StepState.Warning => "BusyBrush",
        StepState.Failed => "BadBrush",
        _ => "IdleBrush",
    });

    public void Report(string status, bool failed)
    {
        Status = status;
        if (State != StepState.Failed)
        {
            State = failed ? StepState.Failed : StepState.Working;
        }
    }

    public void Finish()
    {
        if (State == StepState.Working)
        {
            State = StepState.Done;
        }
    }

    public void Warn(string status)
    {
        Status = status;
        State = StepState.Warning;
    }
}
