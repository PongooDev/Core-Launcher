using System.Runtime.InteropServices;
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

    private static BuildSettings Cert() => new()
    {
        Name = "Cert CL-3532353",
        Path = @"F:\Builds\++Fortnite+Cert-CL-3532353\Fortnite",
        ClientArguments = "-epicapp=Fortnite -epicenv=Prod -epicportal -SkipPatchCheck -FORCELOGFLUSH",
        ServerArguments = "-epicapp=Fortnite -epicenv=Prod -epicportal -SkipPatchCheck -FORCELOGFLUSH -nullrhi -nosplash -nosound",
        UseHttpProxy = true,
        ClientDlls = [DllRole.Redirect, DllRole.Client],
        ServerDlls = [DllRole.Server],
    };

    [Fact]
    public void ServerArgumentsHaveEveryRunServerSwitch()
    {
        var arguments = LaunchArguments.Server(Cert(), 5595, "CoreServer");

        // Core-Docs/Tools/run_server.py ARGS
        string[] runServer =
        [
            "-epicapp=Fortnite", "-epicenv=Prod", "-epicportal", "-httpproxy=127.0.0.1:5595", "-AUTH_LOGIN=CoreServer",
            "-AUTH_PASSWORD=Core", "-AUTH_TYPE=epic", "-SkipPatchCheck", "-FORCELOGFLUSH", "-nullrhi", "-nosplash", "-nosound",
        ];
        Assert.Equal(runServer.Order(), Split("x.exe " + arguments).Skip(1).Order());
    }

    [Fact]
    public void ARedirectedProcessIsToldTheBackendUrl()
    {
        var build = Cert();
        Assert.Contains("-CoreBackend=http://127.0.0.1:6000", LaunchArguments.Client(build, 6000, "Player"));
        Assert.DoesNotContain("-CoreBackend", LaunchArguments.Server(build, 6000, "CoreServer1"));

        build.ServerDlls = [DllRole.Redirect, DllRole.Server];
        Assert.Contains("-CoreBackend=http://127.0.0.1:6000", LaunchArguments.Server(build, 6000, "CoreServer1"));
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
    public void BuildsWithoutHttpProxyGetNone()
    {
        var build = Cert();
        build.UseHttpProxy = false;
        Assert.DoesNotContain("-httpproxy", LaunchArguments.Client(build, 5595, "Player"));
    }
}
