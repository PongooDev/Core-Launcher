using System.Net;
using System.Security.Cryptography;
using System.Text;
using Core.Launcher.Hosting.Components;

namespace Core.Launcher.Tests;

public sealed partial class ServerAssetsTests
{
    private const string PakUrl = ServerAssets.ReleaseUrlPrefix + "cl-3901517/ServerAssets-3.0-CL-3901517.pak";

    private static readonly byte[] PakBytes = Encoding.ASCII.GetBytes("not a real pak, just bytes for the test");

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private const string SigUrl = ServerAssets.ReleaseUrlPrefix + "cl-3901517/ServerAssets-3.0-CL-3901517.sig";

    private static readonly byte[] SigBytes = Encoding.ASCII.GetBytes("not a real sig either");

    private static string Manifest(string? sha256 = null, long? size = null, string url = PakUrl, int changelist = 3901517, string? sig = null, int? schema = null) => $$"""
        {
          "schema": {{schema ?? (sig is null ? 1 : 2)}},
          "builds": {
            "{{changelist}}": {
              "version": "3.0",
              "pak": "ServerAssets-3.0-CL-3901517.pak",
              "url": "{{url}}",
              "sha256": "{{sha256 ?? Hash(PakBytes)}}",
              "size": {{size ?? PakBytes.Length}},{{(sig is null ? "" : $"\n      \"sig\": {sig},")}}
              "contents": ["/Game/Athena/Maps/Athena_LocationVolumes", "/Game/Athena/Maps/Streaming/Athena_GameplayActors"]
            }
          }
        }
        """;

    private static string Sig(string file = "ServerAssets-3.0-CL-3901517.sig", string url = SigUrl, string? sha256 = null, long? size = null) =>
        $$"""{ "file": "{{file}}", "url": "{{url}}", "sha256": "{{sha256 ?? Hash(SigBytes)}}", "size": {{size ?? SigBytes.Length}} }""";

    private static string SignedManifest() => Manifest(sig: Sig());

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
    [InlineData("""{ "schema": 3, "builds": {} }""")]
    [InlineData("""{ "schema": 0, "builds": {} }""")]
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

    [Fact]
    public void ParsesASig()
    {
        Assert.Null(ServerAssetsManifest.Parse(Manifest()).For(3901517)!.Sig);

        var sig = ServerAssetsManifest.Parse(SignedManifest()).For(3901517)!.Sig!;
        Assert.Equal("ServerAssets-3.0-CL-3901517.sig", sig.File);
        Assert.Equal(SigUrl, sig.Url);
        Assert.Equal(Hash(SigBytes), sig.Sha256);
        Assert.Equal(SigBytes.Length, sig.Size);
        Assert.Equal(Hash(SigBytes), ServerAssetsManifest.Parse(Manifest(sig: Sig(sha256: Hash(SigBytes).ToUpperInvariant()))).For(3901517)!.Sig!.Sha256);
    }

    [Fact]
    public void RefusesSigsThatArentSafe()
    {
        // A sig needs schema 2: launchers that only read schema 1 then install nothing, instead of the pak without its sig.
        Assert.Throws<InvalidDataException>(() => ServerAssetsManifest.Parse(Manifest(sig: Sig(), schema: 1)));
        Assert.Equal(2, ServerAssetsManifest.Parse(Manifest(schema: 2)).Schema);
        Assert.Null(ServerAssetsManifest.Parse(Manifest(schema: 2)).For(3901517)!.Sig);

        Assert.Throws<InvalidDataException>(() => ServerAssetsManifest.Parse(Manifest(sig: "\"ServerAssets-3.0-CL-3901517.sig\"")));
        Assert.Throws<InvalidDataException>(() => ServerAssetsManifest.Parse(Manifest(sig: "{}")));
        Assert.Throws<InvalidDataException>(() => ServerAssetsManifest.Parse(Manifest(sig: Sig(file: "ServerAssets-3.0-CL-3901517.pak"))));
        Assert.Throws<InvalidDataException>(() => ServerAssetsManifest.Parse(Manifest(sig: Sig(file: "..\\\\evil.sig"))));
        Assert.Throws<InvalidDataException>(() => ServerAssetsManifest.Parse(Manifest(sig: Sig(url: "https://example.com/ServerAssets.sig"))));
        Assert.Throws<InvalidDataException>(() => ServerAssetsManifest.Parse(Manifest(sig: Sig(sha256: "abc"))));
        Assert.Throws<InvalidDataException>(() => ServerAssetsManifest.Parse(Manifest(sig: Sig(size: 0))));
    }

    [Fact]
    public void DecidesWhatToDoWithASig()
    {
        var signed = ServerAssetsManifest.Parse(SignedManifest());
        var unsigned = ServerAssetsManifest.Parse(Manifest());
        var sig = new InstalledFile(SigBytes.Length, Hash(SigBytes));
        var pak = new InstalledPak(PakBytes.Length, Hash(PakBytes));

        Assert.Equal(ServerAssetsAction.UpToDate, ServerAssets.Decide(true, 3901517, signed, pak with { Sig = sig }).Action);
        Assert.Equal(ServerAssetsAction.Install, ServerAssets.Decide(true, 3901517, signed, pak).Action);
        Assert.Equal(ServerAssetsAction.Install, ServerAssets.Decide(true, 3901517, signed, pak with { Sig = sig with { Sha256 = new string('0', 64) } }).Action);
        Assert.Equal(ServerAssetsAction.Install, ServerAssets.Decide(true, 3901517, signed, pak with { Sig = sig with { Size = 1 } }).Action);

        // A build that doesn't sign its paks: no sig wanted, so one left from an older entry means reinstalling without it.
        Assert.Equal(ServerAssetsAction.UpToDate, ServerAssets.Decide(true, 3901517, unsigned, pak).Action);
        Assert.Equal(ServerAssetsAction.Install, ServerAssets.Decide(true, 3901517, unsigned, pak with { Sig = sig }).Action);
    }

    [Fact]
    public async Task InstallsThePakWithItsSig()
    {
        using var folder = new TempFolder();
        var (build, paks) = Build(folder);
        var routes = new Routes { [ServerAssets.ManifestUrl] = Text(SignedManifest()), [PakUrl] = Bytes(PakBytes), [SigUrl] = Bytes(SigBytes) };
        var assets = new ServerAssets(new HttpClient(routes), folder.Combine("Cache"), _ => { });

        var result = await assets.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken);

        Assert.Contains("installed", result);
        Assert.Contains("pakchunkCoreServerAssets-WindowsClient_P.sig", result);
        Assert.Equal(PakBytes, File.ReadAllBytes(Path.Combine(paks, "pakchunkCoreServerAssets-WindowsClient_P.pak")));
        Assert.Equal(SigBytes, File.ReadAllBytes(Path.Combine(paks, "pakchunkCoreServerAssets-WindowsClient_P.sig")));
        Assert.Equal(3, Directory.GetFiles(paks).Length);

        // Up to date: neither is downloaded again.
        routes.Remove(PakUrl);
        routes.Remove(SigUrl);
        Assert.Contains("is installed", await assets.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken));

        // The sig went missing: both are installed again.
        File.Delete(Path.Combine(paks, "pakchunkCoreServerAssets-WindowsClient_P.sig"));
        routes[PakUrl] = Bytes(PakBytes);
        routes[SigUrl] = Bytes(SigBytes);
        Assert.Contains("updating", await assets.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken));
        Assert.Equal(SigBytes, File.ReadAllBytes(Path.Combine(paks, "pakchunkCoreServerAssets-WindowsClient_P.sig")));
    }

    [Fact]
    public async Task NeverInstallsAPakWithoutItsSig()
    {
        using var folder = new TempFolder();
        var (build, paks) = Build(folder);
        var routes = new Routes { [ServerAssets.ManifestUrl] = Text(SignedManifest()), [PakUrl] = Bytes(PakBytes), [SigUrl] = Bytes([.. SigBytes, 0]) };
        var assets = new ServerAssets(new HttpClient(routes), folder.Combine("Cache"), _ => { });

        // The sig doesn't match: neither is installed (the verified pak waits as a partial download for the next try).
        Assert.StartsWith("skipped", await assets.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken));
        Assert.Equal(["pakchunk0-WindowsClient.pak"], Directory.GetFiles(paks).Where(file => !file.EndsWith(".download", StringComparison.Ordinal)).Select(Path.GetFileName));

        // The sig can't be downloaded at all.
        routes.Remove(SigUrl);
        Assert.StartsWith("skipped", await assets.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken));
        Assert.Equal(["pakchunk0-WindowsClient.pak"], Directory.GetFiles(paks).Where(file => !file.EndsWith(".download", StringComparison.Ordinal)).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AFailedUpdateKeepsTheInstalledPakAndSig()
    {
        using var folder = new TempFolder();
        var (build, paks) = Build(folder);
        var pak = Path.Combine(paks, "pakchunkCoreServerAssets-WindowsClient_P.pak");
        var sig = Path.Combine(paks, "pakchunkCoreServerAssets-WindowsClient_P.sig");
        File.WriteAllText(pak, "an older pak");
        File.WriteAllText(sig, "its sig");
        var routes = new Routes { [ServerAssets.ManifestUrl] = Text(SignedManifest()), [PakUrl] = Bytes(PakBytes), [SigUrl] = Bytes(Encoding.ASCII.GetBytes("a tampered sig")) };
        var assets = new ServerAssets(new HttpClient(routes), folder.Combine("Cache"), _ => { });

        Assert.StartsWith("skipped", await assets.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken));
        Assert.Equal("an older pak", File.ReadAllText(pak));
        Assert.Equal("its sig", File.ReadAllText(sig));
        Assert.Equal(3, Directory.GetFiles(paks).Count(file => !file.EndsWith(".download", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task InstallsNoSigWhenTheManifestHasNone()
    {
        using var folder = new TempFolder();
        var (build, paks) = Build(folder);
        var sig = Path.Combine(paks, "pakchunkCoreServerAssets-WindowsClient_P.sig");
        File.WriteAllBytes(Path.Combine(paks, "pakchunkCoreServerAssets-WindowsClient_P.pak"), PakBytes);
        File.WriteAllBytes(sig, SigBytes);
        var routes = new Routes { [ServerAssets.ManifestUrl] = Text(Manifest()), [PakUrl] = Bytes(PakBytes), [SigUrl] = Bytes(SigBytes) };
        var assets = new ServerAssets(new HttpClient(routes), folder.Combine("Cache"), _ => { });

        // A sig from an older entry goes with the reinstall.
        Assert.Contains("installed", await assets.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(sig));
        Assert.Contains("is installed", await assets.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken));
        Assert.Equal(["pakchunk0-WindowsClient.pak", "pakchunkCoreServerAssets-WindowsClient_P.pak"], Directory.GetFiles(paks).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task RemovesThePakAndSigTogether()
    {
        using var folder = new TempFolder();
        var (build, paks) = Build(folder);
        File.WriteAllText(Path.Combine(paks, "pakchunk0-WindowsClient.sig"), "game sig");
        File.WriteAllBytes(Path.Combine(paks, "pakchunkCoreServerAssets-WindowsClient_P.pak"), PakBytes);
        File.WriteAllBytes(Path.Combine(paks, "pakchunkCoreServerAssets-WindowsClient_P.sig"), SigBytes);
        File.WriteAllText(Path.Combine(paks, "pakchunkCoreServerAssets-WindowsClient_P.sig.download"), "half");
        var routes = new Routes { [ServerAssets.ManifestUrl] = Text("""{ "schema": 1, "builds": {} }""") };
        var assets = new ServerAssets(new HttpClient(routes), folder.Combine("Cache"), _ => { });

        var result = await assets.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken);

        Assert.StartsWith("removed pakchunkCoreServerAssets-WindowsClient_P.pak and pakchunkCoreServerAssets-WindowsClient_P.sig", result);
        Assert.Equal(["pakchunk0-WindowsClient.pak", "pakchunk0-WindowsClient.sig"], Directory.GetFiles(paks).Select(Path.GetFileName).Order());

        // A sig left without its pak goes too, even with nothing published.
        File.WriteAllBytes(Path.Combine(paks, "pakchunkCoreServerAssets-WindowsClient_P.sig"), SigBytes);
        Assert.Contains("none published", await assets.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken));
        Assert.Equal(["pakchunk0-WindowsClient.pak", "pakchunk0-WindowsClient.sig"], Directory.GetFiles(paks).Select(Path.GetFileName).Order());
    }

    /// <summary>A real Core-ServerAssets manifest.json, from CORE_SERVER_ASSETS_MANIFEST (a path), for checking an edit before it's pushed.</summary>
    [Fact]
    public void ParsesTheRealManifest()
    {
        var path = Environment.GetEnvironmentVariable("CORE_SERVER_ASSETS_MANIFEST");
        if (string.IsNullOrEmpty(path))
        {
            Assert.Skip("Set CORE_SERVER_ASSETS_MANIFEST to a Core-ServerAssets manifest.json to run this");
        }

        var manifest = ServerAssetsManifest.Parse(File.ReadAllText(path));
        foreach (var entry in manifest.Builds.Values)
        {
            TestContext.Current.SendDiagnosticMessage($"CL-{entry.Changelist} {entry.Version}: {entry.Pak} {entry.Size} {entry.Sha256}, sig {entry.Sig?.File ?? "none"}, {entry.Contents.Count} assets");
            Assert.NotEmpty(entry.Contents);
        }
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
