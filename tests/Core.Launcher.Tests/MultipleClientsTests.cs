using System.Net;
using System.Net.Sockets;
using Core.Launcher.Hosting;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Tests;

public sealed class MultipleClientsTests
{
    [Fact]
    public async Task RunsSeveralClientsWithTheirOwnLogins()
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
                Name = "Stand-in",
                Path = Environment.SystemDirectory,
                Executable = "cmd.exe",
                ClientArguments = "/c ping -n 60 127.0.0.1 >nul &rem",
                UseHttpProxy = true,
            });
            host.SaveSettings(settings);

            await host.PlayAsync();
            await host.PlayAsync("Alt");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.PlayAsync("tester"));
            Assert.Equal("tester is already in the game", error.Message);

            Assert.Equal(["Tester", "Alt"], host.Clients.Select(client => client.Login));
            Assert.NotEqual(host.Clients[0].Game.Id, host.Clients[1].Game.Id);
            Assert.NotSame(host.ClientLog("Tester"), host.ClientLog("Alt"));
            Assert.True(host.GamesRunning);

            var alt = host.ClientFor("alt")!;
            host.StopClient("Alt");
            await alt.Game.Finished.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.Equal(["Tester"], host.Clients.Select(client => client.Login));
        }
        finally
        {
            await host.DisposeAsync();
            listener.Stop();
        }

        Assert.Empty(host.Clients);
    }
}
