using System.Net;
using System.Security.Cryptography;
using System.Text;
using Core.Launcher.Hosting.Components;

namespace Core.Launcher.Tests;

public sealed class ServerAssetsTests
{
    private const string PakUrl = ServerAssets.ReleaseUrlPrefix + "cl-3901517/ServerAssets-3.0-CL-3901517.pak";

    private static readonly byte[] PakBytes = Encoding.ASCII.GetBytes("not a real pak, just bytes for the test");

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Manifest(string? sha256 = null, long? size = null, string url = PakUrl, int changelist = 3901517) => $$"""
        {
          "schema": 1,
          "builds": {
            "{{changelist}}": {
              "version": "3.0",
              "pak": "ServerAssets-3.0-CL-3901517.pak",
              "url": "{{url}}",
              "sha256": "{{sha256 ?? Hash(PakBytes)}}",
              "size": {{size ?? PakBytes.Length}},
              "contents": ["/Game/Athena/Maps/Athena_LocationVolumes", "/Game/Athena/Maps/Streaming/Athena_GameplayActors"]
            }
          }
        }
        """;

    [Fact]
    public void ParsesTheManifest()
    {
        var manifest = ServerAssetsManifest.Parse(Manifest());
        var entry = manifest.For(3901517)!;
        Assert.Equal("3.0", entry.Version);
        Assert.Equal("ServerAssets-3.0-CL-3901517.pak", entry.Pak);
        Assert.Equal(PakUrl, entry.Url);
        Assert.Equal(Hash(PakBytes), entry.Sha256);
        Assert.Equal(PakBytes.Length, entry.Size);
        Assert.Equal(2, entry.Contents.Count);
        Assert.Null(manifest.For(3889387));
    }

    [Fact]
    public void ParsesAnEmptyManifest()
    {
        Assert.Empty(ServerAssetsManifest.Parse("""{ "schema": 1, "builds": {} }""").Builds);
    }

    [Fact]
    public void NormalisesTheHashToLowerCase()
    {
        Assert.Equal(Hash(PakBytes), ServerAssetsManifest.Parse(Manifest(sha256: Hash(PakBytes).ToUpperInvariant())).For(3901517)!.Sha256);
    }

    [Theory]
    [InlineData("""{ "builds": {} }""")]
    [InlineData("""{ "schema": 2, "builds": {} }""")]
    [InlineData("""{ "schema": 1, "builds": [] }""")]
    [InlineData("""{ "schema": 1, "builds": { "3.0": {} } }""")]
    [InlineData("not json")]
    public void RefusesBadManifests(string json)
    {
        Assert.Throws<InvalidDataException>(() => ServerAssetsManifest.Parse(json));
    }

    [Fact]
    public void RefusesEntriesThatArentSafe()
    {
        Assert.Throws<InvalidDataException>(() => ServerAssetsManifest.Parse(Manifest(sha256: "abc")));
        Assert.Throws<InvalidDataException>(() => ServerAssetsManifest.Parse(Manifest(size: 0)));
        Assert.Throws<InvalidDataException>(() => ServerAssetsManifest.Parse(Manifest(url: "https://example.com/ServerAssets.pak")));
        Assert.Throws<InvalidDataException>(() => ServerAssetsManifest.Parse(Manifest(url: "http://github.com/PongooDev/Core-ServerAssets/releases/download/x.pak")));
        Assert.Throws<InvalidDataException>(() => ServerAssetsManifest.Parse(Manifest().Replace("\"ServerAssets-3.0-CL-3901517.pak\"", "\"..\\\\evil.pak\"")));
        Assert.Throws<InvalidDataException>(() => ServerAssetsManifest.Parse(Manifest().Replace("\"ServerAssets-3.0-CL-3901517.pak\"", "\"ServerAssets.exe\"")));
    }

    [Fact]
    public void DecidesWhatToDo()
    {
        var manifest = ServerAssetsManifest.Parse(Manifest());
        var current = new InstalledPak(PakBytes.Length, Hash(PakBytes));
        var other = new InstalledPak(PakBytes.Length, new string('0', 64));

        Assert.Equal(ServerAssetsAction.Install, ServerAssets.Decide(true, 3901517, manifest, null).Action);
        Assert.Equal(ServerAssetsAction.Install, ServerAssets.Decide(true, 3901517, manifest, other).Action);
        Assert.Equal(ServerAssetsAction.Install, ServerAssets.Decide(true, 3901517, manifest, current with { Size = 1 }).Action);
        Assert.Equal(ServerAssetsAction.UpToDate, ServerAssets.Decide(true, 3901517, manifest, current).Action);
        Assert.Equal(ServerAssetsAction.UpToDate, ServerAssets.Decide(true, 3901517, manifest, current with { Sha256 = current.Sha256.ToUpperInvariant() }).Action);

        // Not listed: nothing, or Core's pak goes.
        Assert.Equal(ServerAssetsAction.None, ServerAssets.Decide(true, 3889387, manifest, null).Action);
        Assert.Equal(ServerAssetsAction.Remove, ServerAssets.Decide(true, 3889387, manifest, current).Action);

        // Turned off: Core's pak goes, whatever the manifest says.
        Assert.Equal(ServerAssetsAction.Remove, ServerAssets.Decide(false, 3901517, manifest, current).Action);
        Assert.Equal(ServerAssetsAction.Remove, ServerAssets.Decide(false, 3901517, null, current).Action);
        Assert.Equal(ServerAssetsAction.None, ServerAssets.Decide(false, 3901517, manifest, null).Action);

        // No manifest or no changelist: an installed pak stays, nothing is installed.
        Assert.Equal(ServerAssetsAction.Keep, ServerAssets.Decide(true, 3901517, null, current).Action);
        Assert.Equal(ServerAssetsAction.None, ServerAssets.Decide(true, 3901517, null, null).Action);
        Assert.Equal(ServerAssetsAction.Keep, ServerAssets.Decide(true, 0, manifest, current).Action);
        Assert.Equal(ServerAssetsAction.None, ServerAssets.Decide(true, 0, manifest, null).Action);
    }

    [Fact]
    public void NamesThePakAfterTheBuildsPlatform()
    {
        using var folder = new TempFolder();
        Assert.Equal("pakchunkCoreServerAssets-WindowsClient_P.pak", ServerAssets.PakFileName(folder.Combine("missing")));

        var paks = Directory.CreateDirectory(folder.Combine("Paks")).FullName;
        File.WriteAllText(Path.Combine(paks, "pakchunk0-WindowsNoEditor.pak"), "");
        File.WriteAllText(Path.Combine(paks, "pakchunk0_s1-WindowsNoEditor.pak"), "");
        Assert.Equal("pakchunkCoreServerAssets-WindowsNoEditor_P.pak", ServerAssets.PakFileName(paks));
    }

    [Fact]
    public async Task InstallsAVerifiedPak()
    {
        using var folder = new TempFolder();
        var (build, paks) = Build(folder);
        var routes = new Routes { [ServerAssets.ManifestUrl] = Text(Manifest()), [PakUrl] = Bytes(PakBytes) };
        var log = new List<string>();
        var assets = new ServerAssets(new HttpClient(routes), folder.Combine("Cache"), log.Add);

        var result = await assets.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken);

        var installed = Path.Combine(paks, "pakchunkCoreServerAssets-WindowsClient_P.pak");
        Assert.Equal(PakBytes, File.ReadAllBytes(installed));
        Assert.Contains("installed", result);
        Assert.Equal("game pak", File.ReadAllText(Path.Combine(paks, "pakchunk0-WindowsClient.pak")));
        Assert.True(File.Exists(assets.CachedManifest));
        Assert.Contains(log, line => line.StartsWith("Server assets: ", StringComparison.Ordinal));

        // Up to date: not downloaded again.
        routes.Remove(PakUrl);
        Assert.Contains("is installed", await assets.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken));
        Assert.Equal(PakBytes, File.ReadAllBytes(installed));
    }

    [Fact]
    public async Task NeverInstallsAPakThatDoesntMatch()
    {
        using var folder = new TempFolder();
        var (build, paks) = Build(folder);
        var tampered = Encoding.ASCII.GetBytes("NOT a real pak, just bytes for the test");
        var routes = new Routes { [ServerAssets.ManifestUrl] = Text(Manifest()), [PakUrl] = Bytes(tampered) };
        var assets = new ServerAssets(new HttpClient(routes), folder.Combine("Cache"), _ => { });

        var result = await assets.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken);

        Assert.StartsWith("skipped", result);
        Assert.Equal(["pakchunk0-WindowsClient.pak"], Directory.GetFiles(paks).Select(Path.GetFileName));

        // Wrong size, right bytes otherwise.
        routes[PakUrl] = Bytes([.. PakBytes, 0]);
        Assert.StartsWith("skipped", await assets.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken));
        Assert.Equal(["pakchunk0-WindowsClient.pak"], Directory.GetFiles(paks).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ReplacesAnOutdatedPak()
    {
        using var folder = new TempFolder();
        var (build, paks) = Build(folder);
        var installed = Path.Combine(paks, "pakchunkCoreServerAssets-WindowsClient_P.pak");
        File.WriteAllText(installed, "an older pak");
        var routes = new Routes { [ServerAssets.ManifestUrl] = Text(Manifest()), [PakUrl] = Bytes(PakBytes) };
        var assets = new ServerAssets(new HttpClient(routes), folder.Combine("Cache"), _ => { });

        Assert.Contains("updating", await assets.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken));
        Assert.Equal(PakBytes, File.ReadAllBytes(installed));
    }

    [Fact]
    public async Task RemovesOnlyCoresPak()
    {
        using var folder = new TempFolder();
        var (build, paks) = Build(folder);
        File.WriteAllBytes(Path.Combine(paks, "pakchunkCoreServerAssets-WindowsClient_P.pak"), PakBytes);
        File.WriteAllText(Path.Combine(paks, "pakchunkCoreServerAssets-WindowsClient_P.pak.download"), "half");
        File.WriteAllText(Path.Combine(paks, "pakchunk1-WindowsClient.pak"), "game pak");
        var routes = new Routes { [ServerAssets.ManifestUrl] = Text("""{ "schema": 1, "builds": {} }""") };
        var assets = new ServerAssets(new HttpClient(routes), folder.Combine("Cache"), _ => { });

        Assert.StartsWith("removed", await assets.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken));
        Assert.Equal(["pakchunk0-WindowsClient.pak", "pakchunk1-WindowsClient.pak"], Directory.GetFiles(paks).Select(Path.GetFileName).Order());

        // Turned off: removed without asking GitHub.
        File.WriteAllBytes(Path.Combine(paks, "pakchunkCoreServerAssets-WindowsClient_P.pak"), PakBytes);
        var offline = new ServerAssets(new HttpClient(new Routes()), folder.Combine("Cache"), _ => { });
        Assert.StartsWith("removed", await offline.EnsureAsync(build, 3901517, false, TestContext.Current.CancellationToken));
        Assert.Equal(2, Directory.GetFiles(paks).Length);
    }

    [Fact]
    public async Task UsesTheCachedManifestWhenGitHubIsDown()
    {
        using var folder = new TempFolder();
        var (build, paks) = Build(folder);
        var routes = new Routes { [ServerAssets.ManifestUrl] = Text(Manifest()), [PakUrl] = Bytes(PakBytes) };
        var assets = new ServerAssets(new HttpClient(routes), folder.Combine("Cache"), _ => { });
        await assets.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken);

        routes.Remove(ServerAssets.ManifestUrl);
        var log = new List<string>();
        var offline = new ServerAssets(new HttpClient(routes), folder.Combine("Cache"), log.Add);
        Assert.Contains("is installed", await offline.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken));
        Assert.Contains(log, line => line.Contains("using the copy from", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NetworkFailuresNeverThrow()
    {
        using var folder = new TempFolder();
        var (build, paks) = Build(folder);
        var assets = new ServerAssets(new HttpClient(new Throwing()), folder.Combine("Cache"), _ => { });

        Assert.Contains("manifest couldn't be read", await assets.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(paks));

        var manifestOnly = new Routes { [ServerAssets.ManifestUrl] = Text(Manifest()) };
        var noPak = new ServerAssets(new HttpClient(manifestOnly), folder.Combine("Cache2"), _ => { });
        Assert.StartsWith("skipped", await noPak.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(paks));
    }

    [Fact]
    public async Task PendingChecksOncePerBuildAndSetting()
    {
        using var folder = new TempFolder();
        var (build, _) = Build(folder);
        var routes = new Routes { [ServerAssets.ManifestUrl] = Text("""{ "schema": 1, "builds": {} }""") };
        var assets = new ServerAssets(new HttpClient(routes), folder.Combine("Cache"), _ => { });
        var checkedOnce = new TaskCompletionSource();
        assets.Checked += () => checkedOnce.TrySetResult();

        assets.Pending(build, 3901517, true, TestContext.Current.CancellationToken);
        await checkedOnce.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Null(assets.Pending(build, 3901517, true, TestContext.Current.CancellationToken));
        Assert.Contains("none published for CL-3901517", assets.Status);
    }

    private static (string Build, string Paks) Build(TempFolder folder)
    {
        var build = folder.Combine("Build");
        var paks = ServerAssets.PaksFolder(build);
        Directory.CreateDirectory(paks);
        File.WriteAllText(Path.Combine(paks, "pakchunk0-WindowsClient.pak"), "game pak");
        return (build, paks);
    }

    private static Func<HttpResponseMessage> Text(string text) => () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) };

    private static Func<HttpResponseMessage> Bytes(byte[] bytes) => () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class Routes : HttpMessageHandler, IEnumerable<KeyValuePair<string, Func<HttpResponseMessage>>>
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = [];

        public Func<HttpResponseMessage> this[string url]
        {
            set => _routes[url] = value;
        }

        public void Remove(string url) => _routes.Remove(url);

        public IEnumerator<KeyValuePair<string, Func<HttpResponseMessage>>> GetEnumerator() => _routes.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_routes.TryGetValue(request.RequestUri!.ToString(), out var respond) ? respond() : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class Throwing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No such host is known.");
    }
}
