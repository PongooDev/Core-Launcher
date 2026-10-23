using System.Net;
using System.Net.Sockets;
using System.Text;
using Core.Launcher.Hosting;
using Core.Launcher.Hosting.Backend;
using Core.Launcher.Hosting.Builds;
using Core.Launcher.Hosting.Dlls;
using Core.Launcher.Hosting.Logging;
using Core.Launcher.Hosting.Native;
using Core.Launcher.Hosting.Processes;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Tests;

/// <summary>Playing on a friend's Core-Backend (Core-Launcher #2).</summary>
public sealed class RemoteBackendTests
{
    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7", 5595)]
    [InlineData(" 203.0.113.7:5600 ", "203.0.113.7", 5600)]
    [InlineData("http://203.0.113.7:5595/", "203.0.113.7", 5595)]
    [InlineData("http://My-House.DuckDNS.org:6000/admin", "my-house.duckdns.org", 6000)]
    [InlineData("friend-pc", "friend-pc", 5595)]
    [InlineData("[2001:db8::1]:5595", "2001:db8::1", 5595)]
    [InlineData("2001:db8::1", "2001:db8::1", 5595)]
    [InlineData("26.103.131.196", "26.103.131.196", 5595)]
    public void AFriendsAddressIsReadWithTheDefaultPort(string text, string host, int port)
    {
        Assert.True(BackendEndpoint.TryParse(text, out var endpoint, out var error), error);
        Assert.Equal(new BackendEndpoint(host, port), endpoint);
    }

    [Theory]
    [InlineData("", "Enter your friend's address")]
    [InlineData("https://203.0.113.7", "http://")]
    [InlineData("203.0.113.7:99999", "isn't a port")]
    [InlineData("203.0.113.7:port", "isn't a port")]
    [InlineData(":5595", "before the port")]
    [InlineData("1.2", "isn't an IP address")]
    [InlineData("1.2.3.999", "isn't a host name or IP address")]
    [InlineData("0.0.0.0", "another PC")]
    [InlineData("my house", "isn't a host name")]
    public void ABadAddressSaysWhatIsWrong(string text, string message)
    {
        Assert.False(BackendEndpoint.TryParse(text, out _, out var error));
        Assert.Contains(message, error);
    }

    [Fact]
    public void ClientsAreSentToTheFriendsBackend()
    {
        var build = KnownBuilds.Create(@"C:\Cert", new BuildVersion(4, 16, 0, KnownBuilds.Cert, "++Fortnite+Release-Cert"));
        var arguments = LaunchArguments.Client(build, new BackendEndpoint("203.0.113.7", 5600), "Friend");
        Assert.Contains("-httpproxy=203.0.113.7:5600", arguments);
        Assert.Contains("-CoreBackend=http://203.0.113.7:5600", arguments);
        Assert.Contains("-AUTH_LOGIN=Friend", arguments);
        Assert.DoesNotContain("127.0.0.1", arguments);

        var ipv6 = LaunchArguments.Client(build, new BackendEndpoint("2001:db8::1", 5595), "Friend");
        Assert.Contains("-httpproxy=[2001:db8::1]:5595", ipv6);
        Assert.Contains("-CoreBackend=http://[2001:db8::1]:5595", ipv6);

        // This PC's backend, as before.
        Assert.Contains("-CoreBackend=http://127.0.0.1:5595", LaunchArguments.Client(build, BackendEndpoint.Local(5595), "Player"));
    }

    [Fact]
    public async Task AFriendsBackendIsWatchedNotStarted()
    {
        using var folder = new TempFolder();
        var host = new LauncherHost(new LauncherPaths(folder.Combine("Launcher")));
        try
        {
            var settings = new BackendSettings { UseRemote = true, Remote = new RemoteBackend { Name = "Sam", Address = "203.0.113.7:5600" } };
            var friend = host.TargetFor(settings);
            Assert.False(friend.Start);
            Assert.True(friend.Remote);
            Assert.Null(friend.Problem);
            Assert.Equal(new BackendEndpoint("203.0.113.7", 5600), friend.Endpoint);
            Assert.Equal("Sam", friend.RemoteName);

            settings.Remote.Address = "";
            Assert.Contains("Enter your friend's backend address", host.TargetFor(settings).Problem);
            settings.Remote.Address = "https://x";
            Assert.Contains("http://", host.TargetFor(settings).Problem);

            // Turning it off goes back to this PC's backend as it was set up.
            settings.UseRemote = false;
            settings.Source = BackendSource.External;
            Assert.False(host.TargetFor(settings).Remote);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public void SavedFriendsRoundTrip()
    {
        using var folder = new TempFolder();
        var store = new SettingsStore(folder.Combine("settings.json"));
        var settings = new LauncherSettings();
        settings.Backend.UseRemote = true;
        settings.Backend.Remote = new RemoteBackend { Name = " Sam ", Address = " 203.0.113.7 " };
        settings.Backend.SavedRemotes.Add(new RemoteBackend { Name = "Sam", Address = "203.0.113.7" });
        store.Save(settings);

        var loaded = store.Load();
        Assert.True(loaded.Backend.UseRemote);
        Assert.Equal(("Sam", "203.0.113.7"), (loaded.Backend.Remote.Name, loaded.Backend.Remote.Address));
        Assert.Equal("Sam", Assert.Single(loaded.Backend.SavedRemotes).DisplayName);
        Assert.DoesNotContain("DisplayName", File.ReadAllText(store.FilePath));

        // Settings from before friends' backends have none.
        File.WriteAllText(store.FilePath, """{ "Version": 2, "Backend": { "Port": 5595 } }""");
        loaded = store.Load();
        Assert.False(loaded.Backend.UseRemote);
        Assert.Empty(loaded.Backend.SavedRemotes);
        Assert.Equal("", loaded.Backend.Remote.Address);
    }

    [Fact]
    public async Task TheCheckTellsCoreBackendFromAnythingElse()
    {
        var endpoint = new BackendEndpoint("203.0.113.7", 5595);
        const string url = "http://203.0.113.7:5595/core/api/status";

        var status = await CheckAsync(url, HttpStatusCode.OK, """{"name":"Core-Backend","version":"0.1.0"}""", endpoint);
        Assert.True(status.Reachable);
        Assert.Equal("0.1.0", status.Version);
        Assert.Contains("Core-Backend 0.1.0", status.Message);

        // A Core-Backend from before /core/api/status answers its unknown-route 404.
        status = await CheckAsync(url, HttpStatusCode.NotFound, """{"errorCode":"errors.com.epicgames.common.not_found","errorMessage":"Sorry"}""", endpoint);
        Assert.True(status.Reachable);
        Assert.Contains("older Core-Backend", status.Message);

        status = await CheckAsync(url, HttpStatusCode.OK, "<html>router login</html>", endpoint);
        Assert.False(status.Reachable);
        Assert.Contains("isn't Core-Backend", status.Message);
    }

    [Fact]
    public async Task NothingListeningSaysToTurnOnRemoteAccess()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        using var http = RemoteBackendCheck.CreateHttpClient();
        var status = await RemoteBackendCheck.CheckAsync(http, BackendEndpoint.Local(port), TestContext.Current.CancellationToken);
        Assert.False(status.Reachable);
        Assert.Contains("remote access", status.Message);
    }

    [Fact]
    public async Task TheBackendHostFollowsAFriendsBackend()
    {
        var routes = new StatusRoute();
        using var job = new JobObject();
        using var output = new LogChannel("Backend");
        using var log = new LogChannel("Launcher");
        var host = new BackendHost(job, output, log, new HttpClient(routes));
        host.Configure(BackendTarget.Friend(new BackendEndpoint("203.0.113.7", 5600), "Sam"));
        host.Start();
        Assert.True(await host.WaitReadyAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), string.Join('\n', log.Snapshot()));
        Assert.Equal(new BackendEndpoint("203.0.113.7", 5600), host.Endpoint);
        Assert.Null(host.AdminPanelUrl);
        Assert.Equal("0.1.0", host.RemoteStatus?.Version);
        Assert.Equal("http://203.0.113.7:5600/core/api/status", routes.LastUrl);
        await host.StopAsync();
        Assert.Equal(BackendState.Stopped, host.State);
        Assert.Null(host.Endpoint);

        routes.Down = true;
        host.Start();
        await WaitForAsync(() => host.State == BackendState.Unreachable);
        Assert.Contains("203.0.113.7:5600", host.Error);
        Assert.False(await host.WaitReadyAsync(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken));
        await host.StopAsync();

        // A bad address fails at once with what to fix.
        host.Configure(BackendTarget.FriendProblem("Enter your friend's backend address", "Sam"));
        host.Start();
        await WaitForAsync(() => host.State == BackendState.Failed);
        Assert.Equal("Enter your friend's backend address", host.Error);
        await host.DisposeAsync();
    }

    private static async Task<RemoteBackendStatus> CheckAsync(string url, HttpStatusCode code, string body, BackendEndpoint endpoint)
    {
        using var http = new HttpClient(new StatusRoute { Url = url, Code = code, Body = body });
        return await RemoteBackendCheck.CheckAsync(http, endpoint, TestContext.Current.CancellationToken);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class StatusRoute : HttpMessageHandler
    {
        public string Url { get; init; } = "http://203.0.113.7:5600/core/api/status";

        public HttpStatusCode Code { get; init; } = HttpStatusCode.OK;

        public string Body { get; init; } = """{"name":"Core-Backend","version":"0.1.0"}""";

        public volatile bool Down;

        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri!.ToString();
            if (Down)
            {
                throw new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused));
            }

            return Task.FromResult(LastUrl == Url
                ? new HttpResponseMessage(Code) { Content = new StringContent(Body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
