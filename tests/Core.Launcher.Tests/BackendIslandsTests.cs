using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Core.Launcher.Hosting;
using Core.Launcher.Hosting.Backend;
using Core.Launcher.Hosting.Logging;
using Core.Launcher.Hosting.Native;
using Core.Launcher.Hosting.Processes;

namespace Core.Launcher.Tests;

/// <summary>The Islands page's client of Core-Backend's island files (Core-Launcher #21).</summary>
public sealed class BackendIslandsTests
{
    private const string Token = "player-token";
    private static readonly BackendEndpoint Backend = BackendEndpoint.Local(6799);

    // Core-Backend's own answer (GET /core/api/creative/islands?build=4727874), cut to two islands and two slot rows.
    private const string ListAnswer = """
        {"accountId":"f8d7dcde9a0d7d7381b855fb86b180cb","displayName":"E2EBuilder","build":"4727874","buildVersion":"7.20","maxFileBytes":22522540,
         "islands":[{"plotItemId":"7ba312b9-c1f0-4d3f-a376-c77e69ab83f4","templateId":"CreativePlot:temperate_medium","islandIndex":1,"name":"Island 1",
           "islandName":"","title":"Box Fights","published":"1234-5678-9012","saved":true,"size":255,"updated":"2026-11-19T16:24:54.733Z","inUse":false,"free":false,
           "build":{"changelist":"4727874","version":"7.20","from":"save"},"imported":{"at":"2026-11-19T16:24:54.733Z"},"loadsOnBuild":true},
          {"plotItemId":"420272e1-8475-4aac-b0e8-c060c46d2617","templateId":"CreativePlot:temperate_medium","islandIndex":2,"name":"Island 2","islandName":"",
           "title":"","published":null,"saved":false,"size":0,"updated":null,"inUse":false,"free":true,"build":null,"imported":null,"loadsOnBuild":null}],
         "slots":[{"templateId":"CreativePlot:temperate_medium","name":"Island","limit":4,"islands":4,"free":3,"firstBuild":"4629139","firstVersion":"7.00"},
          {"templateId":"CreativePlot:arctic_competitive_medium1","name":"Frosty Fortress","limit":1,"islands":1,"free":1,"firstBuild":"4727874","firstVersion":"7.20"}]}
        """;

    private const string IslandFileText = """
        {"format":"core-island","formatVersion":1,"exportedAt":"2026-11-19T16:24:54.841Z","build":{"changelist":"4727874","version":"7.20","from":"save"},
         "island":{"type":"CreativePlot:temperate_medium","name":"","title":"Box Fights","description":"1v1","meta":{}},
         "save":{"compression":"gzip","size":3,"sha256":"039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81","data":"H4sIAAAAAAAACmNkYgYAHblCfQMAAAA="}}
        """;

    [Fact]
    public async Task SignInKeepsTheAccessTokenAndItsLifetime()
    {
        var handler = new IslandsHandler(_ => Json(HttpStatusCode.OK, """{"access_token":"abc","expires_in":7200,"token_type":"bearer"}"""));
        using var http = new HttpClient(handler);
        var result = await BackendAuthClient.SignInAsync(http, Backend, "Player", "Core", TestContext.Current.CancellationToken);
        Assert.True(result.Result.Succeeded);
        Assert.Equal("abc", result.AccessToken);
        Assert.NotNull(result.ExpiresAt);
        Assert.InRange(result.ExpiresAt!.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(119), TimeSpan.FromMinutes(121));
        // As the Play button's check: the launcher's client id with the password grant.
        var sent = handler.Requests.Single();
        Assert.Equal(BackendAuthClient.OAuthTokenPath, sent.Path);
        Assert.Contains("grant_type=password", sent.Body);
        Assert.Equal("Basic", sent.Authorization?.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{LaunchArguments.DedicatedServerClientId}:{LaunchArguments.DedicatedServerClientSecret}")), sent.Authorization?.Parameter);
        // And Play's own check is unchanged.
        Assert.True((await BackendAuthClient.AuthenticateAsync(http, Backend, "Player", "Core", TestContext.Current.CancellationToken)).Succeeded);
    }

    [Fact]
    public async Task SignInFailureKeepsTheFriendlyMessage()
    {
        using var http = new HttpClient(new IslandsHandler(_ => Json(HttpStatusCode.BadRequest,
            """{"errorCode":"errors.com.epicgames.account.invalid_account_credentials","errorMessage":"This account does not have a password configured and is admin-managed. Please request a Password Reset Link from your host."}""")));
        var result = await BackendAuthClient.SignInAsync(http, Backend, "Legacy", "Core", TestContext.Current.CancellationToken);
        Assert.False(result.Result.Succeeded);
        Assert.Null(result.AccessToken);
        Assert.Contains("Password Reset Link", result.Result.ErrorMessage);
    }

    [Fact]
    public async Task ListSendsThePlayersTokenAndBuildAndReadsTheIslands()
    {
        var handler = new IslandsHandler(_ => Json(HttpStatusCode.OK, ListAnswer));
        using var http = new HttpClient(handler);
        var list = await BackendIslands.ListAsync(http, Backend, Token, 4727874, TestContext.Current.CancellationToken);

        var sent = handler.Requests.Single();
        Assert.Equal("GET", sent.Method);
        Assert.Equal("/core/api/creative/islands?build=4727874", sent.PathAndQuery);
        Assert.Equal(("bearer", Token), (sent.Authorization?.Scheme, sent.Authorization?.Parameter));

        Assert.Equal("E2EBuilder", list.DisplayName);
        Assert.Equal(22_522_540, list.MaxFileBytes);
        Assert.Equal(2, list.Islands.Count);
        var built = list.Islands[0];
        Assert.Equal(("Island 1", "Box Fights", "1234-5678-9012", true, 255L, "7.20", true, true),
            (built.Name, built.Title, built.Published, built.Saved, built.Size, built.BuildVersion, built.Imported, built.LoadsOnBuild));
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 16, 24, 54, 733, TimeSpan.Zero), built.Updated);
        var free = list.Islands[1];
        Assert.Equal((false, true, null, false, (bool?)null), (free.Saved, free.Free, free.BuildVersion, free.Imported, free.LoadsOnBuild));
        Assert.Equal(new IslandSlots("CreativePlot:temperate_medium", "Island", 4, 4, 3, "7.00"), list.Slots[0]);
        Assert.Equal("Frosty Fortress", list.Slots[1].Name);
    }

    [Fact]
    public void UrlsNameTheBuildOnlyWhenThereIsOne()
    {
        Assert.Equal("http://127.0.0.1:6799/core/api/creative/islands", BackendIslands.ListUrl(Backend, 0));
        Assert.Equal("http://127.0.0.1:6799/core/api/creative/islands/import?build=4834550", BackendIslands.ImportUrl(Backend, 4834550));
        Assert.Equal("http://127.0.0.1:6799/core/api/creative/islands/a%2Fb/export", BackendIslands.ExportUrl(Backend, "a/b"));
    }

    [Fact]
    public async Task ExportReturnsTheFileAndItsSuggestedName()
    {
        var handler = new IslandsHandler(_ =>
        {
            var response = Json(HttpStatusCode.OK, IslandFileText);
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "Island-1-Box-Fights.coreisland" };
            return response;
        });
        using var http = new HttpClient(handler);
        var file = await BackendIslands.ExportAsync(http, Backend, Token, "7ba312b9-c1f0-4d3f-a376-c77e69ab83f4", TestContext.Current.CancellationToken);
        Assert.Equal("Island-1-Box-Fights.coreisland", file.FileName);
        Assert.Equal(IslandFileText, Encoding.UTF8.GetString(file.Contents));
        Assert.Equal("/core/api/creative/islands/7ba312b9-c1f0-4d3f-a376-c77e69ab83f4/export", handler.Requests.Single().PathAndQuery);

        using var notAnIsland = new HttpClient(new IslandsHandler(_ => Json(HttpStatusCode.OK, """{"hello":"world"}""")));
        await Assert.ThrowsAsync<IslandException>(() => BackendIslands.ExportAsync(notAnIsland, Backend, Token, "x", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImportPostsTheFileAsItIsAndReadsTheNewIsland()
    {
        var handler = new IslandsHandler(_ => Json(HttpStatusCode.OK,
            """{"imported":{"plotItemId":"fb86269a-65aa-4c07-9c23-cd4e9570820b","templateId":"CreativePlot:temperate_medium","islandIndex":2,"name":"Island 2","title":"Box Fights","build":{"changelist":"4727874","version":"7.20"}},"islands":[]}"""));
        using var http = new HttpClient(handler);
        var contents = Encoding.UTF8.GetBytes(IslandFileText);
        var imported = await BackendIslands.ImportAsync(http, Backend, Token, contents, 4834550, TestContext.Current.CancellationToken);

        Assert.Equal(new ImportedIsland("fb86269a-65aa-4c07-9c23-cd4e9570820b", "Island 2", "Box Fights", "7.20"), imported);
        var sent = handler.Requests.Single();
        Assert.Equal(("POST", "/core/api/creative/islands/import?build=4834550"), (sent.Method, sent.PathAndQuery));
        Assert.Equal(("bearer", Token), (sent.Authorization?.Scheme, sent.Authorization?.Parameter));
        Assert.Equal(IslandFileText, sent.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "errors.com.epicgames.creative.island_newer_build",
        "This island is from a newer build (7.30 (CL-4834550)) than yours (7.20 (CL-4727874)). An island loads on the build it was saved on or a newer one",
        "This island is from a newer build (7.30 (CL-4834550)) than yours")]
    [InlineData(HttpStatusCode.Conflict, "errors.com.epicgames.creative.no_free_island_slot",
        "No free Island slots: all 4 of Player's Island slots hold an island (the limit is 4). Ask the server's admin to reset one (admin panel, Players, Creative: Reset island keeps a backup of it), then import again",
        "No free Island slots")]
    [InlineData(HttpStatusCode.BadRequest, "errors.com.epicgames.creative.island_file_invalid", "That isn't a Core island file (its save is damaged: the size or sha256 doesn't match)", "damaged")]
    [InlineData(HttpStatusCode.NotFound, "errors.com.epicgames.common.not_found", "Sorry the resource /core/api/creative/islands/import could not be found", "doesn't have island export and import yet")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "errors.com.epicgames.creative.island_file_too_large", "An island file can't be more than 21 MB", "can't be more than 21 MB")]
    public async Task ImportRefusalsReachThePlayerInPlainWords(HttpStatusCode status, string code, string message, string expected)
    {
        using var http = new HttpClient(new IslandsHandler(_ => Json(status, JsonSerializer.Serialize(new { errorCode = code, errorMessage = message }))));
        var refusal = await Assert.ThrowsAsync<IslandException>(() =>
            BackendIslands.ImportAsync(http, Backend, Token, Encoding.UTF8.GetBytes(IslandFileText), 4727874, TestContext.Current.CancellationToken));
        Assert.Contains(expected, refusal.Message);
        Assert.Equal(code, refusal.ErrorCode);
        Assert.Equal((int)status, refusal.StatusCode);
    }

    [Fact]
    public async Task AnUnreachableBackendIsSaidSo()
    {
        using var http = new HttpClient(new IslandsHandler(_ => throw new HttpRequestException("No connection could be made")));
        var refusal = await Assert.ThrowsAsync<IslandException>(() => BackendIslands.ListAsync(http, Backend, Token, 0, TestContext.Current.CancellationToken));
        Assert.Contains("Couldn't reach", refusal.Message);
    }

    [Fact]
    public void FilesAreCheckedBeforeTheyAreUploaded()
    {
        var summary = BackendIslands.CheckFile(Encoding.UTF8.GetBytes(IslandFileText), BackendIslands.DefaultMaxFileBytes);
        Assert.Equal(new IslandFileSummary("CreativePlot:temperate_medium", "Box Fights", "7.20"), summary);
        Assert.Contains("can't be more than", Assert.Throws<IslandException>(() => BackendIslands.CheckFile(Encoding.UTF8.GetBytes(IslandFileText), 100)).Message);
        Assert.Contains("isn't a Core island file", Assert.Throws<IslandException>(() => BackendIslands.CheckFile(Encoding.UTF8.GetBytes("""{"format":"other"}"""), 1000)).Message);
        Assert.Contains("isn't a Core island file", Assert.Throws<IslandException>(() => BackendIslands.CheckFile([0x50, 0x4B, 0x03, 0x04], 1000)).Message);
        Assert.Contains("empty", Assert.Throws<IslandException>(() => BackendIslands.CheckFile([], 1000)).Message);
    }

    [Theory]
    [InlineData("Island-1-Box-Fights.coreisland", "Island-1-Box-Fights.coreisland")]
    [InlineData("Island 2 - Box: Fights", "Island 2 - Box- Fights.coreisland")]
    [InlineData("..\\..\\evil.coreisland", "evil.coreisland")]
    [InlineData(null, "Island.coreisland")]
    [InlineData("", "Island.coreisland")]
    public void SaveDialogNamesAreSafe(string? suggested, string expected) => Assert.Equal(expected, BackendIslands.SafeFileName(suggested));

    /// <summary>
    /// Runs two real Core-Backends from CORE_BACKEND_DIR (two Core servers): a player's island saved by a 7.20 server on
    /// the first is exported with the launcher's client and imported on the second, where it becomes a new island.
    /// </summary>
    [Fact]
    public async Task AnIslandMovesBetweenTwoRealBackends()
    {
        var backendFolder = Environment.GetEnvironmentVariable("CORE_BACKEND_DIR");
        if (string.IsNullOrEmpty(backendFolder))
        {
            Assert.Skip("Set CORE_BACKEND_DIR to a Core-Backend checkout to run this");
        }

        var cancellation = TestContext.Current.CancellationToken;
        using var job = new JobObject();
        await using var first = await RealBackend.StartAsync(job, backendFolder, cancellation);
        await using var second = await RealBackend.StartAsync(job, backendFolder, cancellation);
        using var http = BackendIslands.CreateHttpClient();

        var builder = await BackendAuthClient.SignInAsync(http, first.Endpoint, "IslandBuilder", LaunchArguments.Password, cancellation);
        Assert.True(builder.Result.Succeeded, builder.Result.ErrorMessage);
        var builderId = await first.AccountIdAsync(builder.AccessToken!, cancellation);
        await first.QueryCreativeAsync(builderId, builder.AccessToken!, cancellation);
        var islands = await BackendIslands.ListAsync(http, first.Endpoint, builder.AccessToken!, 4727874, cancellation);
        var island = islands.Islands.First(entry => entry.TemplateId == "CreativePlot:temperate_medium");
        var save = Enumerable.Range(0, 5000).Select(index => (byte)(index * 7)).ToArray();
        await first.SaveIslandAsync(builderId, island.PlotItemId, save, cancellation);

        var listed = (await BackendIslands.ListAsync(http, first.Endpoint, builder.AccessToken!, 4727874, cancellation)).Islands.Single(entry => entry.PlotItemId == island.PlotItemId);
        Assert.Equal((true, "7.20", true), (listed.Saved, listed.BuildVersion, listed.LoadsOnBuild));
        var file = await BackendIslands.ExportAsync(http, first.Endpoint, builder.AccessToken!, island.PlotItemId, cancellation);
        Assert.EndsWith(".coreisland", file.FileName);
        Assert.Equal("7.20", BackendIslands.CheckFile(file.Contents, islands.MaxFileBytes).BuildVersion);

        var visitor = await BackendAuthClient.SignInAsync(http, second.Endpoint, "IslandVisitor", LaunchArguments.Password, cancellation);
        var visitorId = await second.AccountIdAsync(visitor.AccessToken!, cancellation);
        await second.QueryCreativeAsync(visitorId, visitor.AccessToken!, cancellation);
        // On 7.10 the 7.20 island is refused; on 7.30 it is a new island of the visitor's.
        var older = await Assert.ThrowsAsync<IslandException>(() => BackendIslands.ImportAsync(http, second.Endpoint, visitor.AccessToken!, file.Contents, 4667333, cancellation));
        Assert.Equal("errors.com.epicgames.creative.island_newer_build", older.ErrorCode);
        var imported = await BackendIslands.ImportAsync(http, second.Endpoint, visitor.AccessToken!, file.Contents, 4834550, cancellation);
        Assert.Equal("Island 1", imported.Name);
        Assert.NotEqual(island.PlotItemId, imported.PlotItemId);
        Assert.Equal(save, await second.ReadIslandAsync(visitorId, imported.PlotItemId, cancellation));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record SentRequest(string Method, string Path, string PathAndQuery, AuthenticationHeaderValue? Authorization, string Body);

    private sealed class IslandsHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new SentRequest(request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.PathAndQuery, request.Headers.Authorization, body));
            return answer(request);
        }
    }

    /// <summary>A real Core-Backend on a free port with its own data folder, where every account has its Creative islands.</summary>
    private sealed class RealBackend : IAsyncDisposable
    {
        private const string ServerClient = "313dfed3ec3023e52d3be9dda178bdfa:111aa434a6780d54f5cf759149639f20";
        private const string Agent720 = "Fortnite/++Fortnite+Release-7.20-CL-4727874 Windows/10.0.19045.1.256.64bit";
        private readonly TempFolder _data;
        private readonly LogChannel _output = new("Backend");
        private readonly LogChannel _log = new("Launcher");
        private readonly BackendHost _host;
        private readonly HttpClient _http = new();

        private RealBackend(JobObject job, string folder)
        {
            _data = new TempFolder();
            // Season 7's replay past 13 December 2018 (Update v7.00): every account has its islands.
            var seasonStart = DateTime.UtcNow.AddDays(-19).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            File.WriteAllText(_data.Combine("settings.json"),
                JsonSerializer.Serialize(new { events = new { seasonStarts = new Dictionary<string, string> { ["7"] = seasonStart } } }));
            _host = new BackendHost(job, _output, _log);
            _host.Configure(new BackendTarget(true, "node", folder, _data.Path, 0));
        }

        public BackendEndpoint Endpoint => BackendEndpoint.Local(_host.Port!.Value);

        public static async Task<RealBackend> StartAsync(JobObject job, string folder, CancellationToken cancellation)
        {
            var backend = new RealBackend(job, folder);
            backend._host.Start();
            Assert.True(await backend._host.WaitReadyAsync(TimeSpan.FromSeconds(30), cancellation), string.Join('\n', backend._log.Snapshot().Concat(backend._output.Snapshot())));
            return backend;
        }

        public async Task<string> AccountIdAsync(string token, CancellationToken cancellation)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Endpoint.Url}/account/api/oauth/verify");
            request.Headers.Authorization = new AuthenticationHeaderValue("bearer", token);
            using var response = await _http.SendAsync(request, cancellation);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            return json.RootElement.GetProperty("account_id").GetString()!;
        }

        /// <summary>A 7.20 client's look at its creative profile, which gives the account its islands.</summary>
        public async Task QueryCreativeAsync(string accountId, string token, CancellationToken cancellation)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Endpoint.Url}/fortnite/api/game/v2/profile/{accountId}/client/QueryProfile?profileId=creative&rvn=-1")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("bearer", token);
            request.Headers.TryAddWithoutValidation("User-Agent", Agent720);
            using var response = await _http.SendAsync(request, cancellation);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(cancellation));
        }

        /// <summary>The island saved as a 7.20 server saves it: checkout, upload, checkin on the DSS.</summary>
        public async Task SaveIslandAsync(string accountId, string plotItemId, byte[] save, CancellationToken cancellation)
        {
            var server = await ServerTokenAsync(cancellation);
            var file = $"{Endpoint.Url}/api/dss/v1/creative/islands/personal/{accountId}/{plotItemId}.island";
            foreach (var (method, url, body) in new[] { (HttpMethod.Post, $"{file}/checkout?pid=launcher-test&dl=true", Array.Empty<byte>()), (HttpMethod.Put, $"{file}?autolock=false", save), (HttpMethod.Post, $"{file}/checkin", Array.Empty<byte>()) })
            {
                using var request = new HttpRequestMessage(method, url) { Content = new ByteArrayContent(body) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Headers.Authorization = new AuthenticationHeaderValue("bearer", server);
                request.Headers.TryAddWithoutValidation("User-Agent", Agent720);
                using var response = await _http.SendAsync(request, cancellation);
                Assert.True(response.IsSuccessStatusCode, $"{method} {url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellation)}");
            }
        }

        public async Task<byte[]> ReadIslandAsync(string accountId, string plotItemId, CancellationToken cancellation)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Endpoint.Url}/api/dss/v1/creative/islands/personal/{accountId}/{plotItemId}.island");
            request.Headers.Authorization = new AuthenticationHeaderValue("bearer", await ServerTokenAsync(cancellation));
            using var response = await _http.SendAsync(request, cancellation);
            Assert.True(response.IsSuccessStatusCode);
            return await response.Content.ReadAsByteArrayAsync(cancellation);
        }

        private async Task<string> ServerTokenAsync(CancellationToken cancellation)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Endpoint.Url}{BackendAuthClient.OAuthTokenPath}")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(ServerClient)));
            using var response = await _http.SendAsync(request, cancellation);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            return json.RootElement.GetProperty("access_token").GetString()!;
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _http.Dispose();
            _output.Dispose();
            _log.Dispose();
            _data.Dispose();
        }
    }
}
