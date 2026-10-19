using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Core.Launcher.Hosting;
using Core.Launcher.Hosting.Backend;
using Core.Launcher.Hosting.Components;
using Core.Launcher.Hosting.Logging;
using Core.Launcher.Hosting.Native;

namespace Core.Launcher.Tests;

public sealed class ComponentsTests
{
    private const string NodeIndex = """
        [
          {"version":"v26.12.0","files":["win-x64-zip","linux-x64"],"lts":false},
          {"version":"v24.22.0","files":["linux-x64","win-x64-zip","win-x64-msi"],"lts":"Krypton"},
          {"version":"v24.21.0","files":["win-x64-zip"],"lts":"Krypton"},
          {"version":"v22.30.0","files":["win-x64-zip"],"lts":"Jod"}
        ]
        """;

    [Fact]
    public void PicksTheNewestLtsWithAWindowsZip()
    {
        var release = NodeRuntime.PickRelease(NodeIndex, 22)!;
        Assert.Equal("v24.22.0", release.Version);
        Assert.Equal("node-v24.22.0-win-x64.zip", release.ArchiveName);
        Assert.Equal("https://nodejs.org/dist/v24.22.0/node-v24.22.0-win-x64.zip", release.ArchiveUrl);
        Assert.Equal("https://nodejs.org/dist/v24.22.0/SHASUMS256.txt", release.ChecksumsUrl);
        Assert.Null(NodeRuntime.PickRelease(NodeIndex, 30));
    }

    [Fact]
    public void FindsAFilesChecksum()
    {
        const string sums = "aaaa  node-v24.22.0-x64.msi\nBBBB  node-v24.22.0-win-x64.zip\ncccc  node-v24.22.0-win-x64.7z\n";
        Assert.Equal("bbbb", NodeRuntime.ChecksumFor(sums, "node-v24.22.0-win-x64.zip"));
        Assert.Null(NodeRuntime.ChecksumFor(sums, "node-v24.22.0-win-arm64.zip"));
    }

    [Fact]
    public void ExtractsWithoutTheArchivesTopFolder()
    {
        using var folder = new TempFolder();
        var wrapped = Zip(folder.Combine("wrapped.zip"), ("PongooDev-Core-Backend-12bd046/src/main.js", "main"), ("PongooDev-Core-Backend-12bd046/package.json", "{}"));
        Downloads.ExtractWithoutTopFolder(wrapped, folder.Combine("wrapped"));
        Assert.Equal("main", File.ReadAllText(folder.Combine("wrapped", "src", "main.js")));

        var flat = Zip(folder.Combine("flat.zip"), ("a.txt", "a"), ("b/c.txt", "c"));
        Downloads.ExtractWithoutTopFolder(flat, folder.Combine("flat"));
        Assert.True(File.Exists(folder.Combine("flat", "a.txt")));
        Assert.True(File.Exists(folder.Combine("flat", "b", "c.txt")));

        var escaping = Zip(folder.Combine("escaping.zip"), ("top/../../evil.txt", "x"));
        Assert.Throws<InvalidDataException>(() => Downloads.ExtractWithoutTopFolder(escaping, folder.Combine("escaping")));
        Assert.False(File.Exists(folder.Combine("evil.txt")));
    }

    [Theory]
    [InlineData("v0.1.0", "0.1.0")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v2.0", "2.0.0")]
    [InlineData("v1.4.0-beta.2", "1.4.0")]
    public void ParsesReleaseTags(string tag, string version)
    {
        Assert.Equal(Version.Parse(version), LauncherUpdater.ParseVersion(tag));
    }

    [Fact]
    public void UnversionedTagsAreIgnored()
    {
        Assert.Null(LauncherUpdater.ParseVersion("latest"));
    }

    [Fact]
    public async Task OffersOnlyANewerReleaseWithTheExe()
    {
        var github = Github(new Routes
        {
            ["https://api.github.com/repos/PongooDev/Core-Launcher/releases/latest"] = Json(Release("v0.2.0", ("CoreLauncher.exe", "exe"))),
        });

        Assert.Equal("v0.2.0", (await new LauncherUpdater(github, new Version(0, 1, 0), null, _ => { }).CheckAsync(TestContext.Current.CancellationToken))?.Tag);
        Assert.Null(await new LauncherUpdater(github, new Version(0, 2, 0), null, _ => { }).CheckAsync(TestContext.Current.CancellationToken));

        var withoutExe = Github(new Routes
        {
            ["https://api.github.com/repos/PongooDev/Core-Launcher/releases/latest"] = Json(Release("v0.3.0", ("Source.zip", "zip"))),
        });
        Assert.Null(await new LauncherUpdater(withoutExe, new Version(0, 1, 0), null, _ => { }).CheckAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InstallsTheNewExeInPlaceOfTheRunningOne()
    {
        using var folder = new TempFolder();
        var executable = folder.Combine("CoreLauncher.exe");
        File.WriteAllText(executable, "old");
        var github = Github(new Routes
        {
            ["https://api.github.com/repos/PongooDev/Core-Launcher/releases/latest"] = Json(Release("v0.2.0", ("CoreLauncher.exe", "new"))),
            ["https://example.test/CoreLauncher.exe"] = Text("new"),
        });
        var updater = new LauncherUpdater(github, new Version(0, 1, 0), executable, _ => { });

        var release = await updater.CheckAsync(TestContext.Current.CancellationToken);
        await updater.InstallAsync(release!, null, TestContext.Current.CancellationToken);

        Assert.Equal("new", File.ReadAllText(executable));
        Assert.Equal("old", File.ReadAllText(executable + ".old"));
        LauncherUpdater.CleanUp(executable);
        Assert.False(File.Exists(executable + ".old"));
    }

    [Fact]
    public async Task BackendComesFromTheLatestReleaseOrTheDefaultBranch()
    {
        var released = new BackendPackage(new LauncherPaths("unused"), Github(new Routes
        {
            ["https://api.github.com/repos/PongooDev/Core-Backend/releases/latest"] = Json(Release("v1.0.0")),
        }), null!, _ => { });
        var version = await released.LatestAsync("PongooDev/Core-Backend", TestContext.Current.CancellationToken);
        Assert.Equal(("v1.0.0", "https://api.github.com/repos/PongooDev/Core-Backend/zipball/v1.0.0"), (version.Id, version.ArchiveUrl));

        var unreleased = new BackendPackage(new LauncherPaths("unused"), Github(new Routes
        {
            ["https://api.github.com/repos/PongooDev/Core-Backend"] = Json("""{"default_branch":"main"}"""),
            ["https://api.github.com/repos/PongooDev/Core-Backend/commits/main"] = Text("12bd0461bcb4d79a4e20496b2eec40495b383e97"),
        }), null!, _ => { });
        version = await unreleased.LatestAsync("PongooDev/Core-Backend", TestContext.Current.CancellationToken);
        Assert.Equal(("main-12bd046", "https://api.github.com/repos/PongooDev/Core-Backend/zipball/12bd0461bcb4d79a4e20496b2eec40495b383e97"), (version.Id, version.ArchiveUrl));
    }

    [Fact]
    public async Task InstallsNodeAndTheBackendThenSkipsAnUpToDateOne()
    {
        using var folder = new TempFolder();
        var paths = new LauncherPaths(folder.Combine("Launcher"));
        var nodeZip = File.ReadAllBytes(Zip(folder.Combine("node.zip"), ("node-v24.22.0-win-x64/node.exe", "node"), ("node-v24.22.0-win-x64/node_modules/npm/bin/npm-cli.js", "npm")));
        var backendZip = File.ReadAllBytes(Zip(folder.Combine("backend.zip"),
            ("PongooDev-Core-Backend-abc/src/main.js", "main"), ("PongooDev-Core-Backend-abc/package.json", "{}"), ("PongooDev-Core-Backend-abc/node_modules/express/index.js", "express")));
        var routes = new Routes
        {
            ["https://nodejs.org/dist/index.json"] = Json(NodeIndex),
            ["https://nodejs.org/dist/v24.22.0/SHASUMS256.txt"] = Text($"{Convert.ToHexStringLower(SHA256.HashData(nodeZip))}  node-v24.22.0-win-x64.zip\n"),
            ["https://nodejs.org/dist/v24.22.0/node-v24.22.0-win-x64.zip"] = Bytes(nodeZip),
            ["https://api.github.com/repos/PongooDev/Core-Backend/releases/latest"] = Json(Release("v1.0.0")),
            ["https://api.github.com/repos/PongooDev/Core-Backend/zipball/v1.0.0"] = Bytes(backendZip),
        };
        var github = Github(routes);
        var node = new NodeRuntime(paths, github.Http, _ => { });
        var package = new BackendPackage(paths, github, node, _ => { });

        var installed = await package.UpdateAsync("PongooDev/Core-Backend", null, TestContext.Current.CancellationToken);

        Assert.Equal("v1.0.0", installed!.Id);
        Assert.Equal(Path.Combine(paths.BackendVersions, "v1.0.0"), installed.Folder);
        Assert.Equal("main", File.ReadAllText(Path.Combine(installed.Folder, "src", "main.js")));
        Assert.Equal(installed, package.Installed);
        Assert.Equal(Path.Combine(paths.Runtime, "node-v24.22.0-win-x64", "node.exe"), node.Executable);
        Assert.Equal("v24.22.0", node.Version);

        routes.Remove("https://api.github.com/repos/PongooDev/Core-Backend/zipball/v1.0.0");
        Assert.Null(await package.UpdateAsync("PongooDev/Core-Backend", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATamperedNodeDownloadIsRefused()
    {
        using var folder = new TempFolder();
        var paths = new LauncherPaths(folder.Combine("Launcher"));
        var github = Github(new Routes
        {
            ["https://nodejs.org/dist/index.json"] = Json(NodeIndex),
            ["https://nodejs.org/dist/v24.22.0/SHASUMS256.txt"] = Text($"{new string('0', 64)}  node-v24.22.0-win-x64.zip\n"),
            ["https://nodejs.org/dist/v24.22.0/node-v24.22.0-win-x64.zip"] = Bytes(File.ReadAllBytes(Zip(folder.Combine("node.zip"), ("node-v24.22.0-win-x64/node.exe", "node")))),
        });
        var node = new NodeRuntime(paths, github.Http, _ => { });

        await Assert.ThrowsAsync<InvalidDataException>(() => node.EnsureAsync(null, TestContext.Current.CancellationToken));
        Assert.Null(node.Executable);
    }

    [Fact]
    public void IncompleteInstallMarkersMeanNotInstalled()
    {
        using var folder = new TempFolder();
        var paths = new LauncherPaths(folder.Combine("Launcher"));
        Directory.CreateDirectory(paths.Runtime);
        Directory.CreateDirectory(paths.Backend);
        File.WriteAllText(Path.Combine(paths.Runtime, "node.json"), """{"Version":"v24.22.0","Executable":"C:/old/node.exe"}""");
        File.WriteAllText(Path.Combine(paths.Backend, "installed.json"), """{"Id":"main-abc"}""");

        Assert.Null(new NodeRuntime(paths, new HttpClient(), _ => { }).Executable);
        Assert.Null(new BackendPackage(paths, Github(new Routes()), null!, _ => { }).Installed);

        File.WriteAllText(Path.Combine(paths.Backend, "installed.json"), "not json");
        Assert.Null(new BackendPackage(paths, Github(new Routes()), null!, _ => { }).Installed);
    }

    /// <summary>The public path for real: Node.js from nodejs.org, Core-Backend from GitHub, npm ci, then the backend runs.</summary>
    [Fact]
    public async Task InstallsAndRunsTheRealManagedBackend()
    {
        if (Environment.GetEnvironmentVariable("CORE_LAUNCHER_NETWORK_TESTS") != "1")
        {
            Assert.Skip("Set CORE_LAUNCHER_NETWORK_TESTS=1 to download Node.js and Core-Backend for real");
        }

        using var folder = new TempFolder();
        var paths = new LauncherPaths(folder.Combine("Launcher"));
        using var http = GitHubClient.CreateHttpClient();
        var log = new LogChannel("Launcher");
        var node = new NodeRuntime(paths, http, log.Write);
        var package = new BackendPackage(paths, new GitHubClient(http), node, log.Write);

        var installed = await package.UpdateAsync("PongooDev/Core-Backend", null, TestContext.Current.CancellationToken);
        Assert.NotNull(installed);

        using var job = new JobObject();
        using var output = new LogChannel("Backend");
        var backend = new BackendHost(job, output, log);
        backend.Configure(new BackendTarget(true, node.Executable!, installed.Folder, paths.BackendData, 0));
        backend.Start();
        Assert.True(await backend.WaitReadyAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken), string.Join('\n', log.Snapshot().Concat(output.Snapshot())));
        await backend.StopAsync();
        TestContext.Current.SendDiagnosticMessage(string.Join('\n', log.Snapshot()));
    }

    private static string Release(string tag, params (string Name, string Content)[] assets) =>
        $$"""{"tag_name":"{{tag}}","published_at":"2026-10-20T10:00:00Z","assets":[{{string.Join(',', assets.Select(asset =>
            $$"""{"name":"{{asset.Name}}","size":{{Encoding.UTF8.GetByteCount(asset.Content)}},"digest":"sha256:{{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(asset.Content)))}}","browser_download_url":"https://example.test/{{asset.Name}}"}"""))}}]}""";

    private static string Zip(string path, params (string Name, string Content)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }

        return path;
    }

    private static GitHubClient Github(Routes routes) => new(new HttpClient(routes));

    private static Func<HttpResponseMessage> Json(string json) => () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static Func<HttpResponseMessage> Text(string text) => () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) };

    private static Func<HttpResponseMessage> Bytes(byte[] bytes) => () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class Routes : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = [];

        public Func<HttpResponseMessage> this[string url]
        {
            set => _routes[url] = value;
        }

        public void Remove(string url) => _routes.Remove(url);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_routes.TryGetValue(request.RequestUri!.ToString(), out var respond) ? respond() : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
