using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Launcher.Hosting;
using Core.Launcher.Hosting.Backend;
using Core.Launcher.Hosting.Builds;
using Core.Launcher.Hosting.Dlls;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly LauncherHost _host;
    private readonly Action<bool> _finished;

    /// <param name="finished">Called with true after a save, false after the changes are discarded.</param>
    public SettingsViewModel(LauncherHost host, Action<bool> finished)
    {
        _host = host;
        _finished = finished;
        Settings = SettingsStore.Clone(host.Settings);
        foreach (var build in Settings.Builds)
        {
            Builds.Add(new BuildEditor(build));
        }

        SelectedBuild = Builds.FirstOrDefault(build => build.Settings.Name == Settings.SelectedBuild) ?? Builds.FirstOrDefault();
        foreach (var remote in Settings.Backend.SavedRemotes)
        {
            SavedRemotes.Add(remote);
        }
    }

    /// <summary>Friends' backends saved in the settings (the same objects, so edits and removals save with the rest).</summary>
    public ObservableCollection<RemoteBackend> SavedRemotes { get; } = [];

    [ObservableProperty]
    public partial RemoteBackend? SelectedSavedRemote { get; set; }

    [ObservableProperty]
    public partial string RemoteCheckMessage { get; set; } = "";

    [ObservableProperty]
    public partial bool RemoteCheckFailed { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestRemoteCommand))]
    public partial bool TestingRemote { get; set; }

    public bool BackendOwn
    {
        get => !Settings.Backend.UseRemote;
        set => SetUseRemote(!value);
    }

    public bool BackendRemote
    {
        get => Settings.Backend.UseRemote;
        set => SetUseRemote(value);
    }

    private void SetUseRemote(bool remote)
    {
        if (Settings.Backend.UseRemote == remote)
        {
            return;
        }

        Settings.Backend.UseRemote = remote;
        OnPropertyChanged(nameof(BackendOwn));
        OnPropertyChanged(nameof(BackendRemote));
    }

    public string RemoteName
    {
        get => Settings.Backend.Remote.Name;
        set
        {
            Settings.Backend.Remote.Name = value.Trim();
            OnPropertyChanged();
        }
    }

    public string RemoteAddress
    {
        get => Settings.Backend.Remote.Address;
        set
        {
            Settings.Backend.Remote.Address = value.Trim();
            OnPropertyChanged();
            RemoteCheckMessage = "";
        }
    }

    partial void OnSelectedSavedRemoteChanged(RemoteBackend? value)
    {
        if (value is null)
        {
            return;
        }

        RemoteName = value.Name;
        RemoteAddress = value.Address;
        BackendRemote = true;
    }

    [RelayCommand(CanExecute = nameof(CanTestRemote))]
    private async Task TestRemoteAsync()
    {
        TestingRemote = true;
        RemoteCheckFailed = false;
        RemoteCheckMessage = "Checking…";
        try
        {
            var status = await _host.CheckRemoteAsync(RemoteAddress);
            RemoteCheckFailed = !status.Reachable;
            RemoteCheckMessage = status.Reachable ? $"{status.Message}. You can play on it." : status.Message;
        }
        finally
        {
            TestingRemote = false;
        }
    }

    private bool CanTestRemote() => !TestingRemote;

    /// <summary>Adds the friend's backend being edited to the saved list, or updates the saved one with its name.</summary>
    [RelayCommand]
    private void SaveRemoteToList()
    {
        if (!BackendEndpoint.TryParse(RemoteAddress, out _, out var problem))
        {
            RemoteCheckFailed = true;
            RemoteCheckMessage = problem;
            return;
        }

        var key = RemoteName.Length > 0 ? RemoteName : RemoteAddress;
        var saved = SavedRemotes.FirstOrDefault(remote => string.Equals(remote.DisplayName, key, StringComparison.OrdinalIgnoreCase));
        if (saved is null)
        {
            saved = Settings.Backend.Remote.Copy();
            Settings.Backend.SavedRemotes.Add(saved);
            SavedRemotes.Add(saved);
        }
        else
        {
            saved.Name = RemoteName;
            saved.Address = RemoteAddress;
        }

        RemoteCheckFailed = false;
        RemoteCheckMessage = $"{saved.DisplayName} is in your list; pick it on Home to switch to it. Save to keep it.";
    }

    [RelayCommand]
    private void RemoveSavedRemote()
    {
        if (SelectedSavedRemote is not { } remote)
        {
            return;
        }

        Settings.Backend.SavedRemotes.Remove(remote);
        SavedRemotes.Remove(remote);
        SelectedSavedRemote = null;
    }

    public LauncherSettings Settings { get; }

    public ObservableCollection<BuildEditor> Builds { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBuild))]
    public partial BuildEditor? SelectedBuild { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; } = "";

    /// <summary>0 Builds, 1 Core DLLs, 2 Backend, 3 Servers, 4 About.</summary>
    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    public bool HasBuild => SelectedBuild is not null;

    public static string UnsupportedBuildsHint =>
        $"Core is only tested on the supported builds ({KnownBuilds.SupportedNames}). With this on, other builds get Core's defaults (the Core DLLs and -httpproxy) and can play and host, but they may not work. Builds whose Core options you've set yourself keep them.";

    /// <summary>
    /// Turning it on gives every unsupported build that still has every Core option off Core's defaults; turning it off
    /// takes them back off the builds that still have them unchanged. Builds set up by hand keep their settings.
    /// </summary>
    public bool AllowUnsupportedBuilds
    {
        get => Settings.AllowUnsupportedBuilds;
        set
        {
            if (value == Settings.AllowUnsupportedBuilds)
            {
                return;
            }

            var changed = KnownBuilds.SetAllowUnsupported(Settings, value);
            foreach (var editor in Builds.Where(editor => changed.Contains(editor.Settings)))
            {
                editor.Refresh();
            }

            OnPropertyChanged();
            var names = string.Join(", ", changed.Select(build => build.Name));
            Message = changed.Count == 0 ? ""
                : value ? $"Turned on Core's defaults for {names}. Save to keep them."
                : $"Took Core's defaults back off {names}. Save to keep it.";
        }
    }

    public bool UseRelease
    {
        get => Settings.Dlls.Source == DllSource.Release;
        set
        {
            Settings.Dlls.Source = value ? DllSource.Release : DllSource.Local;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UseLocal));
        }
    }

    public bool UseLocal
    {
        get => !UseRelease;
        set => UseRelease = !value;
    }

    public string LocalFolder
    {
        get => Settings.Dlls.LocalFolder;
        set
        {
            Settings.Dlls.LocalFolder = value.Trim();
            OnPropertyChanged();
        }
    }

    public bool BackendManaged
    {
        get => Settings.Backend.Source == BackendSource.Managed;
        set => SetBackendSource(value, BackendSource.Managed);
    }

    public bool BackendFromFolder
    {
        get => Settings.Backend.Source == BackendSource.Folder;
        set => SetBackendSource(value, BackendSource.Folder);
    }

    public bool BackendExternal
    {
        get => Settings.Backend.Source == BackendSource.External;
        set => SetBackendSource(value, BackendSource.External);
    }

    private void SetBackendSource(bool selected, BackendSource source)
    {
        if (!selected)
        {
            return;
        }

        Settings.Backend.Source = source;
        OnPropertyChanged(nameof(BackendManaged));
        OnPropertyChanged(nameof(BackendFromFolder));
        OnPropertyChanged(nameof(BackendExternal));
    }

    public string BackendFolder
    {
        get => Settings.Backend.Folder;
        set
        {
            Settings.Backend.Folder = value.Trim();
            OnPropertyChanged();
        }
    }

    public string BackendDataFolder
    {
        get => Settings.Backend.DataFolder;
        set
        {
            Settings.Backend.DataFolder = value.Trim();
            OnPropertyChanged();
        }
    }

    public Func<string, string?>? PickFolder { get; set; }

    [RelayCommand]
    private async Task AddBuildAsync()
    {
        if (PickFolder?.Invoke("Pick the build folder (the one with Engine and FortniteGame)") is not { } folder)
        {
            return;
        }

        Message = "Checking the build…";
        var detected = await Task.Run(() => BuildDetector.Detect(folder));
        Message = BuildMessages.Describe(detected, AllowUnsupportedBuilds).Text;
        if (detected is null)
        {
            return;
        }

        var build = KnownBuilds.Create(detected.Folder, detected.Version, AllowUnsupportedBuilds);
        var name = build.Name;
        for (var copy = 2; Settings.Builds.Any(existing => string.Equals(existing.Name, build.Name, StringComparison.OrdinalIgnoreCase)); ++copy)
        {
            build.Name = $"{name} ({copy})";
        }

        Settings.Builds.Add(build);
        var editor = new BuildEditor(build);
        Builds.Add(editor);
        SelectedBuild = editor;
        Message = KnownBuilds.IsKnown(detected.Version.Changelist) ? $"Added {build.Name}"
            : AllowUnsupportedBuilds ? $"Added {build.Name} with Core's defaults. {KnownBuilds.UnsupportedWarning(build.Name)}"
            : $"Added {build.Name}. Core doesn't support it yet: its DLLs stay off until you turn them on, or turn on {KnownBuilds.AllowUnsupportedSetting} to try it.";
    }

    [RelayCommand]
    private void RemoveBuild()
    {
        if (SelectedBuild is not { } build)
        {
            return;
        }

        Settings.Builds.Remove(build.Settings);
        Builds.Remove(build);
        SelectedBuild = Builds.FirstOrDefault();
    }

    [RelayCommand]
    private void BrowseLocalFolder()
    {
        if (PickFolder?.Invoke("Pick the folder with Core.dll, CoreClient.dll and CoreRedirect.dll") is { } folder)
        {
            LocalFolder = folder;
        }
    }

    [RelayCommand]
    private void BrowseBackendFolder()
    {
        if (PickFolder?.Invoke("Pick the Core-Backend folder") is { } folder)
        {
            BackendFolder = folder;
        }
    }

    [RelayCommand]
    private void BrowseBackendDataFolder()
    {
        if (PickFolder?.Invoke("Pick the backend's data folder") is { } folder)
        {
            BackendDataFolder = folder;
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (Builds.GroupBy(build => build.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1 || group.Key.Length == 0))
        {
            Message = "Every build needs its own name";
            return;
        }

        if (Settings.Dlls.Source == DllSource.Local && !Directory.Exists(Settings.Dlls.LocalFolder))
        {
            Message = "The local DLL folder doesn't exist";
            return;
        }

        if (Settings.Backend.UseRemote && !BackendEndpoint.TryParse(Settings.Backend.Remote.Address, out _, out var remoteProblem))
        {
            Message = $"Friend's backend: {remoteProblem}";
            SelectedTab = 2;
            return;
        }

        foreach (var build in Builds)
        {
            build.Settings.Name = build.Name.Trim();
        }

        if (SelectedBuild is not null)
        {
            Settings.SelectedBuild = SelectedBuild.Settings.Name;
        }

        _host.SaveSettings(Settings);
        _finished(true);
    }

    [RelayCommand]
    private void Discard() => _finished(false);

    public string Version => $"v{_host.Updater.Current.ToString(3)}";

    public string DataFolder => _host.Paths.Root;

    [RelayCommand]
    private void OpenDataFolder() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_host.Paths.Root) { UseShellExecute = true });

    [RelayCommand]
    private void OpenBuildFolder()
    {
        if (SelectedBuild is { } build && Directory.Exists(build.Settings.Path))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(build.Settings.Path) { UseShellExecute = true });
        }
    }
}

/// <summary>Edits one build; the DLL lists are kept in load order (redirect first).</summary>
public sealed partial class BuildEditor : ObservableObject
{
    private static readonly DllRole[] LoadOrder = [DllRole.Redirect, DllRole.Client, DllRole.Server];

    public BuildEditor(BuildSettings settings)
    {
        Settings = settings;
        Name = settings.Name;
    }

    public BuildSettings Settings { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    public string Path
    {
        get => Settings.Path;
        set
        {
            Settings.Path = value.Trim();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExecutableStatus));
        }
    }

    public string Executable
    {
        get => Settings.Executable;
        set
        {
            Settings.Executable = value.Trim();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExecutableStatus));
        }
    }

    public string ExecutableStatus => PeImage.CheckX64(Settings.ExecutablePath, dll: false) ?? "x64 executable found";

    public string ClientArguments
    {
        get => Settings.ClientArguments;
        set => Settings.ClientArguments = value.Trim();
    }

    public string ServerArguments
    {
        get => Settings.ServerArguments;
        set => Settings.ServerArguments = value.Trim();
    }

    public bool UseHttpProxy
    {
        get => Settings.UseHttpProxy;
        set => Settings.UseHttpProxy = value;
    }

    public bool ClientRedirect
    {
        get => Settings.ClientDlls.Contains(DllRole.Redirect);
        set => Toggle(Settings.ClientDlls, DllRole.Redirect, value);
    }

    public bool ClientClient
    {
        get => Settings.ClientDlls.Contains(DllRole.Client);
        set => Toggle(Settings.ClientDlls, DllRole.Client, value);
    }

    public bool ServerRedirect
    {
        get => Settings.ServerDlls.Contains(DllRole.Redirect);
        set => Toggle(Settings.ServerDlls, DllRole.Redirect, value);
    }

    public bool ServerServer
    {
        get => Settings.ServerDlls.Contains(DllRole.Server);
        set => Toggle(Settings.ServerDlls, DllRole.Server, value);
    }

    /// <summary>Shows settings changed outside the editor (Allow unsupported builds).</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    private static void Toggle(List<DllRole> list, DllRole role, bool on)
    {
        var roles = list.Where(existing => existing != role).ToHashSet();
        if (on)
        {
            roles.Add(role);
        }

        list.Clear();
        list.AddRange(LoadOrder.Where(roles.Contains));
    }
}
