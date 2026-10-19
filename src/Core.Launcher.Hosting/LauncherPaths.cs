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

    public string Logs => Path.Combine(Root, "Logs");

    public string CoreLogs => Path.Combine(Logs, "Core");
}
