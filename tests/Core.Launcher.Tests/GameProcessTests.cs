using Core.Launcher.Hosting.Dlls;
using Core.Launcher.Hosting.Native;
using Core.Launcher.Hosting.Processes;

namespace Core.Launcher.Tests;

public sealed class GameProcessTests
{
    private static readonly string System32 = Environment.SystemDirectory;
    private static readonly string Cmd = Path.Combine(System32, "cmd.exe");

    // A DLL cmd.exe never loads on its own.
    private static readonly string Probe = Path.Combine(System32, "msftedit.dll");

    private static GameLaunch Waiting(params string[] dlls) => new(Cmd, "/c ping -n 30 127.0.0.1 >nul", dlls, StartMinimized: true);

    [Fact]
    public async Task QueuedDllIsLoadedIntoTheNewProcess()
    {
        using var job = new JobObject();

        using (var control = GameProcess.Start(Waiting(), job))
        {
            Assert.Equal([Probe], await control.WaitForModulesAsync([Probe], TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            control.Terminate();
        }

        using var injected = GameProcess.Start(Waiting(Probe), job);
        Assert.Empty(await injected.WaitForModulesAsync([Probe], TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        injected.Terminate();
        Assert.Equal(1u, await injected.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClosingTheJobKillsItsProcesses()
    {
        var job = new JobObject();
        using var process = GameProcess.Start(Waiting(), job);
        Assert.False(process.Exited.IsCompleted);

        job.Dispose();
        await process.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void AMissingExecutableThrows()
    {
        using var job = new JobObject();
        Assert.ThrowsAny<System.ComponentModel.Win32Exception>(() => GameProcess.Start(new GameLaunch(Path.Combine(System32, "missing-game.exe"), "", [], false), job));
    }

    [Fact]
    public void PeCheckAcceptsOnlyX64ImagesOfTheRightKind()
    {
        Assert.Null(PeImage.CheckX64(Path.Combine(System32, "kernel32.dll"), dll: true));
        Assert.Null(PeImage.CheckX64(Cmd, dll: false));
        Assert.Contains("isn't a DLL", PeImage.CheckX64(Cmd, dll: true));
        Assert.Contains("is a DLL", PeImage.CheckX64(Path.Combine(System32, "kernel32.dll"), dll: false));
        Assert.Contains("isn't x64 (machine 0x014C)", PeImage.CheckX64(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "kernel32.dll"), dll: true));
        Assert.Contains("doesn't exist", PeImage.CheckX64(Path.Combine(System32, "missing.dll"), dll: true));

        using var folder = new TempFolder();
        File.WriteAllText(folder.Combine("Core.dll"), "not a DLL");
        Assert.Contains("isn't a Windows image", PeImage.CheckX64(folder.Combine("Core.dll"), dll: true));
    }
}
