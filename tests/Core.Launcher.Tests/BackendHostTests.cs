using System.Net.Sockets;
using Core.Launcher.Hosting.Backend;
using Core.Launcher.Hosting.Logging;
using Core.Launcher.Hosting.Native;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Tests;

public sealed class BackendHostTests
{
    [Fact]
    public void ValidateExplainsWhatIsMissing()
    {
        using var folder = new TempFolder();
        Assert.Contains("isn't set", BackendHost.Validate(new BackendSettings()));
        Assert.Contains("no src\\main.js", BackendHost.Validate(new BackendSettings { Folder = folder.Path }));

        Directory.CreateDirectory(folder.Combine("src"));
        File.WriteAllText(folder.Combine("src", "main.js"), "");
        Assert.Contains("run npm install", BackendHost.Validate(new BackendSettings { Folder = folder.Path }));

        Directory.CreateDirectory(folder.Combine("node_modules"));
        Assert.Null(BackendHost.Validate(new BackendSettings { Folder = folder.Path }));
    }

    [Fact]
    public void DataFolderDefaultsToTheBackendsOwn()
    {
        Assert.Equal(@"C:\Core-Backend\data", new BackendSettings { Folder = @"C:\Core-Backend" }.ResolvedDataFolder);
        Assert.Equal(@"D:\Data", new BackendSettings { Folder = @"C:\Core-Backend", DataFolder = @"D:\Data" }.ResolvedDataFolder);
    }

    /// <summary>Runs the real Core-Backend from CORE_BACKEND_DIR on a free port with a throwaway data folder.</summary>
    [Fact]
    public async Task StartsTheRealBackendAndStopsIt()
    {
        var backendFolder = Environment.GetEnvironmentVariable("CORE_BACKEND_DIR");
        if (string.IsNullOrEmpty(backendFolder))
        {
            Assert.Skip("Set CORE_BACKEND_DIR to a Core-Backend checkout to run this");
        }

        using var data = new TempFolder();
        using var job = new JobObject();
        using var output = new LogChannel("Backend");
        using var log = new LogChannel("Launcher");
        var host = new BackendHost(job, output, log);
        host.Configure(new BackendSettings { Folder = backendFolder, Port = 0, DataFolder = data.Path });

        host.Start();
        Assert.True(await host.WaitReadyAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), string.Join('\n', log.Snapshot().Concat(output.Snapshot())));
        Assert.True(host.Port > 0);
        Assert.NotNull(host.ProcessId);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync("127.0.0.1", host.Port!.Value, TestContext.Current.CancellationToken);
        }

        await host.StopAsync();
        Assert.Equal(BackendState.Stopped, host.State);
        Assert.Null(host.ProcessId);
        TestContext.Current.SendDiagnosticMessage(string.Join('\n', log.Snapshot()));
    }
}
