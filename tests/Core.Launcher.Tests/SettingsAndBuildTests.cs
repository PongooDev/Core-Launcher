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
        Assert.Equal("NA", settings.Servers.Region);
        Assert.Equal(DllSource.Release, settings.Dlls.Source);
        Assert.Equal("PongooDev/CoreV2", settings.Dlls.Repository);
        Assert.True(settings.Servers.Enabled);
    }

    [Fact]
    public void SettingsRoundTripAndClamp()
    {
        using var folder = new TempFolder();
        var store = new SettingsStore(folder.Combine("settings.json"));
        var settings = new LauncherSettings { PlayerName = "  Player  ", Password = "  MySecret  " };
        settings.Servers.Instances = 99;
        settings.Dlls.Source = DllSource.Local;
        settings.Builds.Add(new BuildSettings { Name = "Cert", ServerDlls = [DllRole.Server] });
        store.Save(settings);

        var loaded = store.Load();
        Assert.Equal("Player", loaded.PlayerName);
        Assert.Equal("MySecret", loaded.Password);
        Assert.Equal(SettingsStore.MaxInstances, loaded.Servers.Instances);
        Assert.Equal(DllSource.Local, loaded.Dlls.Source);
        Assert.Equal([DllRole.Server], loaded.Builds.Single().ServerDlls);
        Assert.Contains("\"Local\"", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void SettingsSavedBeforeTheServerRegionAdvertiseNa()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("settings.json");
        File.WriteAllText(path, $$$"""{"Version": {{{SettingsStore.CurrentVersion}}}, "Servers": {"Instances": 1, "Region": null}}""");
        Assert.Equal("NA", new SettingsStore(path).Load().Servers.Region);

        File.WriteAllText(path, $$$"""{"Version": {{{SettingsStore.CurrentVersion}}}, "Servers": {"Region": " eu "}}""");
        Assert.Equal("EU", new SettingsStore(path).Load().Servers.Region);
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
                { "Name": "Other CL-3790078", "Path": "Z:\\gone", "ServerArguments": "-epicportal" }
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
        var build = KnownBuilds.Create(@"F:\Builds\++Fortnite+Cert-CL-3532353\Fortnite", BuildVersion.Parse(CertBuildVersion), allowUnsupported: false);

        Assert.Equal("Fortnite 1.1 (Cert CL-3532353)", build.Name);
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
        var build = KnownBuilds.Create(@"F:\Builds\Other", new BuildVersion(4, 19, 0, 3790078, "++Fortnite+Release-1.10"), allowUnsupported: false);
        Assert.Equal("Fortnite 1.10 CL-3790078", build.Name);
        Assert.Empty(build.ServerDlls);
        Assert.Empty(build.ClientDlls);
    }

    private static readonly BuildVersion Unsupported = new(4, 19, 0, 3790078, "++Fortnite+Release-1.10");

    private static BuildSettings Cert(bool allowUnsupported) =>
        KnownBuilds.Create(@"F:\Builds\Cert", BuildVersion.Parse(CertBuildVersion), allowUnsupported);

    private static BuildSettings Other(string folder, int changelist, bool allowUnsupported) =>
        KnownBuilds.Create(folder, new BuildVersion(0, 0, 0, changelist, ""), allowUnsupported);

    private static string Json(BuildSettings build) => System.Text.Json.JsonSerializer.Serialize(build);

    [Fact]
    public void AllowUnsupportedBuildsIsOffByDefaultAndSaved()
    {
        using var folder = new TempFolder();
        var store = new SettingsStore(folder.Combine("settings.json"));
        Assert.False(store.Load().AllowUnsupportedBuilds);

        var settings = new LauncherSettings();
        settings.Builds.Add(KnownBuilds.Create(@"F:\Builds\Other", Unsupported, allowUnsupported: false));
        KnownBuilds.SetAllowUnsupported(settings, true);
        store.Save(settings);

        var loaded = store.Load();
        Assert.True(loaded.AllowUnsupportedBuilds);
        Assert.True(loaded.Builds.Single().CoreDefaultsAdded);
        Assert.True(KnownBuilds.CanPlay(loaded.Builds.Single()));
    }

    [Fact]
    public void SettingsFromBeforeTheSwitchLoadWithItOff()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("settings.json");
        File.WriteAllText(path, """
            { "Version": 2, "Builds": [ { "Name": "Fortnite 1.10 CL-3790078", "Changelist": 3790078, "ServerArguments": "-epicportal" } ] }
            """);

        var settings = new SettingsStore(path).Load();
        Assert.False(settings.AllowUnsupportedBuilds);
        Assert.False(settings.Builds[0].CoreDefaultsAdded);
        Assert.False(KnownBuilds.CanPlay(settings.Builds[0]));
    }

    [Fact]
    public void UnsupportedBuildAddedWithTheSwitchOnGetsCertsDefaults()
    {
        var build = KnownBuilds.Create(@"F:\Builds\Other", Unsupported, allowUnsupported: true);
        var cert = Cert(allowUnsupported: false);

        Assert.Equal("Fortnite 1.10 CL-3790078", build.Name);
        Assert.Equal(3790078, build.Changelist);
        Assert.Equal(@"F:\Builds\Other", build.Path);
        Assert.True(build.CoreDefaultsAdded);
        Assert.Equal(cert.UseHttpProxy, build.UseHttpProxy);
        Assert.Equal(cert.ClientDlls, build.ClientDlls);
        Assert.Equal(cert.ServerDlls, build.ServerDlls);
        Assert.Equal(cert.ClientArguments, build.ClientArguments);
        Assert.Equal(cert.ServerArguments, build.ServerArguments);
        Assert.EndsWith(KnownBuilds.CertServerRegion, build.ServerArguments);
        Assert.True(KnownBuilds.CanPlay(build));
        Assert.True(KnownBuilds.CanHost(build));
    }

    [Fact]
    public void TheSwitchDoesNotChangeSupportedBuilds()
    {
        var on = Cert(allowUnsupported: true);

        Assert.Equal(Json(Cert(allowUnsupported: false)), Json(on));
        Assert.False(on.CoreDefaultsAdded);
    }

    [Fact]
    public void UnsupportedBuildsCanPlayAndHostOnlyWithTheSwitchOn()
    {
        var build = KnownBuilds.Create(@"F:\Builds\Other", Unsupported, allowUnsupported: false);
        Assert.False(KnownBuilds.CanPlay(build));
        Assert.False(KnownBuilds.CanHost(build));
        Assert.Contains("doesn't support Fortnite 1.10 CL-3790078 yet", KnownBuilds.NotSupported(build, allowUnsupported: false));
        Assert.Contains("Turn on Allow unsupported builds", KnownBuilds.NotSupported(build, allowUnsupported: false));

        var settings = new LauncherSettings { Builds = [build] };
        KnownBuilds.SetAllowUnsupported(settings, true);
        Assert.True(KnownBuilds.CanPlay(build));
        Assert.True(KnownBuilds.CanHost(build));

        KnownBuilds.SetAllowUnsupported(settings, false);
        Assert.False(KnownBuilds.CanPlay(build));
        Assert.False(KnownBuilds.CanHost(build));
    }

    [Fact]
    public void WithTheSwitchOnAnUnsupportedBuildIsAWarning()
    {
        var build = KnownBuilds.Create(@"F:\Builds\Other", Unsupported, allowUnsupported: true);
        build.ServerDlls = [DllRole.Redirect];

        Assert.False(KnownBuilds.CanHost(build));
        Assert.Contains("Core DLLs turned off", KnownBuilds.NotSupported(build, allowUnsupported: true));
        Assert.DoesNotContain("doesn't support", KnownBuilds.NotSupported(build, allowUnsupported: true));
        Assert.Equal(
            "Fortnite 1.10 CL-3790078 isn't supported and may not work: Core is only tested on Fortnite 1.1 to 1.9, and support for Fortnite 1.9.1 is in progress.",
            KnownBuilds.UnsupportedWarning(build.Name));
    }

    [Fact]
    public void TurningTheSwitchOnGivesDefaultsOnlyToUntouchedUnsupportedBuilds()
    {
        var untouched = KnownBuilds.Create(@"F:\Builds\Other", Unsupported, allowUnsupported: false);
        untouched.ServerArguments += " -mcpregion=EU";
        var byHand = Other(@"F:\Builds\Mine", 14786821, allowUnsupported: false);
        byHand.ClientDlls = [DllRole.Redirect];
        var byHandBefore = Json(byHand);
        var cert = Cert(allowUnsupported: false);
        cert.UseHttpProxy = false;
        cert.ClientDlls = [];
        cert.ServerDlls = [];
        var certBefore = Json(cert);
        var settings = new LauncherSettings { Builds = [untouched, byHand, cert] };

        var changed = KnownBuilds.SetAllowUnsupported(settings, true);

        Assert.True(settings.AllowUnsupportedBuilds);
        Assert.Equal([untouched], changed);
        Assert.True(untouched.UseHttpProxy);
        Assert.Equal([DllRole.Redirect, DllRole.Client], untouched.ClientDlls);
        Assert.Equal([DllRole.Redirect, DllRole.Server], untouched.ServerDlls);
        Assert.Equal("-epicapp=Fortnite -epicenv=Prod -epicportal -SkipPatchCheck -nullrhi -nosplash -nosound -mcpregion=EU", untouched.ServerArguments);
        Assert.True(untouched.CoreDefaultsAdded);
        Assert.Equal(byHandBefore, Json(byHand));
        Assert.Equal(certBefore, Json(cert));
    }

    [Fact]
    public void TurningTheSwitchOffTakesBackOnlyUnchangedDefaults()
    {
        var added = KnownBuilds.Create(@"F:\Builds\Other", Unsupported, allowUnsupported: false);
        var addedBefore = Json(added);
        var changedSince = Other(@"F:\Builds\Mine", 14786821, allowUnsupported: true);
        var byHand = Other(@"F:\Builds\Third", 5046157, allowUnsupported: false);
        byHand.UseHttpProxy = true;
        byHand.ClientDlls = [DllRole.Redirect, DllRole.Client];
        var byHandBefore = Json(byHand);
        var settings = new LauncherSettings { Builds = [added, changedSince, byHand] };
        KnownBuilds.SetAllowUnsupported(settings, true);
        changedSince.ServerDlls = [DllRole.Redirect];

        var changed = KnownBuilds.SetAllowUnsupported(settings, false);

        Assert.False(settings.AllowUnsupportedBuilds);
        Assert.Equal([added], changed);
        Assert.Equal(addedBefore, Json(added));
        Assert.Equal([DllRole.Redirect], changedSince.ServerDlls);
        Assert.True(changedSince.UseHttpProxy);
        Assert.False(changedSince.CoreDefaultsAdded);
        Assert.Equal(byHandBefore, Json(byHand));
        Assert.True(KnownBuilds.CanPlay(byHand));
    }
}
