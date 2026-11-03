using System.Net;
using System.Text;
using Core.Launcher.Hosting.Backend;
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

    [Fact]
    public async Task AFriendsBackendIsAskedAtItsAddressAndARefusalIsShown()
    {
        var friend = new BackendEndpoint("203.0.113.7", 5600);
        const string friendUrl = "http://203.0.113.7:5600/core/api/sessions";
        using var sessions = Sessions();

        // A backend that doesn't trust this PC's IP answers 403 (Core-Backend #125). Not asked yet: the first look starts a read.
        _routes[friendUrl] = () => new HttpResponseMessage(HttpStatusCode.Forbidden);
        Assert.Equal(HostingAccess.Unknown, sessions.Access(friend));
        await WaitForAsync(() => sessions.Access(friend) == HostingAccess.Refused);

        // Once it does, its listing is read, and this PC's servers are found in it by their own login.
        _routes[friendUrl] = () => Json($"[{Session("CoreServer1", 7777, Started.AddSeconds(40))},{Session("CoreServer-DESKTOP7-1", 7777, Started.AddSeconds(40))}]");
        _time.Advance(BackendSessions.PollInterval);
        await WaitForAsync(() => sessions.Access(friend) == HostingAccess.Allowed);
        Assert.Equal(SessionCheck.Listed, sessions.Check(friend, "CoreServer-DESKTOP7-1", Started, [7777]));
        Assert.Equal(SessionCheck.Pending, sessions.Check(friend, "CoreServer-DESKTOP7-2", Started, [7777]));

        // A listing from another backend isn't used.
        Assert.Equal(SessionCheck.Pending, sessions.Check(Port, "CoreServer1", Started, [7777]));
    }

    [Fact]
    public async Task AnUnreachableFriendIsNotARefusal()
    {
        var friend = new BackendEndpoint("203.0.113.7", 5595);
        _routes["http://203.0.113.7:5595/core/api/sessions"] = () => throw new HttpRequestException("No connection could be made");
        using var sessions = Sessions();
        await sessions.RefreshAsync(friend, TestContext.Current.CancellationToken);
        Assert.Equal(HostingAccess.Unknown, sessions.Access(friend));
    }

    // Core-Launcher #10: each server's card shows how many players its session has.
    private static string WithPlayers(string session, int current, int max) =>
        session.Replace("\"currentPlayers\":0,\"maxPlayers\":4", $"\"currentPlayers\":{current},\"maxPlayers\":{max}");

    private async Task<SessionPlayers?> PlayersAsync(string login, params int[] ports)
    {
        using var sessions = Sessions();
        await sessions.RefreshAsync(Port, TestContext.Current.CancellationToken);
        return sessions.Players(BackendEndpoint.Local(Port), login, ports);
    }

    [Fact]
    public async Task EachServersPlayersAreReadFromItsSession()
    {
        List(WithPlayers(Session("CoreServer1", 7777, Started.AddSeconds(40)), 3, 100), Session("CoreServer2", 7778, Started.AddSeconds(40)));
        Assert.Equal(new SessionPlayers(3, 100), await PlayersAsync("coreserver1", 7777));
        Assert.Equal(new SessionPlayers(0, 4), await PlayersAsync("CoreServer2", 7778));
        Assert.Null(await PlayersAsync("CoreServer3", 7779));
    }

    [Fact]
    public async Task AServersNewestSessionGivesItsPlayers()
    {
        List(WithPlayers(Session("CoreServer1", 7777, Started.AddSeconds(-30)), 2, 4), WithPlayers(Session("CoreServer1", 7777, Started.AddSeconds(40)), 1, 4));
        Assert.Equal(new SessionPlayers(1, 4), await PlayersAsync("CoreServer1", 7777));
    }

    [Fact]
    public async Task AnOlderBackendWithoutPlayerCountsShowsNone()
    {
        List("""{"ownerName":"CoreServer1","serverPort":7777,"lastUpdated":"2026-10-22T12:00:40.000Z"}""");
        Assert.Null(await PlayersAsync("CoreServer1", 7777));

        // No listing at all (404), or an unreachable backend.
        _routes[Url] = () => new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert.Null(await PlayersAsync("CoreServer1", 7777));
        _routes[Url] = () => throw new HttpRequestException("No connection could be made");
        Assert.Null(await PlayersAsync("CoreServer1", 7777));
    }

    [Theory]
    [InlineData("""{"currentPlayers":3,"maxPlayers":100}""", 3, 100)]
    [InlineData("""{"currentPlayers":0,"maxPlayers":4}""", 0, 4)]
    [InlineData("""{"currentPlayers":2}""", 2, 0)]
    [InlineData("""{"currentPlayers":2,"maxPlayers":"lots"}""", 2, 0)]
    public void PlayerCountsAreParsed(string json, int current, int max)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(new SessionPlayers(current, max), BackendSessions.ParsePlayers(document.RootElement));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"maxPlayers":4}""")]
    [InlineData("""{"currentPlayers":"3","maxPlayers":4}""")]
    [InlineData("""{"currentPlayers":-1,"maxPlayers":4}""")]
    [InlineData("""{"currentPlayers":1.5}""")]
    [InlineData("""[]""")]
    public void MissingOrBadPlayerCountsAreNull(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Null(BackendSessions.ParsePlayers(document.RootElement));
    }

    [Theory]
    [InlineData(0, 100, "Empty")]
    [InlineData(0, 0, "Empty")]
    [InlineData(3, 100, "3 / 100 players")]
    [InlineData(1, 4, "1 / 4 players")]
    [InlineData(1, 0, "1 player")]
    [InlineData(5, 0, "5 players")]
    public void PlayerCountsAreDescribed(int current, int max, string text) =>
        Assert.Equal(text, new SessionPlayers(current, max).Describe());

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
