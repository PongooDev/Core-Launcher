using Core.Launcher.Hosting.Builds;
using Core.Launcher.Hosting.Dlls;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Tests;

public sealed class SettingsAndBuildTests
{
    private const string CertBuildVersion = """
        {
        	"MajorVersion" : 4,
        	"MinorVersion" : 16,
        	"PatchVersion" : 0,
        	"Changelist" : 3532353,
        	"CompatibleChangelist" : 0,
        	"IsLicenseeVersion" : 0,
        	"IsPromotedBuild" : 1,
        	"BranchName" : "++Fortnite+Release-Cert"
        }
        """;

    [Fact]
    public void MissingSettingsFileGivesDefaults()
    {
        using var folder = new TempFolder();
        var settings = new SettingsStore(folder.Combine("settings.json")).Load();

        Assert.Equal(5595, settings.Backend.Port);
        Assert.Equal(2, settings.Servers.Instances);
        Assert.Equal(180, settings.Servers.ReadyTimeoutSeconds);
        Assert.Equal(DllSource.Release, settings.Dlls.Source);
        Assert.Equal("PongooDev/CoreV2", settings.Dlls.Repository);
        Assert.True(settings.Servers.Enabled);
    }

    [Fact]
    public void SettingsRoundTripAndClamp()
    {
        using var folder = new TempFolder();
        var store = new SettingsStore(folder.Combine("settings.json"));
        var settings = new LauncherSettings { PlayerName = "  Player  " };
        settings.Servers.Instances = 99;
        settings.Dlls.Source = DllSource.Local;
        settings.Builds.Add(new BuildSettings { Name = "Cert", ServerDlls = [DllRole.Server] });
        store.Save(settings);

        var loaded = store.Load();
        Assert.Equal("Player", loaded.PlayerName);
        Assert.Equal(SettingsStore.MaxInstances, loaded.Servers.Instances);
        Assert.Equal(DllSource.Local, loaded.Dlls.Source);
        Assert.Equal([DllRole.Server], loaded.Builds.Single().ServerDlls);
        Assert.Contains("\"Local\"", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void OlderSettingsLoseForceLogFlushOnce()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("settings.json");
        File.WriteAllText(path, """
            {
              "SetupComplete": true,
              "Builds": [
                { "Name": "Cert CL-3532353", "ClientArguments": "-epicapp=Fortnite -SkipPatchCheck -FORCELOGFLUSH",
                  "ServerArguments": "-epicapp=Fortnite -forcelogflush -nullrhi" }
              ]
            }
            """);
        var store = new SettingsStore(path);

        var migrated = store.Load();
        Assert.Equal(SettingsStore.CurrentVersion, migrated.Version);
        Assert.Equal("-epicapp=Fortnite -SkipPatchCheck", migrated.Builds[0].ClientArguments);
        Assert.Equal("-epicapp=Fortnite -nullrhi -McpRegion=NA", migrated.Builds[0].ServerArguments);
        Assert.DoesNotContain("FORCELOGFLUSH", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);

        migrated.Builds[0].ClientArguments += " -FORCELOGFLUSH";
        store.Save(migrated);
        Assert.EndsWith("-FORCELOGFLUSH", store.Load().Builds[0].ClientArguments);
    }

    [Fact]
    public void SavedCertServersGetTheirRegionOnce()
    {
        using var folder = new TempFolder();
        var certFolder = folder.Combine("Cert");
        Directory.CreateDirectory(Path.Combine(certFolder, "Engine", "Build"));
        File.WriteAllText(Path.Combine(certFolder, BuildVersion.RelativePath), CertBuildVersion);
        var path = folder.Combine("settings.json");
        File.WriteAllText(path, $$"""
            {
              "Version": 1,
              "Builds": [
                { "Name": "My Cert", "Path": {{System.Text.Json.JsonSerializer.Serialize(certFolder)}}, "ClientArguments": "-epicportal", "ServerArguments": "-epicportal -nullrhi" },
                { "Name": "Cert CL-3532353 (2)", "Path": "Z:\\gone", "ServerArguments": "-epicportal -mcpregion=EU" },
                { "Name": "Other CL-3700114", "Path": "Z:\\gone", "ServerArguments": "-epicportal" }
              ]
            }
            """);

        var builds = new SettingsStore(path).Load().Builds;

        Assert.Equal(KnownBuilds.Cert, builds[0].Changelist);
        Assert.Equal("-epicportal -nullrhi -McpRegion=NA", builds[0].ServerArguments);
        Assert.Equal("-epicportal", builds[0].ClientArguments);
        Assert.Equal(KnownBuilds.Cert, builds[1].Changelist);
        Assert.Equal("-epicportal -mcpregion=EU", builds[1].ServerArguments);
        Assert.Equal(0, builds[2].Changelist);
        Assert.Equal("-epicportal", builds[2].ServerArguments);
    }

    [Fact]
    public void ServerLoginNumbersEachInstance()
    {
        Assert.Equal("CoreServer2", new ServerSettings().LoginFor(2));
    }

    [Fact]
    public void BuildVersionParsesTheCertBuild()
    {
        var version = BuildVersion.Parse(CertBuildVersion);
        Assert.Equal(3532353, version.Changelist);
        Assert.Equal("4.16.0", version.EngineVersion);
        Assert.Equal("++Fortnite+Release-Cert", version.BranchName);
    }

    [Fact]
    public void DetectorFindsTheRootFromAboveAndBelow()
    {
        using var folder = new TempFolder();
        var root = folder.Combine("++Fortnite+Cert-CL-3532353", "Fortnite");
        Directory.CreateDirectory(Path.Combine(root, "Engine", "Build"));
        Directory.CreateDirectory(Path.Combine(root, "FortniteGame", "Binaries", "Win64"));
        File.WriteAllText(Path.Combine(root, BuildVersion.RelativePath), CertBuildVersion);

        Assert.Equal(root, BuildDetector.Detect(root)!.Folder);
        Assert.Equal(root, BuildDetector.Detect(folder.Combine("++Fortnite+Cert-CL-3532353"))!.Folder);
        Assert.Equal(root, BuildDetector.Detect(Path.Combine(root, "FortniteGame", "Binaries", "Win64"))!.Folder);
        Assert.Null(BuildDetector.Detect(folder.Path));
    }

    [Fact]
    public void KnownCertBuildGetsRunServerSettings()
    {
        var build = KnownBuilds.Create(@"F:\Builds\++Fortnite+Cert-CL-3532353\Fortnite", BuildVersion.Parse(CertBuildVersion));

        Assert.Equal("Cert CL-3532353", build.Name);
        Assert.True(build.UseHttpProxy);
        Assert.Equal([DllRole.Redirect, DllRole.Client], build.ClientDlls);
        Assert.Equal([DllRole.Redirect, DllRole.Server], build.ServerDlls);
        Assert.Contains("-nullrhi", build.ServerArguments);
        Assert.DoesNotContain("-nullrhi", build.ClientArguments);
        Assert.DoesNotContain("FORCELOGFLUSH", build.ClientArguments + build.ServerArguments);
        Assert.EndsWith("-McpRegion=NA", build.ServerArguments);
        Assert.Equal(3532353, build.Changelist);
        Assert.Equal(@"F:\Builds\++Fortnite+Cert-CL-3532353\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", build.ExecutablePath);
    }

    [Fact]
    public void UnknownBuildHasNoDlls()
    {
        var build = KnownBuilds.Create(@"F:\Builds\Other", new BuildVersion(4, 19, 0, 3700114, "++Fortnite+Release-1.8"));
        Assert.Equal("Fortnite 1.8 CL-3700114", build.Name);
        Assert.Empty(build.ServerDlls);
        Assert.Empty(build.ClientDlls);
    }
}
