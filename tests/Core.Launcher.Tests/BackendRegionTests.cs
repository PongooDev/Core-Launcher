using System.Net;
using System.Text;
using Core.Launcher.Hosting.Backend;
using Microsoft.Extensions.Time.Testing;

namespace Core.Launcher.Tests;

/// <summary>The hosted servers' region comes from the backend's Server Regions (Core-Launcher #8, Core-Backend #142).</summary>
public sealed class BackendRegionTests
{
    private static readonly BackendEndpoint Local = BackendEndpoint.Local(5595);
    private static readonly BackendEndpoint Friend = new("203.0.113.7", 5600);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = [];

    private BackendRegion Region() => new(new HttpClient(new Handler(_routes)), _time);

    private void Status(BackendEndpoint backend, string json)
    {
        lock (_routes)
        {
            _routes[backend.Url + BackendRegion.StatusPath] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private async Task<string?> ReadAsync(BackendRegion region, BackendEndpoint backend)
    {
        await region.RefreshAsync(backend, TestContext.Current.CancellationToken);
        return region.Region(backend);
    }

    [Fact]
    public async Task ServersTakeTheFirstServerRegion()
    {
        using var region = Region();
        Status(Local, """{"name":"Core-Backend","version":"1.0.0","serverRegions":["EU","NA"]}""");
        Assert.Equal("EU", await ReadAsync(region, Local));

        // Codes the client's Matchmaking Region list doesn't offer are skipped.
        Status(Local, """{"name":"Core-Backend","version":"1.0.0","serverRegions":["MARS"," oce "]}""");
        Assert.Equal("OCE", await ReadAsync(region, Local));
    }

    [Fact]
    public async Task NoServerRegionsOrAnOlderBackendGiveNa()
    {
        using var region = Region();
        Status(Local, """{"name":"Core-Backend","version":"1.0.0","serverRegions":[]}""");
        Assert.Equal("NA", await ReadAsync(region, Local));

        Status(Local, """{"name":"Core-Backend","version":"0.9.0"}""");
        Assert.Equal("NA", await ReadAsync(region, Local));

        // An older Core-Backend without the status route answers 404.
        _routes.Clear();
        Assert.Equal("NA", await ReadAsync(region, Local));
    }

    [Fact]
    public async Task AnUnreachableBackendKeepsTheRegionItLastGave()
    {
        using var region = Region();
        Status(Friend, """{"name":"Core-Backend","version":"1.0.0","serverRegions":["BR"]}""");
        Assert.Equal("BR", await ReadAsync(region, Friend));

        _routes[Friend.Url + BackendRegion.StatusPath] = () => throw new HttpRequestException("No connection could be made");
        Assert.Equal("BR", await ReadAsync(region, Friend));

        // Never reached at all: NA.
        Assert.Equal("NA", await ReadAsync(region, new BackendEndpoint("203.0.113.8", 5600)));
    }

    [Fact]
    public async Task AFriendsBackendIsAskedForItsOwnRegions()
    {
        using var region = Region();
        Status(Local, """{"name":"Core-Backend","version":"1.0.0","serverRegions":["NA"]}""");
        Status(Friend, """{"name":"Core-Backend","version":"1.0.0","serverRegions":["ASIA","EU"]}""");
        Assert.Equal("NA", await ReadAsync(region, Local));

        // Not asked yet: unknown until the read the first look starts.
        Assert.Null(region.Region(Friend));
        await WaitForAsync(() => region.Region(Friend) == "ASIA");

        // A change on the admin panel is read again once the last read is old.
        Status(Friend, """{"name":"Core-Backend","version":"1.0.0","serverRegions":["EU"]}""");
        Assert.Equal("ASIA", region.Region(Friend));
        _time.Advance(BackendRegion.PollInterval);
        await WaitForAsync(() => region.Region(Friend) == "EU");
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

    private sealed class Handler(Dictionary<string, Func<HttpResponseMessage>> routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Func<HttpResponseMessage>? respond;
            lock (routes)
            {
                routes.TryGetValue(request.RequestUri!.ToString(), out respond);
            }

            return Task.FromResult(respond?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
