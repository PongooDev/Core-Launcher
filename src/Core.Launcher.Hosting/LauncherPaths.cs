namespace Core.Launcher.Hosting;

public sealed class LauncherPaths
{
    public LauncherPaths(string root)
    {
        Root = root;
    }

    public static LauncherPaths Default { get; } =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Core-Launcher"));

    public string Root { get; }

    public string Settings => Path.Combine(Root, "settings.json");

    public string ReleaseDlls => Path.Combine(Root, "Dlls", "Release");

    public string LocalDlls => Path.Combine(Root, "Dlls", "Local");

    public string Runtime => Path.Combine(Root, "Runtime");

    public string Backend => Path.Combine(Root, "Backend");

    public string BackendVersions => Path.Combine(Backend, "versions");

    /// <summary>The managed backend's accounts and profiles; shared by every installed version.</summary>
    public string BackendData => Path.Combine(Backend, "data");

    public string Logs => Path.Combine(Root, "Logs");

    public string CoreLogs => Path.Combine(Logs, "Core");

    /// <summary>Fortnite's own folder (FPaths::GameUserDir), whatever the launcher's is: its Saved folders are here.</summary>
    public string GameUserDir { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame");

    /// <summary>The servers' saved folder (<see cref="Processes.ServerConfig"/>).</summary>
    public string ServerSaved => Path.Combine(GameUserDir, "Saved_" + Processes.ServerConfig.SavedDirSuffix);
}
