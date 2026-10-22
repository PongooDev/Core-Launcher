using System.Net;
using System.Text;
using Core.Launcher.Hosting.Servers;
using Microsoft.Extensions.Time.Testing;

namespace Core.Launcher.Tests;

public sealed class BackendSessionsTests
{
    private const int Port = 5595;
    private const string Url = "http://127.0.0.1:5595/core/api/sessions";

    private static readonly DateTimeOffset Started = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Started + TimeSpan.FromSeconds(60));
    private readonly Routes _routes = new();

    private BackendSessions Sessions() => new(new HttpClient(_routes), _time);

    // GET /core/api/sessions answers Core-Backend's sessionSummary for each live session.
    private static string Session(string ownerName, int serverPort, DateTimeOffset lastUpdated) =>
        $$"""{"id":"0a1b","ownerId":"","ownerName":"{{ownerName}}","serverName":"","serverAddress":"127.0.0.1","serverPort":{{serverPort}},"gameMode":"","region":"NAE","beaconPort":15000,"publicPlayers":[],"privatePlayers":[],"currentPlayers":0,"maxPlayers":4,"started":false,"lastUpdated":"{{lastUpdated.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}}"}""";

    private void List(params string[] sessions) => _routes[Url] = () => Json($"[{string.Join(",", sessions)}]");

    private async Task<SessionCheck> CheckAsync(string login = "CoreServer1", params int[] ports)
    {
        using var sessions = Sessions();
        await sessions.RefreshAsync(Port, TestContext.Current.CancellationToken);
        return sessions.Check(Port, login, Started, ports);
    }

    [Fact]
    public async Task ASessionOwnedByTheServersAccountIsListed()
    {
        List(Session("CoreServer2", 7778, Started.AddSeconds(40)), Session("coreserver1", 7777, Started.AddSeconds(50)));
        Assert.Equal(SessionCheck.Listed, await CheckAsync("CoreServer1", 7777));
    }

    [Fact]
    public async Task AnotherServersSessionIsNotListed()
    {
        List(Session("CoreServer2", 7777, Started.AddSeconds(40)));
        Assert.Equal(SessionCheck.Pending, await CheckAsync("CoreServer1", 7777));
    }

    [Fact]
    public async Task ASessionLeftByAnEarlierRunDoesNotCount()
    {
        List(Session("CoreServer1", 7777, Started.AddSeconds(-30)));
        Assert.Equal(SessionCheck.Pending, await CheckAsync("CoreServer1", 7777));
    }

    [Fact]
    public async Task ASessionWithoutAnOwnerMatchesOnTheServersPort()
    {
        List(Session("", 7777, Started.AddSeconds(40)));
        Assert.Equal(SessionCheck.Listed, await CheckAsync("CoreServer1", 7777));
        Assert.Equal(SessionCheck.Pending, await CheckAsync("CoreServer1", 7778));
        Assert.Equal(SessionCheck.Pending, await CheckAsync("CoreServer1"));
    }

    [Fact]
    public async Task NoSessionsYetIsPending()
    {
        List();
        Assert.Equal(SessionCheck.Pending, await CheckAsync("CoreServer1", 7777));
    }

    [Fact]
    public async Task ABackendWithoutTheListingIsUnsupported()
    {
        Assert.Equal(SessionCheck.Unsupported, await CheckAsync("CoreServer1", 7777));

        _routes[Url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>not Core</html>") };
        Assert.Equal(SessionCheck.Unsupported, await CheckAsync("CoreServer1", 7777));
    }

    [Fact]
    public async Task AnUnreachableBackendIsPending()
    {
        _routes[Url] = () => throw new HttpRequestException("No connection could be made");
        Assert.Equal(SessionCheck.Pending, await CheckAsync("CoreServer1", 7777));

        _routes[Url] = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        Assert.Equal(SessionCheck.Pending, await CheckAsync("CoreServer1", 7777));
    }

    [Fact]
    public async Task CheckReadsInTheBackgroundAndAnswersFromTheLatestListing()
    {
        List();
        using var sessions = Sessions();

        // Nothing read yet: the first check starts a read.
        Assert.Equal(SessionCheck.Pending, sessions.Check(Port, "CoreServer1", Started, [7777]));
        await _routes.WaitForRequestAsync(1);

        List(Session("CoreServer1", 7777, Started.AddSeconds(40)));

        // Within the poll interval the listing read before is used, without asking again.
        Assert.Equal(SessionCheck.Pending, sessions.Check(Port, "CoreServer1", Started, [7777]));
        Assert.Equal(1, _routes.Requests);

        _time.Advance(BackendSessions.PollInterval);
        await WaitForAsync(() => sessions.Check(Port, "CoreServer1", Started, [7777]) == SessionCheck.Listed);
        Assert.Equal(2, _routes.Requests);

        // A listing from another backend port isn't used.
        Assert.Equal(SessionCheck.Pending, sessions.Check(6000, "CoreServer1", Started, [7777]));
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

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Routes : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = [];
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public Func<HttpResponseMessage> this[string url]
        {
            set
            {
                lock (_routes)
                {
                    _routes[url] = value;
                }
            }
        }

        public async Task WaitForRequestAsync(int count) => await WaitForAsync(() => Requests >= count);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Func<HttpResponseMessage>? respond;
            lock (_routes)
            {
                _routes.TryGetValue(request.RequestUri!.ToString(), out respond);
            }

            try
            {
                return Task.FromResult(respond?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            finally
            {
                Interlocked.Increment(ref _requests);
            }
        }
    }
}
