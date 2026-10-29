using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Core.Launcher.Hosting;
using Core.Launcher.Hosting.Builds;
using Core.Launcher.Hosting.Dlls;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Tests;

public sealed class BuildSupportTests
{
    private static readonly BuildVersion Fortnite11 = new(4, 16, 0, KnownBuilds.Cert, "++Fortnite+Release-Cert");
    private static readonly BuildVersion Fortnite12 = new(4, 16, 0, KnownBuilds.Fortnite12, "++Fortnite+Release-Cert");
    private static readonly BuildVersion Unsupported = new(4, 19, 0, 3790078, "++Fortnite+Release-1.10");

    private const string ClientArguments = "-epicapp=Fortnite -epicenv=Prod -epicportal -SkipPatchCheck";
    private const string ServerArguments = ClientArguments + " -nullrhi -nosplash -nosound";

    private static string Json(BuildSettings build) => JsonSerializer.Serialize(build);

    [Fact]
    public void EachKnownBuildHasItsSupportLevel()
    {
        Assert.Equal(BuildSupport.Full, KnownBuilds.SupportOf(KnownBuilds.Cert));
        Assert.Equal(BuildSupport.Full, KnownBuilds.SupportOf(3541083));
        Assert.Equal(BuildSupport.Unsupported, KnownBuilds.SupportOf(3790078));
        Assert.Equal(BuildSupport.Unsupported, KnownBuilds.SupportOf(0));
        Assert.True(KnownBuilds.IsKnown(3541083));
        Assert.Equal("Fortnite 1.1 (Cert CL-3532353)", KnownBuilds.Create(@"F:\Builds\1.1", Fortnite11, allowUnsupported: false).Name);
        Assert.Equal("Fortnite 1.2 (CL-3541083)", KnownBuilds.Create(@"F:\Builds\1.2", Fortnite12, allowUnsupported: false).Name);
    }

    [Fact]
    public void Fortnite12GetsTheSameLaunchSettingsAs11()
    {
        var cert = KnownBuilds.Create(@"F:\Builds\1.1", Fortnite11, allowUnsupported: false);
        var build = KnownBuilds.Create(@"F:\Builds\1.2.0\Fortnite\Fortnite", Fortnite12, allowUnsupported: false);

        Assert.Equal(3541083, build.Changelist);
        Assert.Equal(@"F:\Builds\1.2.0\Fortnite\Fortnite", build.Path);
        Assert.Equal(@"F:\Builds\1.2.0\Fortnite\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", build.ExecutablePath);
        Assert.Equal(cert.Executable, build.Executable);
        Assert.Equal(ClientArguments, build.ClientArguments);
        Assert.Equal(ServerArguments + " -McpRegion=NA", build.ServerArguments);
        Assert.Equal(cert.ClientArguments, build.ClientArguments);
        Assert.Equal(cert.ServerArguments, build.ServerArguments);
        Assert.True(build.UseHttpProxy);
        Assert.Equal([DllRole.Redirect, DllRole.Client], build.ClientDlls);
        Assert.Equal([DllRole.Redirect, DllRole.Server], build.ServerDlls);
        Assert.False(build.CoreDefaultsAdded);
        Assert.Equal(Json(build), Json(KnownBuilds.Create(@"F:\Builds\1.2.0\Fortnite\Fortnite", Fortnite12, allowUnsupported: true)));
    }

    [Fact]
    public void Fortnite12PlaysAndHostsWithTheUnsupportedSwitchOff()
    {
        var build = KnownBuilds.Create(@"F:\Builds\1.2", Fortnite12, allowUnsupported: false);
        var before = Json(build);
        var settings = new LauncherSettings { Builds = [build] };

        Assert.False(settings.AllowUnsupportedBuilds);
        Assert.True(KnownBuilds.CanPlay(build));
        Assert.True(KnownBuilds.CanHost(build));
        Assert.Empty(KnownBuilds.SetAllowUnsupported(settings, true));
        Assert.Empty(KnownBuilds.SetAllowUnsupported(settings, false));
        Assert.Equal(before, Json(build));

        build.ServerDlls = [DllRole.Redirect];
        Assert.False(KnownBuilds.CanHost(build));
        Assert.Equal("Fortnite 1.2 (CL-3541083) has the Core DLLs turned off (Settings → Builds)", KnownBuilds.NotSupported(build, allowUnsupported: false));
    }

    [Fact]
    public void MessagesGiveTheSupportedBuildsAsOneRangeOfVersions()
    {
        Assert.Equal("Fortnite 1.1 to 1.9", KnownBuilds.SupportedNames);
        Assert.Equal("Fortnite 1.9.1", KnownBuilds.InProgressNames);
        Assert.Equal("Fortnite 1.1 to 1.9, and support for Fortnite 1.9.1 is in progress", KnownBuilds.SupportSummary);

        var other = KnownBuilds.Create(@"F:\Builds\Other", Unsupported, allowUnsupported: false);
        Assert.Equal(
            "Core doesn't support Fortnite 1.10 CL-3790078 yet. It supports Fortnite 1.1 to 1.9, and support for Fortnite 1.9.1 is in progress. Turn on Allow unsupported builds in Settings → Builds to try it.",
            KnownBuilds.NotSupported(other, allowUnsupported: false));
        Assert.Equal(
            "Fortnite 1.10 CL-3790078 isn't supported and may not work: Core is only tested on Fortnite 1.1 to 1.9, and support for Fortnite 1.9.1 is in progress.",
            KnownBuilds.UnsupportedWarning(other.Name));
    }

    [Fact]
    public void OnlyBuildsCoreDoesNotFullySupportGetALogWarning()
    {
        var cert = KnownBuilds.Create(@"F:\Builds\1.1", Fortnite11, allowUnsupported: false);
        var fortnite12 = KnownBuilds.Create(@"F:\Builds\1.2", Fortnite12, allowUnsupported: false);
        fortnite12.Name = "1.2";
        var other = KnownBuilds.Create(@"F:\Builds\Other", Unsupported, allowUnsupported: true);

        Assert.Null(KnownBuilds.SupportWarning(cert));
        Assert.Null(KnownBuilds.SupportWarning(fortnite12));
        Assert.Equal(KnownBuilds.UnsupportedWarning("Fortnite 1.10 CL-3790078"), KnownBuilds.SupportWarning(other));
        Assert.Equal("support in progress, some things may not work yet", KnownBuilds.InProgressNote);
    }

    [Fact]
    public async Task A12BuildPlaysWithTheSwitchOffAndItsLogHasNoSupportNote()
    {
        using var folder = new TempFolder();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var host = new LauncherHost(new LauncherPaths(folder.Combine("Launcher")));
        try
        {
            var settings = SettingsStore.Clone(host.Settings);
            settings.PlayerName = "Tester";
            settings.Servers.Enabled = false;
            settings.Backend.Source = BackendSource.External;
            settings.Backend.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            // cmd.exe stands in for the game; "&rem" swallows the switches the launcher appends.
            settings.Builds.Add(new BuildSettings
            {
                Name = "1.2",
                Path = Environment.SystemDirectory,
                Executable = "cmd.exe",
                Changelist = KnownBuilds.Fortnite12,
                ClientArguments = "/c ping -n 60 127.0.0.1 >nul &rem",
                UseHttpProxy = true,
            });
            host.SaveSettings(settings);
            Assert.False(host.Settings.AllowUnsupportedBuilds);

            await host.PlayAsync();

            var log = host.ClientLog("Tester").Snapshot();
            Assert.StartsWith("(launcher) --- ", log[0]);
            Assert.DoesNotContain(log, line => line.Contains("Core's support for", StringComparison.Ordinal));
            Assert.DoesNotContain(log, line => line.Contains("isn't supported", StringComparison.Ordinal));

            var client = host.ClientFor("Tester")!;
            host.StopClient("Tester");
            await client.Game.Finished.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            await host.DisposeAsync();
            listener.Stop();
        }
    }

    [Fact]
    public void A12BuildAddedWithTheUnsupportedSwitchBecomesTheKnownBuild()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("settings.json");
        File.WriteAllText(path, $$"""
            {
              "Version": 2,
              "SelectedBuild": "fortnite cert CL-3541083",
              "AllowUnsupportedBuilds": true,
              "Builds": [
                { "Name": "Cert CL-3532353", "Path": "F:\\Builds\\1.1", "Changelist": 3532353, "UseHttpProxy": true,
                  "ClientArguments": "{{ClientArguments}}", "ServerArguments": "{{ServerArguments}} -McpRegion=NA",
                  "ClientDlls": [ "Redirect", "Client" ], "ServerDlls": [ "Redirect", "Server" ] },
                { "Name": "Fortnite Cert CL-3541083", "Path": "F:\\Builds\\1.2.0\\Fortnite\\Fortnite", "Changelist": 3541083, "UseHttpProxy": true,
                  "ClientArguments": "{{ClientArguments}} -log", "ServerArguments": "{{ServerArguments}} -McpRegion=NA",
                  "ClientDlls": [ "Redirect", "Client" ], "ServerDlls": [ "Redirect", "Server" ], "CoreDefaultsAdded": true },
                { "Name": "Fortnite 1.10 CL-3790078", "Path": "F:\\Builds\\1.8", "Changelist": 3790078, "UseHttpProxy": true,
                  "ClientArguments": "{{ClientArguments}}", "ServerArguments": "{{ServerArguments}} -McpRegion=NA",
                  "ClientDlls": [ "Redirect", "Client" ], "ServerDlls": [ "Redirect", "Server" ], "CoreDefaultsAdded": true }
              ]
            }
            """);
        var store = new SettingsStore(path);

        var settings = store.Load();
        var build = settings.Builds[1];

        Assert.Equal(3, JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("Version").GetInt32());
        Assert.Equal("Fortnite 1.1 (Cert CL-3532353)", settings.Builds[0].Name);
        Assert.Equal("Fortnite 1.2 (CL-3541083)", build.Name);
        Assert.Equal("Fortnite 1.2 (CL-3541083)", settings.SelectedBuild);
        Assert.Same(build, settings.FindSelectedBuild());
        Assert.Equal(@"F:\Builds\1.2.0\Fortnite\Fortnite", build.Path);
        Assert.Equal(ClientArguments + " -log", build.ClientArguments);
        Assert.Equal(ServerArguments + " -McpRegion=NA", build.ServerArguments);
        Assert.False(build.CoreDefaultsAdded);
        Assert.True(settings.Builds[2].CoreDefaultsAdded);
        Assert.Equal("Fortnite 1.10 CL-3790078", settings.Builds[2].Name);

        var before = Json(build);
        Assert.Equal([settings.Builds[2]], KnownBuilds.SetAllowUnsupported(settings, false));
        Assert.Equal(before, Json(build));
        Assert.True(KnownBuilds.CanPlay(build));
        Assert.True(KnownBuilds.CanHost(build));
        Assert.False(KnownBuilds.CanPlay(settings.Builds[2]));

        // Migrated once: a name given by hand afterwards stays, even one that looks made up.
        build.Name = "Fortnite Cert CL-3541083";
        settings.SelectedBuild = build.Name;
        store.Save(settings);
        var reloaded = store.Load();
        Assert.Equal("Fortnite Cert CL-3541083", reloaded.Builds[1].Name);
        Assert.False(reloaded.AllowUnsupportedBuilds);
        Assert.True(KnownBuilds.CanHost(reloaded.Builds[1]));
    }

    [Fact]
    public void Earlier12BuildsKeepTheirOwnNamesAndOptions()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("settings.json");
        File.WriteAllText(path, $$"""
            {
              "Version": 2,
              "SelectedBuild": "1.2",
              "Builds": [
                { "Name": "1.1", "Path": "F:\\Builds\\1.1", "Changelist": 3532353, "UseHttpProxy": true,
                  "ClientArguments": "{{ClientArguments}}", "ServerArguments": "{{ServerArguments}} -McpRegion=NA",
                  "ClientDlls": [ "Redirect", "Client" ], "ServerDlls": [ "Redirect", "Server" ] },
                { "Name": "1.2", "Path": "F:\\Builds\\1.2.0\\Fortnite\\Fortnite", "Changelist": 3541083, "UseHttpProxy": true,
                  "ClientArguments": "{{ClientArguments}}", "ServerArguments": "{{ServerArguments}}",
                  "ClientDlls": [ "Redirect", "Client" ], "ServerDlls": [ "Redirect", "Server" ] },
                { "Name": "Fortnite Cert CL-3541083", "Path": "F:\\Builds\\Off", "Changelist": 3541083,
                  "ClientArguments": "{{ClientArguments}}", "ServerArguments": "{{ServerArguments}} -mcpregion=EU" },
                { "Name": "Fortnite Cert CL-3541083 (2)", "Path": "F:\\Builds\\Copy", "Changelist": 3541083,
                  "ClientArguments": "{{ClientArguments}}", "ServerArguments": "{{ServerArguments}}" }
              ]
            }
            """);

        var settings = new SettingsStore(path).Load();
        var (handNamed, addedOff, copy) = (settings.Builds[1], settings.Builds[2], settings.Builds[3]);

        Assert.Equal(["1.1", "1.2", "Fortnite 1.2 (CL-3541083)", "Fortnite 1.2 (CL-3541083) (2)"], settings.Builds.Select(build => build.Name));
        Assert.Equal("1.2", settings.SelectedBuild);

        // Core's options turned on by hand: kept, and the servers get the region the known build has.
        Assert.Equal(ClientArguments, handNamed.ClientArguments);
        Assert.Equal(ServerArguments + " -McpRegion=NA", handNamed.ServerArguments);
        Assert.Equal([DllRole.Redirect, DllRole.Client], handNamed.ClientDlls);

        // Added with the switch off (every Core option off): Core's options, keeping the region set by hand.
        foreach (var build in new[] { addedOff, copy })
        {
            Assert.True(build.UseHttpProxy);
            Assert.Equal([DllRole.Redirect, DllRole.Client], build.ClientDlls);
            Assert.Equal([DllRole.Redirect, DllRole.Server], build.ServerDlls);
            Assert.False(build.CoreDefaultsAdded);
            Assert.True(KnownBuilds.CanPlay(build));
            Assert.True(KnownBuilds.CanHost(build));
        }

        Assert.Equal(ServerArguments + " -mcpregion=EU", addedOff.ServerArguments);
        Assert.Equal(ServerArguments + " -McpRegion=NA", copy.ServerArguments);
        Assert.Equal(@"F:\Builds\Copy", copy.Path);
    }

    [Theory]
    [InlineData("Cert CL-3532353", 3532353, "Fortnite 1.1 (Cert CL-3532353)")]
    [InlineData("Cert CL-3532353 (3)", 3532353, "Fortnite 1.1 (Cert CL-3532353) (3)")]
    [InlineData("Fortnite Cert CL-3541083", 3541083, "Fortnite 1.2 (CL-3541083)")]
    [InlineData("Fortnite CL-3541083", 3541083, "Fortnite 1.2 (CL-3541083)")]
    [InlineData("Fortnite 1.2.0 CL-3541083 (2)", 3541083, "Fortnite 1.2 (CL-3541083) (2)")]
    [InlineData("Fortnite 1.2 (CL-3541083)", 3541083, null)]
    [InlineData("1.2", 3541083, null)]
    [InlineData("My Cert CL-3541083", 3541083, null)]
    [InlineData("Fortnite Cert CL-3541083", 3532353, null)]
    [InlineData("Fortnite 1.10 CL-3790078", 3790078, null)]
    public void OnlyNamesTheLauncherMadeUpAreRenamed(string name, int changelist, string? renamed)
    {
        Assert.Equal(renamed, KnownBuilds.CurrentName(name, changelist));
    }
}
