using System.Runtime.InteropServices;
using Core.Launcher.Hosting.Builds;
using Core.Launcher.Hosting.Dlls;
using Core.Launcher.Hosting.Processes;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Tests;

public sealed partial class CommandLineTests
{
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint hMem);

    private static string[] Split(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        try
        {
            return [.. Enumerable.Range(0, count).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * nint.Size))!)];
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("quote\"inside")]
    [InlineData(@"trail\")]
    [InlineData(@"trail space\")]
    [InlineData(@"back\\""q")]
    [InlineData(@"C:\Program Files\x.exe")]
    [InlineData(@"F:\Builds\++Fortnite+Cert-CL-3532353\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe")]
    public void QuoteRoundTripsThroughCommandLineToArgvW(string argument)
    {
        Assert.Equal(["first.exe", argument], Split("first.exe " + CommandLine.Quote(argument)));
    }

    [Fact]
    public void QuoteLeavesSimpleArgumentsAlone()
    {
        Assert.Equal(@"F:\Builds\++Fortnite+Cert-CL-3532353\x.exe", CommandLine.Quote(@"F:\Builds\++Fortnite+Cert-CL-3532353\x.exe"));
        Assert.Equal("\"C:\\Program Files\\x.exe\"", CommandLine.Quote(@"C:\Program Files\x.exe"));
    }

    [Fact]
    public void SwitchQuotesValuesWithSpacesAfterTheEquals()
    {
        Assert.Equal("-AUTH_LOGIN=Player", CommandLine.Switch("AUTH_LOGIN", "Player"));
        Assert.Equal("-AUTH_LOGIN=\"Two Words\"", CommandLine.Switch("AUTH_LOGIN", "Two Words"));
        Assert.Throws<ArgumentException>(() => CommandLine.Switch("AUTH_LOGIN", "bad\"name"));
    }

    private static BuildSettings Cert() =>
        KnownBuilds.Create(@"F:\Builds\++Fortnite+Cert-CL-3532353\Fortnite", new BuildVersion(4, 16, 0, 3532353, "++Fortnite+Release-Cert"), allowUnsupported: false);

    [Fact]
    public void BattleRoyaleServersStartOnAthenaAndRegisterForMatchmaking()
    {
        var build = KnownBuilds.Create(@"F:\Builds.7.2", new BuildVersion(4, 16, 0, KnownBuilds.Fortnite172, "++Fortnite+Release-Cert"), allowUnsupported: false);
        Assert.Equal(BuildSupport.Full, KnownBuilds.SupportOf(build.Changelist));
        Assert.Equal(BuildSupport.Full, KnownBuilds.SupportOf(KnownBuilds.Fortnite18));
        Assert.Equal(BuildSupport.Full, KnownBuilds.SupportOf(KnownBuilds.Fortnite181));
        Assert.Equal(BuildSupport.Full, KnownBuilds.SupportOf(KnownBuilds.Fortnite182));
        Assert.Equal(BuildSupport.Full, KnownBuilds.SupportOf(KnownBuilds.Fortnite19));
        Assert.Equal(BuildSupport.InProgress, KnownBuilds.SupportOf(KnownBuilds.Fortnite191));
        Assert.True(KnownBuilds.HasBattleRoyale(KnownBuilds.Fortnite191));
        Assert.True(KnownBuilds.HasBattleRoyale(KnownBuilds.Fortnite18));
        Assert.True(KnownBuilds.HasBattleRoyale(build.Changelist));
        Assert.False(KnownBuilds.HasBattleRoyale(Cert().Changelist));

        Assert.Contains("-CoreMode=BattleRoyale", Split("x.exe " + LaunchArguments.Server(build, 5595, "CoreServer3", battleRoyale: true)));
        Assert.DoesNotContain("-CoreMode=BattleRoyale", Split("x.exe " + LaunchArguments.Server(build, 5595, "CoreServer1")));

        // By default 1.7.2 keeps two Save the World servers and two Battle Royale ones, which come after them; 1.2 two.
        var servers = new ServerSettings();
        Assert.Equal(4, KnownBuilds.ServerCount(servers, build));
        Assert.Equal(2, KnownBuilds.ServerCount(servers, Cert()));
        Assert.False(servers.HostsBattleRoyale(2));
        Assert.True(servers.HostsBattleRoyale(3));
    }

    [Fact]
    public void ServerArgumentsHaveEveryRunServerSwitchAndTheRedirectsBackend()
    {
        var arguments = LaunchArguments.Server(Cert(), 5595, "CoreServer");

        // Core-Docs/Tools/run_server.py ARGS, CoreRedirect's backend (servers load it first) and the session region.
        string[] expected =
        [
            "-epicapp=Fortnite", "-epicenv=Prod", "-epicportal", "-httpproxy=127.0.0.1:5595", "-AUTH_LOGIN=CoreServer",
            $"-AUTH_PASSWORD={LaunchArguments.Password}", "-AUTH_TYPE=epic", "-SkipPatchCheck", "-nullrhi", "-nosplash", "-nosound",
            "-CoreBackend=http://127.0.0.1:5595", "-unattended", "-saveddirsuffix=CoreServer", "-McpRegion=NA",
        ];
        Assert.Equal(expected.Order(), Split("x.exe " + arguments).Skip(1).Order());
    }

    [Fact]
    public void ServersAdvertiseTheChosenRegionUnlessTheirBuildSetsAnother()
    {
        // The default -McpRegion=NA gives way to Settings → Servers → Region (Core-Backend #121).
        var build = Cert();
        var server = Split("x.exe " + LaunchArguments.Server(build, 5595, "CoreServer1", region: "EU"));
        Assert.Single(server, argument => argument.StartsWith("-McpRegion=", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("-McpRegion=EU", server);
        Assert.Contains("-McpRegion=NA", Split("x.exe " + LaunchArguments.Server(build, 5595, "CoreServer1", region: "NA")));
        Assert.Contains("-McpRegion=NA", Split("x.exe " + LaunchArguments.Server(build, 5595, "CoreServer1")));

        // A build without one gets it; one whose own arguments name another region keeps that.
        build.ServerArguments = "-epicapp=Fortnite";
        Assert.Contains("-McpRegion=OCE", Split("x.exe " + LaunchArguments.Server(build, 5595, "CoreServer1", region: "OCE")));
        build.ServerArguments = "-epicapp=Fortnite -McpRegion=BR";
        server = Split("x.exe " + LaunchArguments.Server(build, 5595, "CoreServer1", region: "EU"));
        Assert.Single(server, argument => argument.StartsWith("-McpRegion=", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("-McpRegion=BR", server);

        Assert.Equal(["NA", "EU", "OCE", "BR", "ASIA"], KnownBuilds.ServerRegions.Select(region => region.Code));
        Assert.DoesNotContain("-McpRegion", LaunchArguments.Client(Cert(), 5595, "Player"));
    }

    [Fact]
    public void ServersAlwaysRunNullRhiAndUnattendedAndClientsNever()
    {
        var build = Cert();
        build.ServerArguments = "-epicapp=Fortnite -epicportal";
        var server = Split("x.exe " + LaunchArguments.Server(build, 5595, "CoreServer1"));
        Assert.Contains("-nullrhi", server);
        Assert.Contains("-unattended", server);

        var client = LaunchArguments.Client(Cert(), 5595, "Player");
        Assert.DoesNotContain("-nullrhi", client);
        Assert.DoesNotContain("-unattended", client);
        Assert.DoesNotContain("-saveddirsuffix", client);
        Assert.DoesNotContain("-McpRegion", client);

        build.ServerArguments += " -NullRHI -Unattended";
        server = Split("x.exe " + LaunchArguments.Server(build, 5595, "CoreServer1"));
        Assert.Single(server, argument => argument.Equals("-nullrhi", StringComparison.OrdinalIgnoreCase));
        Assert.Single(server, argument => argument.Equals("-unattended", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnlyARedirectedProcessIsToldTheBackendUrl()
    {
        var build = Cert();
        Assert.Contains("-CoreBackend=http://127.0.0.1:6000", LaunchArguments.Client(build, 6000, "Player"));
        Assert.Contains("-CoreBackend=http://127.0.0.1:6000", LaunchArguments.Server(build, 6000, "CoreServer1"));

        build.ServerDlls = [DllRole.Server];
        Assert.DoesNotContain("-CoreBackend", LaunchArguments.Server(build, 6000, "CoreServer1"));
    }

    [Fact]
    public void ClientArgumentsUseThePlayerAndBackendPort()
    {
        var arguments = Split("x.exe " + LaunchArguments.Client(Cert(), 6000, "Two Words")).Skip(1).ToList();
        Assert.Contains("-httpproxy=127.0.0.1:6000", arguments);
        Assert.Contains("-AUTH_LOGIN=Two Words", arguments);
        Assert.DoesNotContain("-nullrhi", arguments);
    }

    [Fact]
    public void ClientArgumentsUseDefaultCorePasswordWhenUnsetOrWhitespace()
    {
        var b = Cert();
        Assert.Contains($"-AUTH_PASSWORD={LaunchArguments.Password}", LaunchArguments.Client(b, 5595, "Player"));
        Assert.Contains($"-AUTH_PASSWORD={LaunchArguments.Password}", LaunchArguments.Client(b, 5595, "Player", ""));
        Assert.Contains($"-AUTH_PASSWORD={LaunchArguments.Password}", LaunchArguments.Client(b, 5595, "Player", "   "));
        Assert.Contains($"-AUTH_PASSWORD={LaunchArguments.Password}", LaunchArguments.Client(b, 5595, "Player", null));
        Assert.Equal("Core", LaunchArguments.Password);
    }

    [Fact]
    public void ClientArgumentsUseCustomPasswordWhenProvided()
    {
        var b = Cert();
        var arguments = Split("x.exe " + LaunchArguments.Client(b, 5595, "Player", "MyCustomSecret"));
        Assert.Contains("-AUTH_PASSWORD=MyCustomSecret", arguments);
        Assert.DoesNotContain($"-AUTH_PASSWORD={LaunchArguments.Password}", arguments);
    }

    [Fact]
    public void BuildsWithoutHttpProxyGetNone()
    {
        var build = Cert();
        build.UseHttpProxy = false;
        Assert.DoesNotContain("-httpproxy", LaunchArguments.Client(build, 5595, "Player"));
    }
}
