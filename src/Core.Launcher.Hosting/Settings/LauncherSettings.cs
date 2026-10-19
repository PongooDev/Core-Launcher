using Core.Launcher.Hosting.Dlls;

namespace Core.Launcher.Hosting.Settings;

public sealed class LauncherSettings
{
    public string PlayerName { get; set; } = "";

    public string SelectedBuild { get; set; } = "";

    public List<BuildSettings> Builds { get; set; } = [];

    public DllSettings Dlls { get; set; } = new();

    public BackendSettings Backend { get; set; } = new();

    public ServerSettings Servers { get; set; } = new();

    public BuildSettings? FindSelectedBuild() =>
        Builds.FirstOrDefault(build => string.Equals(build.Name, SelectedBuild, StringComparison.OrdinalIgnoreCase)) ?? Builds.FirstOrDefault();
}

public sealed class BuildSettings
{
    public string Name { get; set; } = "";

    /// <summary>The build's root folder, the one holding Engine and FortniteGame.</summary>
    public string Path { get; set; } = "";

    public string Executable { get; set; } = @"FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe";

    public string ClientArguments { get; set; } = "";

    public string ServerArguments { get; set; } = "";

    /// <summary>Adds <c>-httpproxy=127.0.0.1:&lt;backend port&gt;</c>, which sends the engine's HTTP through Core-Backend without a redirect DLL.</summary>
    public bool UseHttpProxy { get; set; }

    public List<DllRole> ClientDlls { get; set; } = [];

    public List<DllRole> ServerDlls { get; set; } = [];

    public string ExecutablePath => System.IO.Path.Combine(Path, Executable);
}

public enum DllSource
{
    Release,
    Local,
}

public sealed class DllSettings
{
    public DllSource Source { get; set; } = DllSource.Release;

    /// <summary>GitHub repository whose latest release holds the DLLs, as owner/name.</summary>
    public string Repository { get; set; } = "PongooDev/CoreV2";

    /// <summary>Folder holding locally built DLLs, e.g. CoreV2's x64\Release.</summary>
    public string LocalFolder { get; set; } = "";
}

public sealed class BackendSettings
{
    /// <summary>False uses a Core-Backend that is already running on <see cref="Port"/>.</summary>
    public bool Start { get; set; } = true;

    /// <summary>The Core-Backend checkout, holding src\main.js and node_modules.</summary>
    public string Folder { get; set; } = "";

    public string Node { get; set; } = "node";

    /// <summary>0 lets the backend pick a free port; the launcher reads it from the backend's log.</summary>
    public int Port { get; set; } = 5595;

    /// <summary>Empty uses &lt;Folder&gt;\data, the folder npm start uses.</summary>
    public string DataFolder { get; set; } = "";

    public string ResolvedDataFolder => DataFolder.Length > 0 ? DataFolder : System.IO.Path.Combine(Folder, "data");
}

public sealed class ServerSettings
{
    public bool Enabled { get; set; } = true;

    public int Instances { get; set; } = 2;

    /// <summary>A server that hasn't logged "Server ready" by then is restarted; 0 waits forever.</summary>
    public int ReadyTimeoutSeconds { get; set; } = 180;

    /// <summary>Server login; {n} is the instance number, so each server has its own account.</summary>
    public string Login { get; set; } = "CoreServer{n}";

    public string LoginFor(int instance) => Login.Replace("{n}", instance.ToString(), StringComparison.Ordinal);
}
