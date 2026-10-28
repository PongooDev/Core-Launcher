namespace Core.Launcher.Hosting.Processes;

/// <summary>
/// The servers' own saved folder and the engine settings in it. Servers run with -saveddirsuffix=CoreServer
/// (<see cref="LaunchArguments.ServerSwitches"/>), so the engine keeps their config, logs and crash dumps in
/// Saved_CoreServer next to the player's Saved folder (FPaths::GameSavedDir, 1.8 0x13058C0) and never reads or writes the
/// player's. CoreV2's server lets the switch through the 1.7.2 and 1.8 exes' command line whitelist; 1.1 and 1.2 have none.
/// </summary>
public static class ServerConfig
{
    public const string SavedDirSuffix = "CoreServer";

    /// <summary>
    /// Turns the engine's XMPP off ([XMPP] bEnabled, which FXmppModule::StartupModule reads: 1.8 0x2CA5E40, 1.7.2
    /// 0x2C28BF0, 1.2 0x142A74FD0), so the online subsystem gets the engine's FXmppNull connection. A server logs in at
    /// boot, before the backend's hotfix points XMPP at the backend, so its connection went to Epic's XMPP host and failed,
    /// and the connection's thread (FXmppConnectionJingle::Run) then spun a core for the rest of the server's life
    /// (CoreV2 #109). Servers need no XMPP: parties and chat are the clients'.
    /// </summary>
    public const string EngineIni = "[XMPP]\r\nbEnabled=False\r\n";

    /// <summary>The saved folder's Engine.ini, which the engine reads over the build's own config at start-up.</summary>
    public static string EngineIniPath(string savedFolder) => Path.Combine(savedFolder, "Config", "WindowsClient", "Engine.ini");

    /// <summary>
    /// Writes the servers' Engine.ini unless it already holds <see cref="EngineIni"/>; returns whether it wrote it.
    /// Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when it can't.
    /// </summary>
    public static bool WriteEngineIni(string savedFolder)
    {
        var path = EngineIniPath(savedFolder);
        if (File.Exists(path) && File.ReadAllText(path) == EngineIni)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, EngineIni);
        return true;
    }
}
