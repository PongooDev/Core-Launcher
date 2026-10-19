using System.Net;
using System.Text;
using Core.Launcher.Hosting;
using Core.Launcher.Hosting.Components;
using Core.Launcher.Hosting.Dlls;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Tests;

public sealed class DllStoreTests
{
    private static readonly string System32 = Environment.SystemDirectory;

    private const string ReleaseJson = """
        {
          "tag_name": "v0.1.0",
          "published_at": "2026-10-20T10:00:00Z",
          "assets": [
            { "name": "Core.dll", "size": 4, "digest": "sha256:9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08", "browser_download_url": "https://example.test/Core.dll" },
            { "name": "CoreClient.dll", "size": 10, "digest": null, "browser_download_url": "https://example.test/CoreClient.dll" },
            { "name": "Source.zip", "size": 99, "browser_download_url": "https://example.test/Source.zip" }
          ]
        }
        """;

    private static DllStore Store(TempFolder folder, HttpMessageHandler? handler = null, List<string>? log = null) =>
        new(new LauncherPaths(folder.Combine("Launcher")), new GitHubClient(new HttpClient(handler ?? new Respond(HttpStatusCode.NotFound))), (log ?? []).Add);

    [Fact]
    public void LocalDllsAreCopiedWithTheirPdbUnderTheirHash()
    {
        using var folder = new TempFolder();
        var build = folder.Combine("x64", "Release");
        Directory.CreateDirectory(build);
        File.Copy(Path.Combine(System32, "version.dll"), Path.Combine(build, "Core.dll"));
        File.WriteAllText(Path.Combine(build, "Core.pdb"), "symbols");

        var store = Store(folder);
        store.Configure(new DllSettings { Source = DllSource.Local, LocalFolder = build });
        var first = store.Resolve(DllRole.Server);

        Assert.StartsWith(folder.Combine("Launcher", "Dlls", "Local"), first);
        Assert.Equal("Core.dll", Path.GetFileName(first));
        Assert.Equal(File.ReadAllBytes(Path.Combine(build, "Core.dll")), File.ReadAllBytes(first));
        Assert.Equal("symbols", File.ReadAllText(Path.Combine(Path.GetDirectoryName(first)!, "Core.pdb")));
        Assert.Equal(first, store.Resolve(DllRole.Server));

        File.Copy(Path.Combine(System32, "winmm.dll"), Path.Combine(build, "Core.dll"), true);
        var rebuilt = store.Resolve(DllRole.Server);
        Assert.NotEqual(first, rebuilt);
        Assert.True(File.Exists(first));
    }

    [Fact]
    public void MissingLocalDllsExplainThemselves()
    {
        using var folder = new TempFolder();
        var store = Store(folder);
        store.Configure(new DllSettings { Source = DllSource.Local, LocalFolder = "" });
        Assert.Contains("No local DLL folder", Assert.Throws<DllException>(() => store.Resolve(DllRole.Server)).Message);

        store.Configure(new DllSettings { Source = DllSource.Local, LocalFolder = folder.Path });
        Assert.Contains("CoreClient.dll doesn't exist", Assert.Throws<DllException>(() => store.Resolve(DllRole.Client)).Message);

        File.WriteAllText(folder.Combine("Core.dll"), "not a DLL");
        Assert.Contains("isn't a Windows image", Assert.Throws<DllException>(() => store.Resolve(DllRole.Server)).Message);
    }

    [Fact]
    public async Task NoReleaseYetIsReportedAndResolveExplains()
    {
        using var folder = new TempFolder();
        var log = new List<string>();
        var store = Store(folder, new Respond(HttpStatusCode.NotFound), log);
        store.Configure(new DllSettings());

        await store.RefreshAsync(null, TestContext.Current.CancellationToken);

        Assert.Contains("PongooDev/CoreV2 has no release yet", log);
        Assert.Null(store.ReleaseTag);
        Assert.Contains("No PongooDev/CoreV2 release is downloaded", Assert.Throws<DllException>(() => store.Resolve(DllRole.Server)).Message);
    }

    [Fact]
    public async Task OfflineFallsBackToTheNewestDownloadedRelease()
    {
        using var folder = new TempFolder();
        foreach (var (tag, published) in new[] { ("v1", "2026-10-01T00:00:00Z"), ("v2", "2026-10-10T00:00:00Z") })
        {
            var release = folder.Combine("Launcher", "Dlls", "Release", tag);
            Directory.CreateDirectory(release);
            File.Copy(Path.Combine(System32, "version.dll"), Path.Combine(release, "Core.dll"));
            File.WriteAllText(Path.Combine(release, "release.json"), $$"""{"Tag":"{{tag}}","PublishedAt":"{{published}}","Assets":["Core.dll"]}""");
        }

        var store = Store(folder, new Respond(HttpStatusCode.ServiceUnavailable));
        store.Configure(new DllSettings());
        await store.RefreshAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("v2", store.ReleaseTag);
        Assert.Equal(folder.Combine("Launcher", "Dlls", "Release", "v2", "Core.dll"), store.Resolve(DllRole.Server));
    }

    [Fact]
    public void ReleaseJsonParses()
    {
        var release = GitHubClient.ParseRelease(ReleaseJson);

        Assert.Equal("v0.1.0", release.Tag);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero), release.PublishedAt);
        Assert.Equal(["Core.dll", "CoreClient.dll", "Source.zip"], release.Assets.Select(asset => asset.Name));
        Assert.Equal("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", release.Assets[0].Sha256);
        Assert.Null(release.Assets[1].Sha256);
    }

    [Fact]
    public async Task DownloadsAreCheckedAgainstTheReleaseDigest()
    {
        using var folder = new TempFolder();
        var asset = GitHubClient.ParseRelease(ReleaseJson).Assets[0];

        var good = new GitHubClient(new HttpClient(new Respond(HttpStatusCode.OK, "test")));
        await good.DownloadAssetAsync(asset, folder.Combine("Core.dll"), null, TestContext.Current.CancellationToken);
        Assert.Equal("test", File.ReadAllText(folder.Combine("Core.dll")));

        var tampered = new GitHubClient(new HttpClient(new Respond(HttpStatusCode.OK, "TEST")));
        await Assert.ThrowsAsync<InvalidDataException>(() => tampered.DownloadAssetAsync(asset, folder.Combine("Other.dll"), null, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(folder.Combine("Other.dll")));

        var truncated = new GitHubClient(new HttpClient(new Respond(HttpStatusCode.OK, "tes")));
        await Assert.ThrowsAsync<InvalidDataException>(() => truncated.DownloadAssetAsync(asset, folder.Combine("Short.dll"), null, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(folder.Combine("Short.dll.download")));
    }

    private sealed class Respond(HttpStatusCode status, string body = "") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) });
    }
}
