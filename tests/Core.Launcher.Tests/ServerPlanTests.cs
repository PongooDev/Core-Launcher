using System.Text.Json;
using Core.Launcher.Hosting;
using Core.Launcher.Hosting.Builds;
using Core.Launcher.Hosting.Dlls;
using Core.Launcher.Hosting.Logging;
using Core.Launcher.Hosting.Servers;
using Core.Launcher.Hosting.Settings;
using Microsoft.Extensions.Time.Testing;

namespace Core.Launcher.Tests;

/// <summary>Servers for several builds at once (Core-Launcher #18): the plan, idle servers moving, and the pool's per-slot blockers.</summary>
public sealed class ServerPlanTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));

    private static BuildSettings Build(string name, int changelist, int extra = 0) =>
        new() { Name = name, Changelist = changelist, ServerDlls = [DllRole.Redirect, DllRole.Server], ExtraServers = extra };

    private static LauncherSettings Settings(params BuildSettings[] builds)
    {
        var settings = new LauncherSettings { SelectedBuild = builds[0].Name };
        settings.Builds.AddRange(builds);
        settings.Servers.Instances = 1;
        settings.Servers.BattleRoyaleInstances = 1;
        return settings;
    }

    private static ServerSlotView Slot(int instance, ServerState state = ServerState.Ready) =>
        new(instance, state, state == ServerState.Waiting ? null : 100 + instance, null, 0, [], "");

    [Fact]
    public void WithoutExtraServersThePlanIsTodaysServers()
    {
        var withBattleRoyale = Settings(Build("4.5", 4166199), Build("1.2", KnownBuilds.Fortnite12));
        Assert.Equal(KnownBuilds.ServerCount(withBattleRoyale.Servers, withBattleRoyale.FindSelectedBuild()), ServerPlan.For(withBattleRoyale).Count);
        Assert.Equal([new ServerAssignment("4.5", ServerMode.SaveTheWorld), new ServerAssignment("4.5", ServerMode.BattleRoyale)], ServerPlan.For(withBattleRoyale));

        var saveTheWorldOnly = Settings(Build("1.2", KnownBuilds.Fortnite12), Build("4.5", 4166199));
        Assert.Equal([new ServerAssignment("1.2", ServerMode.SaveTheWorld)], ServerPlan.For(saveTheWorldOnly));
        Assert.Equal(1, KnownBuilds.ServerCount(saveTheWorldOnly.Servers, saveTheWorldOnly.FindSelectedBuild()));
    }

    [Fact]
    public void ExtraServersComeAfterTheSelectedBuildsWhicheverIsSelected()
    {
        var settings = Settings(Build("1.2", KnownBuilds.Fortnite12, extra: 1), Build("4.5", 4166199, extra: 2), Build("3.0", 3901517));
        Assert.Equal(
            [new ServerAssignment("1.2", ServerMode.SaveTheWorld), new ServerAssignment("1.2", ServerMode.SaveTheWorld), new ServerAssignment("4.5", ServerMode.BattleRoyale), new ServerAssignment("4.5", ServerMode.BattleRoyale)],
            ServerPlan.For(settings));

        settings.SelectedBuild = "3.0";
        Assert.Equal(
            [new ServerAssignment("3.0", ServerMode.SaveTheWorld), new ServerAssignment("3.0", ServerMode.BattleRoyale), new ServerAssignment("1.2", ServerMode.SaveTheWorld), new ServerAssignment("4.5", ServerMode.BattleRoyale), new ServerAssignment("4.5", ServerMode.BattleRoyale)],
            ServerPlan.For(settings));
    }

    [Fact]
    public void ADemandIsTheFirstBuildOfItsChangelistThatCanHost()
    {
        var off = Build("4.5 (no Core.dll)", 4166199);
        off.ServerDlls = [DllRole.Redirect];
        var settings = Settings(Build("1.2", KnownBuilds.Fortnite12), off, Build("4.5", 4166199));

        Assert.Equal(new ServerAssignment("4.5", ServerMode.BattleRoyale), ServerPlan.Resolve(settings, new BuildDemand(4166199, true, 1)));
        Assert.Equal(new ServerAssignment("4.5", ServerMode.SaveTheWorld), ServerPlan.Resolve(settings, new BuildDemand(4166199, false, 1)));
        Assert.Equal(new ServerAssignment("1.2", ServerMode.SaveTheWorld), ServerPlan.Resolve(settings, new BuildDemand(KnownBuilds.Fortnite12, true, 1)));
        Assert.Null(ServerPlan.Resolve(settings, new BuildDemand(3901517, true, 1)));
    }

    [Fact]
    public void AnIdleServerMovesToAWaitingBuildAndBackWhenItsOwnPlayersWait()
    {
        var settings = Settings(Build("4.5", 4166199), Build("3.0", 3901517));
        var plan = ServerPlan.For(settings);
        var switcher = new ServerSwitcher(_time);
        IReadOnlyList<ServerSlotView> slots = [Slot(1), Slot(2)];
        var players = new Dictionary<int, SessionPlayers?> { [1] = new(0, 4), [2] = new(0, 100) };
        var waiting = new List<BuildDemand> { new(3901517, true, 1) };
        ServerSwitch? Decide() => switcher.Decide(plan, slots, slot => players[slot.Instance], waiting, demand => ServerPlan.Resolve(settings, demand));

        Assert.Null(Decide());
        _time.Advance(ServerSwitcher.IdleFor - TimeSpan.FromSeconds(1));
        Assert.Null(Decide());
        _time.Advance(TimeSpan.FromSeconds(1));

        // The last server, the selected build's Battle Royale one, goes; nothing else moves while it restarts on 3.0.
        Assert.Equal(new ServerSwitch(2, new ServerAssignment("4.5", ServerMode.BattleRoyale), new ServerAssignment("3.0", ServerMode.BattleRoyale)), Decide());
        Assert.Equal(new ServerAssignment("3.0", ServerMode.BattleRoyale), switcher.Assignment(plan, 2));
        slots = [Slot(1), Slot(2, ServerState.Stopping)];
        Assert.Null(Decide());

        // It stays on 3.0 while it's busy, then goes back once it's idle and 4.5's Battle Royale players wait.
        // (Server 1, an idle 4.5 Save the World one, would go to 4.5's Battle Royale players too: it is busy here.)
        slots = [Slot(1), Slot(2)];
        players[1] = new SessionPlayers(2, 4);
        players[2] = new SessionPlayers(3, 100);
        waiting = [new BuildDemand(4166199, true, 2)];
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(Decide());
        players[2] = new SessionPlayers(0, 100);
        Assert.Null(Decide());
        _time.Advance(ServerSwitcher.IdleFor);
        Assert.Equal(new ServerSwitch(2, new ServerAssignment("3.0", ServerMode.BattleRoyale), new ServerAssignment("4.5", ServerMode.BattleRoyale)), Decide());
        Assert.Equal(plan[1], switcher.Assignment(plan, 2));
    }

    [Fact]
    public void NoServerMovesWhenItsBuildHasAFreeOneOrTheIdleOnesAreWantedThemselves()
    {
        var settings = Settings(Build("4.5", 4166199, extra: 1), Build("3.0", 3901517, extra: 1));
        var plan = ServerPlan.For(settings);
        var switcher = new ServerSwitcher(_time);
        IReadOnlyList<ServerSlotView> slots = [Slot(1), Slot(2), Slot(3), Slot(4, ServerState.Starting)];
        var players = new Dictionary<int, SessionPlayers?> { [1] = new(0, 4), [2] = new(0, 100), [3] = new(0, 100), [4] = null };
        ServerSwitch? Decide(params BuildDemand[] waiting) =>
            switcher.Decide(plan, slots, slot => players[slot.Instance], waiting, demand => ServerPlan.Resolve(settings, demand));

        Decide();
        _time.Advance(ServerSwitcher.IdleFor);

        // 3.0's own extra server is booting: its players get that one.
        Assert.Null(Decide(new BuildDemand(3901517, true, 1)));
        // A build this PC doesn't have is left to other PCs.
        Assert.Null(Decide(new BuildDemand(3700114, true, 1)));
        // With both 4.5 modes and 3.0 waiting, 4.5's idle servers aren't taken for 3.0.
        slots = [Slot(1), Slot(2), Slot(3), Slot(4)];
        players[4] = new SessionPlayers(10, 100);
        players[2] = new SessionPlayers(5, 100);
        players[3] = new SessionPlayers(7, 100);
        Assert.Null(Decide(new BuildDemand(4166199, false, 1), new BuildDemand(4166199, true, 1), new BuildDemand(3901517, true, 1)));
        // A server whose players the backend doesn't count never counts as idle.
        players[1] = null;
        Assert.Null(Decide(new BuildDemand(3901517, true, 1)));
    }

    [Fact]
    public void CreativeServersComeAfterTheBattleRoyaleOnesOnABuildWithCreative()
    {
        // 7.00 (Core-Launcher #20): Save the World's, Battle Royale's, then Creative's, and extra servers are Battle Royale's.
        var settings = Settings(Build("7.00", KnownBuilds.Fortnite700, extra: 1), Build("6.31", KnownBuilds.Fortnite631));
        settings.Servers.CreativeInstances = 2;
        Assert.Equal(
            [new ServerAssignment("7.00", ServerMode.SaveTheWorld), new ServerAssignment("7.00", ServerMode.BattleRoyale),
             new ServerAssignment("7.00", ServerMode.Creative), new ServerAssignment("7.00", ServerMode.Creative), new ServerAssignment("7.00", ServerMode.BattleRoyale)],
            ServerPlan.For(settings));
        Assert.Equal(4, KnownBuilds.ServerCount(settings.Servers, settings.FindSelectedBuild()));

        // Builds before 7.00 have no Creative: their servers are as they were, whatever the count says.
        settings.SelectedBuild = "6.31";
        Assert.Equal(
            [new ServerAssignment("6.31", ServerMode.SaveTheWorld), new ServerAssignment("6.31", ServerMode.BattleRoyale), new ServerAssignment("7.00", ServerMode.BattleRoyale)],
            ServerPlan.For(settings));
        Assert.Equal(2, KnownBuilds.ServerCount(settings.Servers, settings.FindSelectedBuild()));
        var saveTheWorldOnly = Settings(Build("1.2", KnownBuilds.Fortnite12));
        saveTheWorldOnly.Servers.CreativeInstances = 3;
        Assert.Equal([new ServerAssignment("1.2", ServerMode.SaveTheWorld)], ServerPlan.For(saveTheWorldOnly));

        // A Creative server starts as a Battle Royale one and takes its build's Battle Royale players (Core-Backend's
        // matchmaking gives a free server either kind of match); Save the World's are another matter.
        var creative = new ServerAssignment("7.00", ServerMode.Creative);
        Assert.True(creative.BattleRoyale);
        Assert.True(creative.Same(new ServerAssignment("7.00", ServerMode.BattleRoyale)));
        Assert.False(creative.Same(new ServerAssignment("7.00", ServerMode.SaveTheWorld)));
        Assert.False(creative.Same(new ServerAssignment("6.31", ServerMode.BattleRoyale)));
        Assert.False(new ServerAssignment("7.00", ServerMode.SaveTheWorld).BattleRoyale);
        // The backend's demand names Battle Royale or Save the World, never Creative.
        Assert.Equal(new ServerAssignment("7.00", ServerMode.BattleRoyale), ServerPlan.Resolve(settings, new BuildDemand(KnownBuilds.Fortnite700, true, 1)));
    }

    [Fact]
    public void ACreativeServerTakesItsBuildsBattleRoyalePlayersAndGoesBackToCreative()
    {
        var settings = Settings(Build("7.00", KnownBuilds.Fortnite700), Build("6.31", KnownBuilds.Fortnite631));
        settings.Servers.Instances = 0;
        settings.Servers.BattleRoyaleInstances = 0;
        settings.Servers.CreativeInstances = 1;
        var plan = ServerPlan.For(settings);
        Assert.Equal([new ServerAssignment("7.00", ServerMode.Creative)], plan);
        var switcher = new ServerSwitcher(_time);
        IReadOnlyList<ServerSlotView> slots = [Slot(1, ServerState.Starting)];
        var players = new Dictionary<int, SessionPlayers?> { [1] = null };
        ServerSwitch? Decide(params BuildDemand[] waiting) =>
            switcher.Decide(plan, slots, slot => players[slot.Instance], waiting, demand => ServerPlan.Resolve(settings, demand));

        // Booting, it takes 7.00's Battle Royale players itself.
        Assert.Null(Decide(new BuildDemand(KnownBuilds.Fortnite700, true, 1)));

        // Idle, it moves to 6.31's waiting players, then back to Creative once 7.00's wait.
        slots = [Slot(1)];
        players[1] = new SessionPlayers(0, 16);
        Decide();
        _time.Advance(ServerSwitcher.IdleFor);
        Assert.Equal(new ServerSwitch(1, new ServerAssignment("7.00", ServerMode.Creative), new ServerAssignment("6.31", ServerMode.BattleRoyale)),
            Decide(new BuildDemand(KnownBuilds.Fortnite631, true, 1)));
        Assert.Equal(new ServerAssignment("6.31", ServerMode.BattleRoyale), switcher.Assignment(plan, 1));
        Decide();
        _time.Advance(ServerSwitcher.IdleFor);
        Assert.Equal(new ServerSwitch(1, new ServerAssignment("6.31", ServerMode.BattleRoyale), new ServerAssignment("7.00", ServerMode.Creative)),
            Decide(new BuildDemand(KnownBuilds.Fortnite700, true, 1)));
        Assert.Equal(new ServerAssignment("7.00", ServerMode.Creative), switcher.Assignment(plan, 1));
        Assert.False(switcher.HasOverrides);
    }

    [Fact]
    public async Task TheLauncherKeepsCreativeServersOnlyForABuildWithCreative()
    {
        // Not started, so no plan yet: each slot's mode comes from the settings, as for a slot past the plan.
        using var folder = new TempFolder();
        var host = new LauncherHost(new LauncherPaths(folder.Combine("Launcher")));
        try
        {
            var settings = SettingsStore.Clone(host.Settings);
            settings.Servers.Enabled = false;
            settings.Servers.Instances = 1;
            settings.Servers.BattleRoyaleInstances = 1;
            settings.Servers.CreativeInstances = 2;
            settings.Builds.Add(Build("7.00", KnownBuilds.Fortnite700));
            settings.Builds.Add(Build("6.31", KnownBuilds.Fortnite631));
            settings.SelectedBuild = "7.00";
            host.SaveSettings(settings);

            Assert.True(host.BuildHasCreative);
            Assert.Equal([ServerMode.SaveTheWorld, ServerMode.BattleRoyale, ServerMode.Creative, ServerMode.Creative], Enumerable.Range(1, 4).Select(host.ModeOf));
            Assert.Equal(["Save the World", "Battle Royale", "Creative"], new[] { ServerMode.SaveTheWorld, ServerMode.BattleRoyale, ServerMode.Creative }.Select(LauncherHost.ModeName));

            settings = SettingsStore.Clone(host.Settings);
            settings.SelectedBuild = "6.31";
            host.SaveSettings(settings);

            Assert.False(host.BuildHasCreative);
            Assert.Equal([ServerMode.SaveTheWorld, ServerMode.BattleRoyale, ServerMode.BattleRoyale, ServerMode.BattleRoyale], Enumerable.Range(1, 4).Select(host.ModeOf));
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public void ASlotWhoseBuildCantStartDoesntHoldBackTheNext()
    {
        var starter = new BlockingStarter { Blocked = { [1] = "4.5 has the Core DLLs turned off (Settings → Builds)" } };
        using var pool = new ServerPool(starter, new LogChannel("Launcher"), _time);
        pool.Configure(3, TimeSpan.FromSeconds(180));
        pool.SetEnabled(true);

        Assert.Equal([2], starter.Started);
        Assert.Equal("4.5 has the Core DLLs turned off (Settings → Builds)", pool.Slots[0].Note);
        Assert.Equal("waiting for its turn", pool.Slots[2].Note);
    }

    [Fact]
    public void TheBackendsDemandIsRead()
    {
        using var json = JsonDocument.Parse("""
            [{"build": "4166199", "mode": "battleRoyale", "players": 2}, {"build": "3532353", "mode": "saveTheWorld", "players": 1},
             {"build": "x", "players": 1}, {"build": 3901517, "players": 0}, 5]
            """);
        Assert.Equal([new BuildDemand(4166199, true, 2), new BuildDemand(KnownBuilds.Cert, false, 1)], BackendDemand.Parse(json.RootElement));
        using var notAList = JsonDocument.Parse("""{"error": "not found"}""");
        Assert.Empty(BackendDemand.Parse(notAList.RootElement));
    }

    [Fact]
    public void OlderSettingsLoadWithNoExtraServersAndSwitchingOff()
    {
        using var folder = new TempFolder();
        var store = new SettingsStore(folder.Combine("settings.json"));
        File.WriteAllText(store.FilePath, $$$"""{"Version": {{{SettingsStore.CurrentVersion}}}, "Builds": [{"Name": "4.5", "Changelist": 4166199}], "Servers": {"Instances": 1}}""");
        var settings = store.Load();
        Assert.Equal(0, settings.Builds.Single().ExtraServers);
        Assert.False(settings.Servers.SwitchIdleServers);
        // Settings from before Creative (Core-Launcher #20) keep one Creative server, used only on a build with Creative.
        Assert.Equal(1, settings.Servers.CreativeInstances);

        settings.Builds.Single().ExtraServers = 99;
        settings.Servers.SwitchIdleServers = true;
        settings.Servers.CreativeInstances = 99;
        store.Save(settings);
        var loaded = store.Load();
        Assert.Equal(SettingsStore.MaxInstances, loaded.Builds.Single().ExtraServers);
        Assert.True(loaded.Servers.SwitchIdleServers);
        Assert.Equal(SettingsStore.MaxInstances, loaded.Servers.CreativeInstances);
        loaded.Servers.CreativeInstances = -1;
        store.Save(loaded);
        Assert.Equal(0, store.Load().Servers.CreativeInstances);
    }

    private sealed class BlockingStarter : IServerStarter
    {
        public Dictionary<int, string> Blocked { get; } = [];

        public List<int> Started { get; } = [];

        public string? Blocker() => null;

        public string? Blocker(int instance) => Blocked.GetValueOrDefault(instance);

        public IServerInstance Start(int instance)
        {
            Started.Add(instance);
            return new IdleServer(instance);
        }

        public SessionCheck Session(int instance, DateTimeOffset startedAt, IReadOnlyList<int> ports) => SessionCheck.Pending;
    }

    private sealed class IdleServer(int id) : IServerInstance
    {
        public int Id => id;

        public Task<uint> Exited { get; } = new TaskCompletionSource<uint>().Task;

        public Task Ready { get; } = new TaskCompletionSource().Task;

        public Task Finished { get; } = new TaskCompletionSource().Task;

        public void Terminate()
        {
        }

        public IReadOnlyList<int> UdpPorts() => [];

        public void Dispose()
        {
        }
    }
}
