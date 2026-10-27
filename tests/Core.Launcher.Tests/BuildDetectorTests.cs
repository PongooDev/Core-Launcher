using System.Text;
using Core.Launcher.Hosting.Builds;
using Core.Launcher.Hosting.Dlls;

namespace Core.Launcher.Tests;

public sealed class BuildDetectorTests
{
    /// <summary>A build folder as shared builds come: Engine and FortniteGame with the client exe, and no Build.version.</summary>
    private static string SharedBuild(TempFolder folder, string relative, byte[]? executable = null)
    {
        var root = folder.Combine(relative);
        var exe = Path.Combine(root, BuildDetector.ClientExecutable);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        Directory.CreateDirectory(Path.Combine(root, "Engine", "Binaries"));
        File.WriteAllBytes(exe, executable ?? [0x4D, 0x5A]);
        return root;
    }

    private static byte[] ExeWith(params (int Offset, string Text)[] strings)
    {
        var size = strings.Max(item => item.Offset) + 4096;
        var data = new byte[size];
        new Random(1).NextBytes(data);
        foreach (var (offset, text) in strings)
        {
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            bytes.CopyTo(data, offset);
        }

        return data;
    }

    [Fact]
    public void ReadsCloudContentWhenBuildVersionIsMissing()
    {
        using var folder = new TempFolder();
        var root = SharedBuild(folder, "26.20");
        Directory.CreateDirectory(Path.Combine(root, "Cloud"));
        File.WriteAllText(Path.Combine(root, "Cloud", "cloudcontent.json"), """{"AppName":"FortniteContentBuilds","BuildVersion":"++Fortnite+Release-26.20-CL-28096793","Platform":"Windows"}""");

        var build = BuildDetector.Detect(root)!;

        Assert.Equal((28096793, "++Fortnite+Release-26.20", "cloudcontent.json"), (build.Version.Changelist, build.Version.BranchName, build.Source));
        Assert.Null(build.Version.Engine);
    }

    [Fact]
    public void ReadsBuildInfo()
    {
        using var folder = new TempFolder();
        var root = SharedBuild(folder, "26.20");
        Directory.CreateDirectory(Path.Combine(root, "Cloud"));
        File.WriteAllText(Path.Combine(root, "Cloud", "BuildInfo.ini"), "[Content]\r\nBuild=Ship-++Fortnite+Release-26.20-CL-28096793\r\nLabel=x\r\n");

        Assert.Equal((28096793, "BuildInfo.ini"), (BuildDetector.Detect(root)!.Version.Changelist, BuildDetector.Detect(root)!.Source));
    }

    [Fact]
    public void ReadsTheBuildStringInTheExe()
    {
        using var folder = new TempFolder();
        var exe = ExeWith(
            (1000, @"++Fortnite+Release-14.60+Full\Sync\Engine\Source\Runtime\Engine\Private\"),
            (5000, "++Fortnite+Release-14.60"),
            (9000, "++Fortnite+Release-14.60-CL-14786821"));
        var root = SharedBuild(folder, "build", exe);

        var build = BuildDetector.Detect(root)!;

        Assert.Equal((14786821, "++Fortnite+Release-14.60", "FortniteClient-Win64-Shipping.exe"), (build.Version.Changelist, build.Version.BranchName, build.Source));
        Assert.Equal("Fortnite 14.60 CL-14786821", KnownBuilds.Create(build.Folder, build.Version, allowUnsupported: false).Name);
    }

    [Fact]
    public void FindsABuildStringThatSpansTwoReads()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("FortniteClient-Win64-Shipping.exe");
        File.WriteAllBytes(path, ExeWith(((4 << 20) - 21, "++Fortnite+Release-Cert-CL-3532353")));

        Assert.Equal("++Fortnite+Release-Cert-CL-3532353", BuildDetector.FindBuildString(path));
    }

    [Fact]
    public void FallsBackToTheFolderName()
    {
        using var folder = new TempFolder();
        var root = SharedBuild(folder, Path.Combine("14.60-CL-14786821", "14.60-CL-14786821"));

        var build = BuildDetector.Detect(root)!;

        Assert.Equal((14786821, "++Fortnite+Release-14.60", "the folder name"), (build.Version.Changelist, build.Version.BranchName, build.Source));
    }

    [Fact]
    public void FindsTheBuildInsideAndAboveThePickedFolder()
    {
        using var folder = new TempFolder();
        var root = SharedBuild(folder, Path.Combine("14.60-CL-14786821", "14.60-CL-14786821"));

        Assert.Equal(root, BuildDetector.Detect(folder.Combine("14.60-CL-14786821"))!.Folder);
        Assert.Equal(root, BuildDetector.Detect(Path.Combine(root, "FortniteGame"))!.Folder);
        Assert.Equal(root, BuildDetector.Detect(Path.Combine(root, "FortniteGame", "Binaries", "Win64"))!.Folder);
        Assert.Null(BuildDetector.Detect(folder.Path));
        Assert.Null(BuildDetector.Detect(folder.Combine("missing")));
    }

    [Fact]
    public void ABuildWithNoVersionAnywhereIsStillFound()
    {
        using var folder = new TempFolder();
        var build = BuildDetector.Detect(SharedBuild(folder, "build"))!;
        Assert.Equal(0, build.Version.Changelist);
        Assert.Equal("Fortnite build", KnownBuilds.Create(build.Folder, build.Version, allowUnsupported: false).Name);
    }

    [Theory]
    [InlineData("++Fortnite+Release-26.20-CL-28096793", "++Fortnite+Release-26.20", 28096793)]
    [InlineData("Ship-++Fortnite+Release-26.20-CL-28096793", "++Fortnite+Release-26.20", 28096793)]
    [InlineData("++Fortnite+Release-14.60-CL-14786821-Windows", "++Fortnite+Release-14.60", 14786821)]
    [InlineData("++Fortnite+Release-Cert-CL-3532353", "++Fortnite+Release-Cert", 3532353)]
    public void ParsesBuildStrings(string text, string branch, int changelist)
    {
        Assert.Equal(new BuildVersion(0, 0, 0, changelist, branch), BuildVersion.FromBuildString(text));
    }

    [Theory]
    [InlineData(@"++Fortnite+Release-Cert+Full\Sync\Engine")]
    [InlineData("++Fortnite+Release-14.60")]
    [InlineData("14.60")]
    public void IgnoresStringsWithoutAChangelist(string text)
    {
        Assert.Null(BuildVersion.FromBuildString(text));
    }

    [Theory]
    [InlineData("14.60-CL-14786821", "14.60", 14786821)]
    [InlineData("Fortnite 7.40 CL-5046157", "7.40", 5046157)]
    [InlineData("CL-3532353", "", 3532353)]
    public void ReadsFolderNames(string name, string shortName, int changelist)
    {
        var version = BuildVersion.FromFolderName(name)!;
        Assert.Equal((shortName, changelist), (version.ShortName, version.Changelist));
    }

    [Fact]
    public void UnsupportedBuildsCanNeitherPlayNorHost()
    {
        var unsupported = KnownBuilds.Create(@"C:\14.60", new BuildVersion(0, 0, 0, 14786821, "++Fortnite+Release-14.60"), allowUnsupported: false);
        Assert.False(KnownBuilds.CanPlay(unsupported));
        Assert.False(KnownBuilds.CanHost(unsupported));
        Assert.Equal(
            "Core doesn't support Fortnite 14.60 CL-14786821 yet. It supports Fortnite 1.1 to 1.2, and support for Fortnite 1.7.2 to 1.8 is in progress. Turn on Allow unsupported builds in Settings → Builds to try it.",
            KnownBuilds.NotSupported(unsupported, allowUnsupported: false));

        var cert = KnownBuilds.Create(@"C:\Cert", new BuildVersion(4, 16, 0, KnownBuilds.Cert, "++Fortnite+Release-Cert"), allowUnsupported: false);
        Assert.True(KnownBuilds.CanPlay(cert));
        Assert.True(KnownBuilds.CanHost(cert));

        cert.ServerDlls = [DllRole.Redirect];
        Assert.False(KnownBuilds.CanHost(cert));
        Assert.Contains("Core DLLs turned off", KnownBuilds.NotSupported(cert, allowUnsupported: false));
    }

    /// <summary>The build strings in real exes, from CORE_LAUNCHER_BUILD_DIRS (folders separated by ';').</summary>
    [Fact]
    public void ReadsRealExes()
    {
        var folders = Environment.GetEnvironmentVariable("CORE_LAUNCHER_BUILD_DIRS");
        if (string.IsNullOrEmpty(folders))
        {
            Assert.Skip("Set CORE_LAUNCHER_BUILD_DIRS to build folders to run this");
        }

        foreach (var folder in folders.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = BuildDetector.FindBuildString(Path.Combine(folder, BuildDetector.ClientExecutable));
            Assert.NotNull(BuildVersion.FromBuildString(text ?? ""));
            Assert.Equal(BuildVersion.Read(folder)?.Changelist ?? BuildVersion.FromBuildString(text!)!.Changelist, BuildVersion.FromBuildString(text!)!.Changelist);
            TestContext.Current.SendDiagnosticMessage($"{folder}: {text}");
        }
    }
}
