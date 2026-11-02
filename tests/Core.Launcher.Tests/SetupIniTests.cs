using Core.Server.Cli.Config;
using Core.Server.Cli.Metrics;

namespace Core.Launcher.Tests;

public sealed class SetupIniTests
{
    [Fact]
    public void ParsesSectionsAndKeyValuePairs()
    {
        var text = """
            ; Comment line
            # Another comment
            
            [Server]
            BuildPath=D:\Fortnite
            BackendUrl=http://192.168.1.100:5595
            Region=EU
            StandardServers=3
            BattleRoyaleServers=1
            AllowUnsupportedBuilds=true
            
            [Paths]
            DllsFolder=C:\CoreDlls
            DataFolder=C:\Data
            """;

        var ini = SetupIni.Parse(text);

        Assert.Equal(@"D:\Fortnite", ini.Get("Server", "BuildPath"));
        Assert.Equal("http://192.168.1.100:5595", ini.Get("Server", "BackendUrl"));
        Assert.Equal("EU", ini.Get("Server", "Region"));
        Assert.Equal("3", ini.Get("Server", "StandardServers"));
        Assert.Equal("1", ini.Get("Server", "BattleRoyaleServers"));
        Assert.Equal("true", ini.Get("Server", "AllowUnsupportedBuilds"));
        Assert.Equal(@"C:\CoreDlls", ini.Get("Paths", "DllsFolder"));
        Assert.Equal(@"C:\Data", ini.Get("Paths", "DataFolder"));
    }

    [Fact]
    public void PopulatesDefaults()
    {
        var ini = new SetupIni();
        ini.PopulateDefaults();

        Assert.Equal("", ini.Get("Server", "BuildPath"));
        Assert.Equal("http://127.0.0.1:5595", ini.Get("Server", "BackendUrl"));
        Assert.Equal("NA", ini.Get("Server", "Region"));
        Assert.Equal("2", ini.Get("Server", "StandardServers"));
        Assert.Equal("2", ini.Get("Server", "BattleRoyaleServers"));
        Assert.Equal("CoreServer{n}", ini.Get("Server", "ServerLogin"));
        Assert.Equal("180", ini.Get("Server", "ReadyTimeout"));
        Assert.Equal("false", ini.Get("Server", "AllowUnsupportedBuilds"));
    }

    [Fact]
    public void ConfigMergesIniAndCommandLineArguments()
    {
        var ini = new SetupIni();
        ini.PopulateDefaults();
        ini.Set("Server", "BuildPath", @"D:\Original");
        ini.Set("Server", "Region", "NA");
        ini.Set("Server", "ServerLogin", "CustomServer{n}");

        string[] args = ["--build", @"D:\Overridden", "--region", "OCE", "--servers", "4", "--br-servers", "0", "--login", "MyServer_{n}", "--allow-unsupported"];
        var config = ServerHostConfig.FromIniAndArgs(ini, args);

        Assert.Equal(@"D:\Overridden", config.BuildPath);
        Assert.Equal("OCE", config.Region);
        Assert.Equal(4, config.StandardServers);
        Assert.Equal(0, config.BattleRoyaleServers);
        Assert.Equal("MyServer_{n}", config.ServerLogin);
        Assert.True(config.AllowUnsupportedBuilds);
    }

    [Fact]
    public void ConfigParsesEndpointsCorrectly()
    {
        var ini = new SetupIni();
        ini.Set("Server", "BackendUrl", "192.168.1.50:5600");

        var config = ServerHostConfig.FromIniAndArgs(ini, []);
        var endpoint = config.ParseEndpoint();

        Assert.Equal("192.168.1.50", endpoint.Host);
        Assert.Equal(5600, endpoint.Port);
        Assert.Equal("http://192.168.1.50:5600", endpoint.Url);

        // A backend behind an https reverse proxy (Core-Launcher #9): 443 unless a port is given.
        ini.Set("Server", "BackendUrl", "https://core.example.com");
        endpoint = ServerHostConfig.FromIniAndArgs(ini, []).ParseEndpoint();
        Assert.Equal(("core.example.com", 443, true, "https://core.example.com"), (endpoint.Host, endpoint.Port, endpoint.Secure, endpoint.Url));

        ini.Set("Server", "BackendUrl", "https://core.example.com:8443/");
        endpoint = ServerHostConfig.FromIniAndArgs(ini, []).ParseEndpoint();
        Assert.Equal("https://core.example.com:8443", endpoint.Url);
    }

    [Fact]
    public void SystemSnapshotCalculatesRamPercentage()
    {
        var snapshot = new SystemSnapshot(5.5, 8L * 1024 * 1024 * 1024, 16L * 1024 * 1024 * 1024);
        Assert.Equal(50.0, snapshot.RamPercent);
        Assert.Equal(5.5, snapshot.CpuPercent);
    }

    [Fact]
    public void TableMeasuresVisibleLengthWithoutAnsiCodes()
    {
        var colored = "\u001b[38;2;86;182;194m▶ Starting\u001b[0m";
        Assert.Equal(10, Core.Server.Cli.Interface.Table.VisibleLength(colored));
        Assert.Equal(9, Core.Server.Cli.Interface.Table.VisibleLength("\u001b[2m○ Waiting\u001b[0m"));
        Assert.Equal(1, Core.Server.Cli.Interface.Table.VisibleLength("\u001b[2m-\u001b[0m"));
    }

    [Fact]
    public void DashboardRendererBuildsAlignedTableFrame()
    {
        var build = new Core.Launcher.Hosting.Settings.BuildSettings
        {
            Name = "Fortnite 1.8.2",
            Changelist = 3741772,
            Path = @"D:\Fortnite",
            Executable = @"FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe"
        };

        var slots = new List<Core.Launcher.Hosting.Servers.ServerSlotView>
        {
            new(1, Core.Launcher.Hosting.Servers.ServerState.Starting, 7496, DateTimeOffset.UtcNow.AddSeconds(-7), 0, [7777], "Starting"),
            new(2, Core.Launcher.Hosting.Servers.ServerState.Waiting, null, null, 0, [], "Waiting"),
            new(3, Core.Launcher.Hosting.Servers.ServerState.Waiting, null, null, 0, [], "Waiting"),
            new(4, Core.Launcher.Hosting.Servers.ServerState.Waiting, null, null, 0, [], "Waiting"),
        };

        var procSnaps = new Dictionary<int, Core.Server.Cli.Metrics.ProcessSnapshot>
        {
            [7496] = new(7496, 611L * 1024 * 1024, 9.6)
        };

        var status = new Core.Server.Cli.Hosting.ServerHostStatus(
            build,
            new Core.Launcher.Hosting.Backend.BackendEndpoint("127.0.0.1", 5595),
            "OCE",
            "CoreServer1",
            4,
            slots,
            new Core.Server.Cli.Metrics.SystemSnapshot(19.5, 18L * 1024 * 1024 * 1024, 32L * 1024 * 1024 * 1024),
            new Core.Server.Cli.Metrics.TotalProcessSnapshot(611L * 1024 * 1024, 9.6, procSnaps),
            ["[*] Server #1 started (PID 7496)"]);

        var lines = Core.Server.Cli.Terminal.DashboardRenderer.BuildFrame(status);
        Assert.NotEmpty(lines);

        // Find table header and rows
        var headerIndex = lines.FindIndex(l => l.Contains("Slot") && l.Contains("Status"));
        Assert.True(headerIndex >= 0);

        var header = lines[headerIndex];
        var row1 = lines[headerIndex + 2];
        var row2 = lines[headerIndex + 3];

        var hVis = Core.Server.Cli.Interface.Table.VisibleLength(header);
        var r1Vis = Core.Server.Cli.Interface.Table.VisibleLength(row1);
        var r2Vis = Core.Server.Cli.Interface.Table.VisibleLength(row2);

        Assert.Equal(hVis, r1Vis);
        Assert.Equal(hVis, r2Vis);

        // Verify consistent vertical line prefix '│' across sections
        Assert.Contains("│", header);
        Assert.Contains("│", row1);
        Assert.Contains("│", row2);

        var activityIndex = lines.FindIndex(l => l.Contains("Server #1 started"));
        Assert.True(activityIndex >= 0);
        Assert.Contains("│", lines[activityIndex]);
    }

    [Fact]
    public void ServerLoginRespectsCustomPattern()
    {
        var defaultConfig = new ServerHostConfig { ServerLogin = "CoreServer{n}" };
        var customConfig = new ServerHostConfig { ServerLogin = "GameNode_{n}" };

        var starterDefault = new Core.Server.Cli.Hosting.GameServerStarter(
            defaultConfig,
            new Core.Launcher.Hosting.Settings.BuildSettings(),
            new Core.Launcher.Hosting.Backend.BackendEndpoint("127.0.0.1", 5595),
            null!,
            Core.Launcher.Hosting.LauncherPaths.Default,
            null!,
            new Core.Launcher.Hosting.Logging.LogChannel("test"));

        var starterCustom = new Core.Server.Cli.Hosting.GameServerStarter(
            customConfig,
            new Core.Launcher.Hosting.Settings.BuildSettings(),
            new Core.Launcher.Hosting.Backend.BackendEndpoint("127.0.0.1", 5595),
            null!,
            Core.Launcher.Hosting.LauncherPaths.Default,
            null!,
            new Core.Launcher.Hosting.Logging.LogChannel("test"));

        Assert.Equal("CoreServer1", starterDefault.ServerLogin(1));
        Assert.Equal("CoreServer2", starterDefault.ServerLogin(2));
        Assert.Equal("GameNode_1", starterCustom.ServerLogin(1));
        Assert.Equal("GameNode_2", starterCustom.ServerLogin(2));
    }

    [Fact]
    public void MigratesLegacySetupIniToSettingsIni()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "CoreServerHostTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var legacyPath = Path.Combine(tempDir, "setup.ini");
            File.WriteAllText(legacyPath, "[Server]\nRegion=EU\n");

            var resolved = SettingsIni.ResolveIniPath(tempDir);
            Assert.Equal(Path.Combine(tempDir, "settings.ini"), resolved);
            Assert.True(File.Exists(resolved));

            var loaded = SettingsIni.Load(resolved);
            Assert.Equal("EU", loaded.Get("Server", "Region"));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void SyncToIniUpdatesAllValues()
    {
        var ini = new SettingsIni();
        var config = new ServerHostConfig
        {
            BuildPath = @"D:\Fortnite",
            BackendUrl = "http://192.168.1.50:5595",
            Region = "OCE",
            StandardServers = 3,
            BattleRoyaleServers = 1,
            ServerLogin = "Custom_{n}",
            PcName = "PC1",
            ReadyTimeoutSeconds = 120,
            AllowUnsupportedBuilds = true,
            DllsFolder = @"C:\Dlls",
            DataFolder = @"C:\Data"
        };

        config.SyncToIni(ini);

        Assert.Equal(@"D:\Fortnite", ini.Get("Server", "BuildPath"));
        Assert.Equal("http://192.168.1.50:5595", ini.Get("Server", "BackendUrl"));
        Assert.Equal("OCE", ini.Get("Server", "Region"));
        Assert.Equal("3", ini.Get("Server", "StandardServers"));
        Assert.Equal("1", ini.Get("Server", "BattleRoyaleServers"));
        Assert.Equal("Custom_{n}", ini.Get("Server", "ServerLogin"));
        Assert.Equal("PC1", ini.Get("Server", "PcName"));
        Assert.Equal("120", ini.Get("Server", "ReadyTimeout"));
        Assert.Equal("true", ini.Get("Server", "AllowUnsupportedBuilds"));
        Assert.Equal(@"C:\Dlls", ini.Get("Paths", "DllsFolder"));
        Assert.Equal(@"C:\Data", ini.Get("Paths", "DataFolder"));
    }

    [Fact]
    public void ServerPoolCanRemoveSpecificSlotAndRenumber()
    {
        var starter = new FakeStarter();
        var log = new Core.Launcher.Hosting.Logging.LogChannel("Test");
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();

        using var pool = new Core.Launcher.Hosting.Servers.ServerPool(starter, log, time);
        pool.Configure(3, TimeSpan.FromSeconds(180));
        pool.SetEnabled(true);

        Assert.Equal(3, pool.Slots.Count);
        Assert.Equal([1, 2, 3], pool.Slots.Select(s => s.Instance));

        var removed = pool.RemoveSlot(2);
        Assert.True(removed);
        Assert.Equal(2, pool.Slots.Count);
        Assert.Equal([1, 2], pool.Slots.Select(s => s.Instance));
        Assert.Equal(2, pool.Instances);
    }

    [Fact]
    public void DashboardRendererRendersPromptDialog()
    {
        var build = new Core.Launcher.Hosting.Settings.BuildSettings
        {
            Name = "Fortnite 1.8.2",
            Changelist = 3741772,
            Path = @"D:\Fortnite",
            Executable = @"FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe"
        };

        var status = new Core.Server.Cli.Hosting.ServerHostStatus(
            build,
            new Core.Launcher.Hosting.Backend.BackendEndpoint("127.0.0.1", 5595),
            "OCE",
            "CoreServer1",
            2,
            [],
            new Core.Server.Cli.Metrics.SystemSnapshot(10, 8L * 1024 * 1024 * 1024, 16L * 1024 * 1024 * 1024),
            new Core.Server.Cli.Metrics.TotalProcessSnapshot(0, 0, new Dictionary<int, Core.Server.Cli.Metrics.ProcessSnapshot>()),
            []);

        var dialog = new Core.Server.Cli.Terminal.PromptDialog(
            "Create Server",
            "Select server type to launch:",
            [
                new("1", "Save The World", "Campaign mode", true),
                new("2", "Battle Royale", "PvP matchmaking mode", true)
            ],
            0);

        var lines = Core.Server.Cli.Terminal.DashboardRenderer.BuildFrame(status, dialog);
        Assert.NotEmpty(lines);

        var titleLine = lines.FirstOrDefault(l => l.Contains("Create Server"));
        Assert.NotNull(titleLine);

        var opt1 = lines.FirstOrDefault(l => l.Contains("Save The World"));
        var opt2 = lines.FirstOrDefault(l => l.Contains("Battle Royale"));
        Assert.NotNull(opt1);
        Assert.NotNull(opt2);

        // Options must follow the unified left border style
        Assert.Contains("│", opt1);
        Assert.Contains("│", opt2);
    }

    [Fact]
    public void GameServerStarterTracksExplicitSlotTypes()
    {
        var config = new ServerHostConfig { StandardServers = 1, BattleRoyaleServers = 1 };
        var build = new Core.Launcher.Hosting.Settings.BuildSettings { Changelist = 3801085 }; // BR-capable build
        var starter = new Core.Server.Cli.Hosting.GameServerStarter(
            config,
            build,
            new Core.Launcher.Hosting.Backend.BackendEndpoint("127.0.0.1", 5595),
            null!,
            config.ResolvePaths(),
            null!,
            new Core.Launcher.Hosting.Logging.LogChannel("Test"));

        starter.SetSlotType(1, false); // STW
        starter.SetSlotType(2, true);  // BR

        Assert.False(starter.HostsBattleRoyale(1));
        Assert.True(starter.HostsBattleRoyale(2));

        // Removing slot 1 shifts slot 2 down to index 1
        starter.RemoveSlotType(1);
        Assert.True(starter.HostsBattleRoyale(1));
    }

    private sealed class FakeStarter : Core.Launcher.Hosting.Servers.IServerStarter
    {
        public string? Blocker() => null;
        public Core.Launcher.Hosting.Servers.IServerInstance Start(int instance) => null!;
        public Core.Launcher.Hosting.Servers.SessionCheck Session(int instance, DateTimeOffset startedAt, IReadOnlyList<int> ports) =>
            Core.Launcher.Hosting.Servers.SessionCheck.Pending;
    }
}

