using System.Net.Sockets;
using Core.Launcher.Hosting;
using Core.Launcher.Hosting.Backend;
using Core.Launcher.Hosting.Logging;
using Core.Launcher.Hosting.Native;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Tests;

public sealed class BackendHostTests
{
    private static BackendTarget Folder(string folder) => new(true, "node", folder, Path.Combine(folder, "data"), 5595);

    [Fact]
    public void ValidateExplainsWhatIsMissing()
    {
        using var folder = new TempFolder();
        Assert.Contains("isn't set", BackendHost.Validate(Folder("")));
        Assert.Contains("no src\\main.js", BackendHost.Validate(Folder(folder.Path)));

        Directory.CreateDirectory(folder.Combine("src"));
        File.WriteAllText(folder.Combine("src", "main.js"), "");
        Assert.Contains("run npm install", BackendHost.Validate(Folder(folder.Path)));

        Directory.CreateDirectory(folder.Combine("node_modules"));
        Assert.Null(BackendHost.Validate(Folder(folder.Path)));
        Assert.Equal("not installed", BackendHost.Validate(Folder(folder.Path) with { Problem = "not installed" }));
    }

    [Fact]
    public async Task TargetsFollowTheBackendSource()
    {
        using var folder = new TempFolder();
        var host = new LauncherHost(new LauncherPaths(folder.Combine("Launcher")));
        try
        {
            var external = host.TargetFor(new BackendSettings { Source = BackendSource.External, Port = 6000 });
            Assert.False(external.Start);
            Assert.Equal(6000, external.Port);

            var local = host.TargetFor(new BackendSettings { Source = BackendSource.Folder, Folder = @"C:\Core-Backend", Node = @"C:\node\node.exe" });
            Assert.Equal((true, @"C:\node\node.exe", @"C:\Core-Backend", @"C:\Core-Backend\data"), (local.Start, local.Node, local.Folder, local.DataFolder));
            Assert.Equal(@"D:\Data", host.TargetFor(new BackendSettings { Source = BackendSource.Folder, Folder = @"C:\Core-Backend", DataFolder = @"D:\Data" }).DataFolder);

            var managed = host.TargetFor(new BackendSettings());
            Assert.Contains("isn't installed yet", managed.Problem);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheAdminPanelFollowsABackendThatIsAlreadyRunning()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var job = new JobObject();
        using var output = new LogChannel("Backend");
        using var log = new LogChannel("Launcher");
        var host = new BackendHost(job, output, log);
        Assert.Null(host.AdminPanelUrl);

        host.Configure(BackendTarget.Running(port));
        host.Start();
        Assert.True(await host.WaitReadyAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal($"http://127.0.0.1:{port}/admin", host.AdminPanelUrl);
        Assert.Equal($"http://127.0.0.1:{port}/register", host.RegisterUrl);
        Assert.Equal($"http://127.0.0.1:{port}/portal", host.PortalUrl);

        listener.Stop();
        await host.StopAsync();
        Assert.Null(host.AdminPanelUrl);
        Assert.Null(host.RegisterUrl);
        Assert.Null(host.PortalUrl);
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
        host.Configure(new BackendTarget(true, "node", backendFolder, data.Path, 0));

        host.Start();
        Assert.True(await host.WaitReadyAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), string.Join('\n', log.Snapshot().Concat(output.Snapshot())));
        Assert.True(host.Port > 0);
        Assert.NotNull(host.ProcessId);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync("127.0.0.1", host.Port!.Value, TestContext.Current.CancellationToken);
        }

        Assert.Equal($"http://127.0.0.1:{host.Port}/admin", host.AdminPanelUrl);
        using (var http = new HttpClient())
        {
            using var panel = await http.GetAsync(host.AdminPanelUrl, TestContext.Current.CancellationToken);
            Assert.True(panel.IsSuccessStatusCode, $"GET {host.AdminPanelUrl} answered {(int)panel.StatusCode}");
        }

        // What a friend's launcher checks before playing on it.
        using (var http = RemoteBackendCheck.CreateHttpClient())
        {
            var status = await RemoteBackendCheck.CheckAsync(http, BackendEndpoint.Local(host.Port!.Value), TestContext.Current.CancellationToken);
            Assert.True(status.Reachable, status.Message);
            Assert.NotNull(status.Version);
        }

        await host.StopAsync();
        Assert.Equal(BackendState.Stopped, host.State);
        Assert.Null(host.ProcessId);
        TestContext.Current.SendDiagnosticMessage(string.Join('\n', log.Snapshot()));
    }
}
