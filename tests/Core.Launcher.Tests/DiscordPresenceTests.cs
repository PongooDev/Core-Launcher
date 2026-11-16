using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Core.Launcher.Hosting.Backend;
using Core.Launcher.Hosting.Builds;
using Core.Launcher.Hosting.Discord;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Tests;

/// <summary>Discord Rich Presence (CoreV2 #467): the IPC frames, the activity's words, the backend's presence and when the pipe is written.</summary>
public sealed class DiscordPresenceTests
{
    private static readonly DateTimeOffset GameStarted = new(2026, 11, 16, 20, 0, 0, TimeSpan.Zero);

    private static readonly DiscordActivity InLobby = PresenceText.Activity("MrPodge6", null, GameStarted, "6.30", PlayerActivity.Lobby);

    [Fact]
    public void AFrameIsTheOpcodeAndPayloadLengthLittleEndianThenTheUtf8Json()
    {
        var frame = DiscordIpc.Encode(DiscordOpcode.Frame, """{"state":"Größe"}""");

        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(frame));
        var payload = Encoding.UTF8.GetBytes("""{"state":"Größe"}""");
        Assert.Equal(payload.Length, BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(4)));
        Assert.Equal(DiscordIpc.HeaderSize + payload.Length, frame.Length);
        Assert.Equal(payload, frame[DiscordIpc.HeaderSize..]);
        Assert.Equal((DiscordOpcode.Frame, payload.Length), DiscordIpc.ReadHeader(frame));
    }

    [Fact]
    public async Task FramesReadBackOneAtATimeAndAClosedPipeReadsNull()
    {
        var stream = new MemoryStream([.. DiscordIpc.Encode(DiscordOpcode.Ping, "{}"), .. DiscordIpc.Encode(DiscordOpcode.Frame, """{"evt":"READY"}""")]);
        var cancellation = TestContext.Current.CancellationToken;

        Assert.Equal((DiscordOpcode.Ping, "{}"), await DiscordIpc.ReadAsync(stream, cancellation));
        Assert.Equal((DiscordOpcode.Frame, """{"evt":"READY"}"""), await DiscordIpc.ReadAsync(stream, cancellation));
        Assert.Null(await DiscordIpc.ReadAsync(stream, cancellation));

        var cut = DiscordIpc.Encode(DiscordOpcode.Frame, """{"evt":"READY"}""")[..12];
        Assert.Null(await DiscordIpc.ReadAsync(new MemoryStream(cut), cancellation));
    }

    [Fact]
    public void ALengthOutOfRangeIsRefused()
    {
        var header = new byte[DiscordIpc.HeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), DiscordIpc.MaxPayload + 1);
        Assert.Throws<InvalidDataException>(() => DiscordIpc.ReadHeader(header));

        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), -1);
        Assert.Throws<InvalidDataException>(() => DiscordIpc.ReadHeader(header));
    }

    [Fact]
    public void TheHandshakeIsVersionOneWithTheApplicationId()
    {
        Assert.Equal("""{"v":1,"client_id":"1234567890"}""", DiscordIpc.Handshake("1234567890"));
    }

    [Fact]
    public void SetActivityCarriesTheLinesTheTimerInMillisecondsAndTheImage()
    {
        using var json = JsonDocument.Parse(DiscordIpc.SetActivity(4242, InLobby, "nonce-1"));
        var root = json.RootElement;

        Assert.Equal("SET_ACTIVITY", root.GetProperty("cmd").GetString());
        Assert.Equal("nonce-1", root.GetProperty("nonce").GetString());
        var arguments = root.GetProperty("args");
        Assert.Equal(4242, arguments.GetProperty("pid").GetInt32());
        var activity = arguments.GetProperty("activity");
        Assert.Equal("Logged in as MrPodge6", activity.GetProperty("details").GetString());
        Assert.Equal("In Lobby", activity.GetProperty("state").GetString());
        Assert.Equal(GameStarted.ToUnixTimeMilliseconds(), activity.GetProperty("timestamps").GetProperty("start").GetInt64());
        Assert.Equal("core", activity.GetProperty("assets").GetProperty("large_image").GetString());
        Assert.Equal("Fortnite 6.30", activity.GetProperty("assets").GetProperty("large_text").GetString());
        Assert.False(activity.GetProperty("instance").GetBoolean());
    }

    [Fact]
    public void ClearingSendsSetActivityWithoutAnActivity()
    {
        using var json = JsonDocument.Parse(DiscordIpc.SetActivity(4242, null, "nonce-2"));
        var arguments = json.RootElement.GetProperty("args");

        Assert.Equal("SET_ACTIVITY", json.RootElement.GetProperty("cmd").GetString());
        Assert.Equal(4242, arguments.GetProperty("pid").GetInt32());
        Assert.False(arguments.TryGetProperty("activity", out _));
    }

    [Fact]
    public void SignedOutAndNoGameLeaveOutTheDetailsAndTheTimer()
    {
        using var json = JsonDocument.Parse(DiscordIpc.SetActivity(1, PresenceText.Activity("", null, null, null, PlayerActivity.Unknown), "n"));
        var activity = json.RootElement.GetProperty("args").GetProperty("activity");

        Assert.False(activity.TryGetProperty("details", out _));
        Assert.False(activity.TryGetProperty("timestamps", out _));
        Assert.Equal("In the launcher", activity.GetProperty("state").GetString());
        Assert.Equal("Core", activity.GetProperty("assets").GetProperty("large_text").GetString());
    }

    [Fact]
    public void TextIsKeptWithinDiscordsLimits()
    {
        Assert.Null(DiscordIpc.Text(null));
        Assert.Null(DiscordIpc.Text(" a "));
        Assert.Equal("ab", DiscordIpc.Text(" ab "));
        var clipped = DiscordIpc.Text(new string('x', 200))!;
        Assert.Equal(DiscordIpc.MaxText, clipped.Length);
        Assert.EndsWith("…", clipped);
        Assert.Equal(new string('y', DiscordIpc.MaxText), DiscordIpc.Text(new string('y', DiscordIpc.MaxText)));
    }

    [Fact]
    public void DiscordsAnswersAreReadForReadyErrorsAndCloseReasons()
    {
        Assert.True(DiscordIpc.IsReady("""{"cmd":"DISPATCH","data":{"v":1},"evt":"READY","nonce":null}"""));
        Assert.False(DiscordIpc.IsReady("""{"cmd":"SET_ACTIVITY","evt":null,"nonce":"1"}"""));
        Assert.False(DiscordIpc.IsReady("not json"));

        Assert.Null(DiscordIpc.Error("""{"cmd":"SET_ACTIVITY","data":{},"evt":null,"nonce":"1"}"""));
        Assert.Equal("child \"state\" fails", DiscordIpc.Error("""{"cmd":"SET_ACTIVITY","data":{"code":4000,"message":"child \"state\" fails"},"evt":"ERROR","nonce":"1"}"""));

        Assert.Equal("Invalid Client ID (4000)", DiscordIpc.CloseReason("""{"code":4000,"message":"Invalid Client ID"}"""));
        Assert.Equal("no reason given", DiscordIpc.CloseReason(""));
    }

    [Fact]
    public void TheDetailsNameWhoIsLoggedInButNeverAnEmail()
    {
        Assert.Equal("Logged in as MrPodge6", PresenceText.Details(" MrPodge6 ", null));
        Assert.Equal("Logged in as MrPodge6", PresenceText.Details("mrpodge6", "MrPodge6"));
        Assert.Null(PresenceText.Details("podge@example.com", null));
        Assert.Equal("Logged in as MrPodge6", PresenceText.Details("podge@example.com", "MrPodge6"));
        Assert.Null(PresenceText.Details("", null));
        Assert.Null(PresenceText.Details("  ", " "));
    }

    [Fact]
    public void TheStateFollowsTheGameAndWhatTheBackendSays()
    {
        Assert.Equal("In the launcher", PresenceText.State(false, "6.30", PlayerActivity.Match));
        Assert.Equal("Playing Fortnite 6.30", PresenceText.State(true, "6.30", PlayerActivity.Unknown));
        Assert.Equal("Playing Fortnite 6.30", PresenceText.State(true, "6.30", PlayerActivity.Offline));
        Assert.Equal("Playing Fortnite", PresenceText.State(true, null, PlayerActivity.Unknown));
        Assert.Equal("In Lobby", PresenceText.State(true, "6.30", PlayerActivity.Lobby));
        Assert.Equal("In a match", PresenceText.State(true, "1.1", PlayerActivity.Match));
    }

    [Fact]
    public void TheVersionIsTheOnePlayersCallTheBuild()
    {
        Assert.Equal("6.30", KnownBuilds.VersionOf(KnownBuilds.Fortnite630));
        Assert.Equal("1.1", KnownBuilds.VersionOf(KnownBuilds.Cert));
        Assert.Null(KnownBuilds.VersionOf(1));

        using var folder = new TempFolder();
        Directory.CreateDirectory(folder.Combine("Engine", "Build"));
        File.WriteAllText(folder.Combine("Engine", "Build", "Build.version"), """{"MajorVersion":4,"MinorVersion":26,"PatchVersion":0,"Changelist":14786821,"BranchName":"++Fortnite+Release-14.60"}""");
        Assert.Equal("14.60", RichPresence.VersionOf(new BuildSettings { Path = folder.Path, Changelist = 14786821 }));
        Assert.Equal("6.30", RichPresence.VersionOf(new BuildSettings { Path = folder.Path, Changelist = KnownBuilds.Fortnite630 }));
        Assert.Null(RichPresence.VersionOf(new BuildSettings { Path = folder.Combine("missing"), Changelist = 1 }));
    }

    [Fact]
    public void ItIsOnByDefaultAlsoForSettingsSavedBeforeIt()
    {
        Assert.True(new LauncherSettings().DiscordRichPresence);

        using var folder = new TempFolder();
        File.WriteAllText(folder.Combine("settings.json"), """{"Version":3,"SetupComplete":true,"PlayerName":"MrPodge6"}""");
        Assert.True(new SettingsStore(folder.Combine("settings.json")).Load().DiscordRichPresence);

        File.WriteAllText(folder.Combine("settings.json"), """{"Version":3,"DiscordRichPresence":false}""");
        Assert.False(new SettingsStore(folder.Combine("settings.json")).Load().DiscordRichPresence);
    }

    [Fact]
    public void BackendPresenceAsksForTheLoginAndTheStateItLastSaw()
    {
        var backend = BackendEndpoint.Local(5595);
        Assert.Equal("http://127.0.0.1:5595/core/api/presence?username=Mr%20Podge", BackendPresence.Url(backend, " Mr Podge ", PlayerActivity.Unknown));
        Assert.Equal("http://127.0.0.1:5595/core/api/presence?username=podge%40example.com&after=lobby", BackendPresence.Url(backend, "podge@example.com", PlayerActivity.Lobby));
        Assert.EndsWith("&after=match", BackendPresence.Url(backend, "a", PlayerActivity.Match));
        Assert.EndsWith("&after=offline", BackendPresence.Url(backend, "a", PlayerActivity.Offline));
    }

    [Theory]
    [InlineData("""{"displayName":"MrPodge6","state":"lobby"}""", PlayerActivity.Lobby, "MrPodge6")]
    [InlineData("""{"displayName":"MrPodge6","state":"match"}""", PlayerActivity.Match, "MrPodge6")]
    [InlineData("""{"state":"offline"}""", PlayerActivity.Offline, null)]
    [InlineData("""{"displayName":" ","state":"offline"}""", PlayerActivity.Offline, null)]
    public void BackendPresenceReadsTheStateAndName(string json, PlayerActivity activity, string? name)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new PlayerPresence(activity, name), BackendPresence.Parse(document.RootElement));
    }

    [Theory]
    [InlineData("""{"state":"dancing"}""")]
    [InlineData("""{"displayName":"MrPodge6"}""")]
    [InlineData("""[]""")]
    public void BackendPresenceSkipsAnythingElse(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Null(BackendPresence.Parse(document.RootElement));
    }

    [Fact]
    public async Task ABackendWithoutTheRouteIsNotServedAndOneThatFailsIsAskedAgain()
    {
        var backend = BackendEndpoint.Local(5595);
        var cancellation = TestContext.Current.CancellationToken;
        HttpStatusCode status = HttpStatusCode.NotFound;
        string body = "";
        using var http = new HttpClient(new Answer(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));

        Assert.True((await BackendPresence.ReadAsync(http, backend, "MrPodge6", PlayerActivity.Unknown, cancellation)).Unsupported);

        status = HttpStatusCode.Forbidden;
        Assert.True((await BackendPresence.ReadAsync(http, backend, "MrPodge6", PlayerActivity.Unknown, cancellation)).Unsupported);

        (status, body) = (HttpStatusCode.OK, "<html>not Core</html>");
        Assert.True((await BackendPresence.ReadAsync(http, backend, "MrPodge6", PlayerActivity.Unknown, cancellation)).Unsupported);

        status = HttpStatusCode.ServiceUnavailable;
        var failed = await BackendPresence.ReadAsync(http, backend, "MrPodge6", PlayerActivity.Unknown, cancellation);
        Assert.False(failed.Unsupported);
        Assert.Null(failed.Presence);

        (status, body) = (HttpStatusCode.OK, """{"displayName":"MrPodge6","state":"match"}""");
        Assert.Equal(new PlayerPresence(PlayerActivity.Match, "MrPodge6"), (await BackendPresence.ReadAsync(http, backend, "MrPodge6", PlayerActivity.Lobby, cancellation)).Presence);

        using var down = new HttpClient(new Answer(() => throw new HttpRequestException("refused")));
        Assert.Same(PresenceRead.Failed, await BackendPresence.ReadAsync(down, backend, "MrPodge6", PlayerActivity.Unknown, cancellation));
    }

    [Fact]
    public async Task WithoutAnApplicationIdNothingTouchesDiscord()
    {
        var discord = new FakeDiscord();
        await using var presence = discord.Presence("");

        presence.Show(InLobby);
        await presence.Idle;

        Assert.False(presence.Enabled);
        Assert.Equal(0, discord.Opens);
    }

    [Fact]
    public async Task AnActivityIsSentOnceAndOnlyAChangeIsSentAgain()
    {
        var discord = new FakeDiscord();
        await using var presence = discord.Presence();

        presence.Show(InLobby);
        await presence.Idle;
        presence.Show(InLobby with { });
        await presence.Idle;
        Assert.Equal(1, discord.Opens);
        Assert.Equal(["In Lobby"], discord.States);

        presence.Show(InLobby with { State = PresenceText.InMatch });
        await presence.Idle;
        Assert.Equal(1, discord.Opens);
        Assert.Equal(["In Lobby", "In a match"], discord.States);
    }

    [Fact]
    public async Task WhileDiscordIsntRunningItTriesAgainOnlyWithTheNextChange()
    {
        var discord = new FakeDiscord { Running = false };
        await using var presence = discord.Presence();

        presence.Show(InLobby);
        await presence.Idle;
        presence.Show(InLobby);
        await presence.Idle;
        Assert.Equal(1, discord.Opens);
        Assert.Empty(discord.States);

        discord.Running = true;
        presence.Show(InLobby with { State = PresenceText.InMatch });
        await presence.Idle;
        Assert.Equal(2, discord.Opens);
        Assert.Equal(["In a match"], discord.States);
    }

    [Fact]
    public async Task APipeDiscordClosedIsReopenedForTheNextChange()
    {
        var discord = new FakeDiscord();
        await using var presence = discord.Presence();

        presence.Show(InLobby);
        await presence.Idle;
        discord.Connections[0].Connected = false;

        presence.Show(InLobby with { State = PresenceText.InMatch });
        await presence.Idle;

        Assert.Equal(2, discord.Opens);
        Assert.True(discord.Connections[0].Disposed);
        Assert.Equal(["In Lobby", "In a match"], discord.States);
    }

    [Fact]
    public async Task AWriteThatFailsDropsThePipeAndTheNextChangeReopensIt()
    {
        var discord = new FakeDiscord();
        await using var presence = discord.Presence();

        presence.Show(InLobby);
        await presence.Idle;
        discord.Connections[0].Fail = true;
        presence.Show(InLobby with { State = PresenceText.InMatch });
        await presence.Idle;
        Assert.True(discord.Connections[0].Disposed);

        presence.Show(InLobby with { State = PresenceText.InLauncher });
        await presence.Idle;
        Assert.Equal(2, discord.Opens);
        Assert.Equal(["In Lobby", "In the launcher"], discord.States);
    }

    [Fact]
    public async Task TurningItOffClearsTheActivityAndLetsGoOfThePipe()
    {
        var discord = new FakeDiscord();
        await using var presence = discord.Presence();

        presence.Show(InLobby);
        await presence.Idle;
        presence.Show(null);
        await presence.Idle;

        var connection = Assert.Single(discord.Connections);
        Assert.Equal(2, connection.Sent.Count);
        Assert.Null(Activity(connection.Sent[1]));
        Assert.True(connection.Disposed);

        presence.Show(null);
        await presence.Idle;
        Assert.Equal(1, discord.Opens);
    }

    [Fact]
    public async Task ClosingTheLauncherClearsTheActivity()
    {
        var discord = new FakeDiscord();
        var presence = discord.Presence();

        presence.Show(InLobby);
        await presence.Idle;
        await presence.DisposeAsync();

        var connection = Assert.Single(discord.Connections);
        Assert.Null(Activity(connection.Sent[^1]));
        Assert.True(connection.Disposed);
        presence.Show(InLobby with { State = PresenceText.InMatch });
        Assert.Single(discord.Connections);
    }

    [Fact]
    public void BackendRetriesDoubleUpToAMinute()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), RichPresence.Retry(1));
        Assert.Equal(TimeSpan.FromSeconds(10), RichPresence.Retry(2));
        Assert.Equal(TimeSpan.FromSeconds(40), RichPresence.Retry(4));
        Assert.Equal(TimeSpan.FromSeconds(60), RichPresence.Retry(5));
        Assert.Equal(TimeSpan.FromSeconds(60), RichPresence.Retry(100));
    }

    /// <summary>The activity a SET_ACTIVITY frame carries; null for a clear.</summary>
    private static JsonElement? Activity(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("args").TryGetProperty("activity", out var activity) ? activity.Clone() : null;
    }

    private sealed class FakeDiscord
    {
        public bool Running { get; set; } = true;

        public int Opens { get; private set; }

        public List<FakeConnection> Connections { get; } = [];

        /// <summary>The state line of every activity sent, over all connections.</summary>
        public List<string> States => [.. Connections.SelectMany(connection => connection.Sent).Select(Activity).OfType<JsonElement>().Select(activity => activity.GetProperty("state").GetString()!)];

        public DiscordPresence Presence(string applicationId = "1234567890") => new(applicationId, 4242, _ => { }, _ =>
        {
            ++Opens;
            if (!Running)
            {
                return Task.FromResult<IDiscordConnection?>(null);
            }

            var connection = new FakeConnection();
            Connections.Add(connection);
            return Task.FromResult<IDiscordConnection?>(connection);
        });
    }

    private sealed class FakeConnection : IDiscordConnection
    {
        public bool Connected { get; set; } = true;

        public bool Fail { get; set; }

        public bool Disposed { get; private set; }

        public List<string> Sent { get; } = [];

        public Task SendAsync(DiscordOpcode opcode, string json, CancellationToken cancellation)
        {
            Assert.Equal(DiscordOpcode.Frame, opcode);
            if (Fail || !Connected)
            {
                Connected = false;
                throw new IOException("The Discord pipe closed");
            }

            Sent.Add(json);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Connected = false;
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Answer(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond());
    }
}
