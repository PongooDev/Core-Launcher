using Core.Launcher.Hosting;
using Core.Launcher.Hosting.Processes;

namespace Core.Launcher.Tests;

public sealed class ServerConfigTests
{
    [Fact]
    public void Servers_get_their_own_saved_folder_next_to_the_players()
    {
        var paths = new LauncherPaths(@"D:\Launcher") { GameUserDir = @"C:\Users\Player\AppData\Local\FortniteGame" };

        // FPaths::GameSavedDir: "Saved_" and the suffix.
        Assert.Equal(@"C:\Users\Player\AppData\Local\FortniteGame\Saved_CoreServer", paths.ServerSaved);
        Assert.Equal(@"C:\Users\Player\AppData\Local\FortniteGame\Saved_CoreServer\Config\WindowsClient\Engine.ini", ServerConfig.EngineIniPath(paths.ServerSaved));
        Assert.EndsWith(@"\FortniteGame", LauncherPaths.Default.GameUserDir);
    }

    [Fact]
    public void Writes_the_engine_ini_that_turns_xmpp_off_once()
    {
        using var folder = new TempFolder();
        var saved = folder.Combine("Saved_CoreServer");
        var ini = ServerConfig.EngineIniPath(saved);

        Assert.True(ServerConfig.WriteEngineIni(saved));
        Assert.Equal("[XMPP]\r\nbEnabled=False\r\n", File.ReadAllText(ini));
        Assert.False(ServerConfig.WriteEngineIni(saved));

        // Whatever a server saved there is replaced.
        File.WriteAllText(ini, "[XMPP]\r\nbEnabled=True\r\n");
        Assert.True(ServerConfig.WriteEngineIni(saved));
        Assert.Equal(ServerConfig.EngineIni, File.ReadAllText(ini));
    }
}
