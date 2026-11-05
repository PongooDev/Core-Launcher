using Core.Launcher.Hosting.Processes;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Tests;

public sealed class EacSplashTests
{
    private static BuildSettings Build(TempFolder folder) => new() { Name = "Test", Path = folder.Path };

    private static string AddSplash(TempFolder folder, string? settingsJson = null)
    {
        var launcher = folder.Combine("FortniteGame", "Binaries", "Win64", "EasyAntiCheat", "Launcher");
        Directory.CreateDirectory(launcher);
        var image = Path.Combine(launcher, "SplashScreen.png");
        File.WriteAllBytes(image, [0x89, 0x50, 0x4E, 0x47]);
        if (settingsJson is not null)
        {
            File.WriteAllText(Path.Combine(launcher, "Settings.json"), settingsJson);
        }

        return image;
    }

    [Fact]
    public void TheSplashIsOffByDefaultAndSaves()
    {
        using var folder = new TempFolder();
        var store = new SettingsStore(folder.Combine("settings.json"));
        Assert.False(store.Load().ShowEacSplash);

        store.Save(new LauncherSettings { ShowEacSplash = true });
        Assert.True(store.Load().ShowEacSplash);
        Assert.True(SettingsStore.Clone(store.Load()).ShowEacSplash);
    }

    [Fact]
    public void ABuildWithTheImageGetsItOnlyWhileTheSettingIsOn()
    {
        using var folder = new TempFolder();
        var image = AddSplash(folder);
        var build = Build(folder);

        Assert.Equal(image, EacSplash.FindImage(build));
        Assert.Equal(image, EacSplash.Find(new LauncherSettings { ShowEacSplash = true }, build));
        Assert.Null(EacSplash.Find(new LauncherSettings(), build));
    }

    [Fact]
    public void ABuildWithoutTheImageGetsNoWindow()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(folder.Combine("FortniteGame", "Binaries", "Win64"));

        Assert.Null(EacSplash.Find(new LauncherSettings { ShowEacSplash = true }, Build(folder)));
        Assert.Null(EacSplash.FindImage(new BuildSettings { Path = folder.Combine("missing") }));
    }

    [Fact]
    public void TheBuildsOwnLauncherSettingsCanHideTheSplash()
    {
        using var hidden = new TempFolder();
        AddSplash(hidden, """{ "hide_splash_screen": "1" }""");
        Assert.Null(EacSplash.FindImage(Build(hidden)));

        using var shown = new TempFolder();
        var image = AddSplash(shown, """{ "hide_splash_screen": "0" }""");
        Assert.Equal(image, EacSplash.FindImage(Build(shown)));

        using var broken = new TempFolder();
        image = AddSplash(broken, "not json");
        Assert.Equal(image, EacSplash.FindImage(Build(broken)));
    }
}
