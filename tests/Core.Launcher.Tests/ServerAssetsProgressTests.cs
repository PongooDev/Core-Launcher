using System.Net;
using System.Net.Http.Headers;
using Core.Launcher.Hosting.Components;
using Microsoft.Extensions.Time.Testing;

namespace Core.Launcher.Tests;

/// <summary>Progress, stall timeout and resume of the server assets download (and of <see cref="Downloads"/> under it).</summary>
public sealed partial class ServerAssetsTests
{
    private static readonly byte[] BigBody = Enumerable.Range(0, 1_000_000).Select(index => (byte)((index * 31) ^ (index >> 8))).ToArray();

    private static DownloadOptions Quick(Action<long, long?>? received = null, int attempts = 1, TimeSpan? stall = null, Action<long, long>? hashed = null, bool resume = true) => new()
    {
        StallTimeout = stall,
        Resume = resume,
        Attempts = attempts,
        RetryDelay = _ => TimeSpan.Zero,
        Received = received,
        Hashed = hashed,
    };

    [Fact]
    public async Task ReportsEveryByteReceivedAndHashed()
    {
        using var folder = new TempFolder();
        var server = new RangeServer(BigBody) { ChunkSize = 100_000 };
        var received = new List<(long Done, long? Total)>();
        var hashed = new List<(long Done, long Total)>();
        var temporary = folder.Combine("pak.download");

        await Downloads.VerifiedAsync(new HttpClient(server), "https://example.test/pak", temporary, "pak", BigBody.Length, Hash(BigBody), null, TestContext.Current.CancellationToken,
            Quick((done, total) => received.Add((done, total)), hashed: (done, total) => hashed.Add((done, total))));

        Assert.Equal(BigBody, File.ReadAllBytes(temporary));
        Assert.Equal((0L, (long?)BigBody.Length), received[0]);
        Assert.Equal((long)BigBody.Length, received[^1].Done);
        Assert.All(received, item => Assert.Equal(BigBody.Length, item.Total));
        Assert.Equal(received.Select(item => item.Done).Order(), received.Select(item => item.Done));
        Assert.Equal(11, received.Count);
        Assert.Equal((0L, (long)BigBody.Length), hashed[0]);
        Assert.Equal(((long)BigBody.Length, (long)BigBody.Length), hashed[^1]);
    }

    [Fact]
    public async Task FailsWhenNoBytesArriveForTheStallTimeout()
    {
        using var folder = new TempFolder();
        var server = new RangeServer(BigBody) { Plan = _ => (StallAfter: 1000, CutAfter: null) };

        var failure = await Assert.ThrowsAsync<DownloadStalledException>(() => Downloads.VerifiedAsync(new HttpClient(server), "https://example.test/pak", folder.Combine("pak.download"), "pak",
            BigBody.Length, Hash(BigBody), null, TestContext.Current.CancellationToken, Quick(stall: TimeSpan.FromMilliseconds(300))));

        Assert.Contains("no data received", failure.Message);
        Assert.Equal(1000, new FileInfo(folder.Combine("pak.download")).Length);
    }

    [Fact]
    public async Task ASlowButSteadyDownloadIsNotAStall()
    {
        using var folder = new TempFolder();
        var body = BigBody[..2000];
        var server = new RangeServer(body) { ChunkSize = 500, ReadDelay = TimeSpan.FromMilliseconds(120) };

        await Downloads.VerifiedAsync(new HttpClient(server), "https://example.test/pak", folder.Combine("pak.download"), "pak", body.Length, Hash(body), null,
            TestContext.Current.CancellationToken, Quick(stall: TimeSpan.FromMilliseconds(400)));

        Assert.Equal(body, File.ReadAllBytes(folder.Combine("pak.download")));
    }

    [Fact]
    public async Task ResumesAStalledDownloadWithARangeRequest()
    {
        using var folder = new TempFolder();
        var server = new RangeServer(BigBody) { Plan = index => index == 0 ? (StallAfter: 400_000, CutAfter: null) : (null, null), ChunkSize = 100_000 };
        var retries = new List<int>();
        var options = new DownloadOptions
        {
            StallTimeout = TimeSpan.FromMilliseconds(300),
            Resume = true,
            Attempts = 3,
            RetryDelay = _ => TimeSpan.Zero,
            Retrying = (attempt, _) => retries.Add(attempt),
        };

        await Downloads.VerifiedAsync(new HttpClient(server), "https://example.test/pak", folder.Combine("pak.download"), "pak", BigBody.Length, Hash(BigBody), null, TestContext.Current.CancellationToken, options);

        Assert.Equal(BigBody, File.ReadAllBytes(folder.Combine("pak.download")));
        Assert.Equal([1], retries);
        Assert.Null(server.Ranges[0]);
        Assert.Equal(400_000, server.Ranges[1]!.Ranges.Single().From);
        Assert.Equal(2, server.Ranges.Count);
    }

    [Fact]
    public async Task ResumesAConnectionThatDropsMidway()
    {
        using var folder = new TempFolder();
        var server = new RangeServer(BigBody) { Plan = index => index < 2 ? (StallAfter: null, CutAfter: 250_000 * (index + 1)) : (null, null), ChunkSize = 50_000 };

        await Downloads.VerifiedAsync(new HttpClient(server), "https://example.test/pak", folder.Combine("pak.download"), "pak", BigBody.Length, Hash(BigBody), null, TestContext.Current.CancellationToken, Quick(attempts: 3));

        Assert.Equal(BigBody, File.ReadAllBytes(folder.Combine("pak.download")));
        Assert.Equal([0L, 250_000L, 750_000L], server.Ranges.Select(range => range?.Ranges.Single().From ?? 0L));
    }

    [Fact]
    public async Task GivesUpAfterTheLastAttempt()
    {
        using var folder = new TempFolder();
        var server = new RangeServer(BigBody) { Plan = _ => (StallAfter: null, CutAfter: 1000) };

        await Assert.ThrowsAsync<IOException>(() => Downloads.VerifiedAsync(new HttpClient(server), "https://example.test/pak", folder.Combine("pak.download"), "pak", BigBody.Length, Hash(BigBody), null,
            TestContext.Current.CancellationToken, Quick(attempts: 2)));

        Assert.Equal(2, server.Ranges.Count);
    }

    [Fact]
    public async Task ResumesAnExistingPartialFile()
    {
        using var folder = new TempFolder();
        var temporary = folder.Combine("pak.download");
        File.WriteAllBytes(temporary, BigBody[..600_000]);
        var server = new RangeServer(BigBody);
        var first = -1L;

        await Downloads.VerifiedAsync(new HttpClient(server), "https://example.test/pak", temporary, "pak", BigBody.Length, Hash(BigBody), null, TestContext.Current.CancellationToken,
            Quick((done, _) => first = first < 0 ? done : first));

        Assert.Equal(BigBody, File.ReadAllBytes(temporary));
        Assert.Equal(600_000, server.Ranges.Single()!.Ranges.Single().From);
        Assert.Equal(600_000, first);
    }

    [Fact]
    public async Task StartsOverWhenTheServerIgnoresRange()
    {
        using var folder = new TempFolder();
        var temporary = folder.Combine("pak.download");
        File.WriteAllBytes(temporary, BigBody[..600_000]);
        var server = new RangeServer(BigBody) { SupportsRange = false };

        await Downloads.VerifiedAsync(new HttpClient(server), "https://example.test/pak", temporary, "pak", BigBody.Length, Hash(BigBody), null, TestContext.Current.CancellationToken, Quick());

        Assert.Equal(BigBody, File.ReadAllBytes(temporary));
    }

    [Fact]
    public async Task StartsOverWhenAResumedFileFailsItsCheck()
    {
        using var folder = new TempFolder();
        var temporary = folder.Combine("pak.download");
        File.WriteAllBytes(temporary, new byte[600_000]);
        var server = new RangeServer(BigBody);

        await Downloads.VerifiedAsync(new HttpClient(server), "https://example.test/pak", temporary, "pak", BigBody.Length, Hash(BigBody), null, TestContext.Current.CancellationToken, Quick());

        Assert.Equal(BigBody, File.ReadAllBytes(temporary));
        Assert.Equal(2, server.Ranges.Count);
        Assert.Null(server.Ranges[1]);
    }

    [Fact]
    public async Task NeverKeepsAFreshDownloadThatFailsItsCheck()
    {
        using var folder = new TempFolder();
        var server = new RangeServer(BigBody);

        await Assert.ThrowsAsync<InvalidDataException>(() => Downloads.VerifiedAsync(new HttpClient(server), "https://example.test/pak", folder.Combine("pak.download"), "pak",
            BigBody.Length, new string('0', 64), null, TestContext.Current.CancellationToken, Quick(attempts: 3)));

        Assert.Single(server.Ranges);
    }

    [Fact]
    public void DescribesTheDownload()
    {
        const long MB = 1048576;
        var downloading = new ServerAssetsProgress(ServerAssetsPhase.Downloading, "4.0", "pak", 412 * MB, 922 * MB, 8.1 * MB, TimeSpan.FromSeconds(63));
        Assert.Equal("Downloading server assets for 4.0 – 412 MB / 922 MB (45%) · 8.1 MB/s · 1 min left", downloading.Describe());
        Assert.Equal(45, Math.Round(downloading.Percent));
        Assert.True(downloading.HasFraction);

        Assert.Equal("Downloading server assets for 4.0 – 0.0 MB / 922 MB (0%)", (downloading with { BytesDone = 0, BytesPerSecond = 0, Remaining = null }).Describe());
        Assert.Equal("Downloading server assets for 4.0 – 0.50 GB / 1.00 GB (50%) · 2.5 MB/s · 45 s left",
            new ServerAssetsProgress(ServerAssetsPhase.Downloading, "4.0", "pak", 512 * MB, 1024 * MB, 2.5 * MB, TimeSpan.FromSeconds(45)).Describe());
        Assert.Equal("Verifying server assets for 4.0 – 50%", new ServerAssetsProgress(ServerAssetsPhase.Verifying, "4.0", "pak", 461 * MB, 922 * MB).Describe());
        Assert.Equal("Installing server assets for 4.0", new ServerAssetsProgress(ServerAssetsPhase.Installing, "4.0").Describe());
        Assert.Equal("Server assets for 4.0 failed: no data received for 60 s", new ServerAssetsProgress(ServerAssetsPhase.Failed, "4.0", Error: "no data received for 60 s").Describe());
        Assert.Equal("", ServerAssetsProgress.None.Describe());
        Assert.False(ServerAssetsProgress.None.Visible);
        Assert.True(new ServerAssetsProgress(ServerAssetsPhase.Failed).Visible);
    }

    [Fact]
    public void TheServerHostDashboardShowsTheDownload()
    {
        var build = new Core.Launcher.Hosting.Settings.BuildSettings { Name = "Fortnite 4.0", Changelist = 4039451, Path = @"D:\Fortnite", Executable = @"FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe" };
        const long MB = 1048576;

        List<string> Frame(ServerAssetsProgress? assets) => Core.Server.Cli.Terminal.DashboardRenderer.BuildFrame(new Core.Server.Cli.Hosting.ServerHostStatus(
            build,
            new Core.Launcher.Hosting.Backend.BackendEndpoint("127.0.0.1", 5595),
            "OCE",
            "CoreServer1",
            2,
            [],
            new Core.Server.Cli.Metrics.SystemSnapshot(10, 8L * 1024 * 1024 * 1024, 16L * 1024 * 1024 * 1024),
            new Core.Server.Cli.Metrics.TotalProcessSnapshot(0, 0, new Dictionary<int, Core.Server.Cli.Metrics.ProcessSnapshot>()),
            [],
            ServerAssets: assets));

        var text = string.Join(" ", Frame(new ServerAssetsProgress(ServerAssetsPhase.Downloading, "4.0", "pak", 412 * MB, 922 * MB, 8.1 * MB, TimeSpan.FromSeconds(63))));
        Assert.Contains("Server Assets", text);
        Assert.Contains("412 MB / 922 MB (45%)", text);

        Assert.Contains("restart CoreServerHost to retry", string.Join(" ", Frame(new ServerAssetsProgress(ServerAssetsPhase.Failed, "4.0", Error: "no data received for 60 s"))));
        Assert.DoesNotContain("Server Assets", string.Join(" ", Frame(null)));
        Assert.DoesNotContain("Server Assets", string.Join(" ", Frame(ServerAssetsProgress.None with { Phase = ServerAssetsPhase.Done })));
    }

    [Fact]
    public void ScalesTheOverallCapWithTheSize()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), ServerAssets.DownloadCap(0));
        Assert.InRange(ServerAssets.DownloadCap(1L << 30).TotalHours, 2.4, 2.6);
        Assert.True(ServerAssets.DownloadCap(2L << 30) > ServerAssets.DownloadCap(1L << 30));
    }

    [Fact]
    public async Task ReportsPhasesSpeedAndTimeLeftWhileInstalling()
    {
        using var folder = new TempFolder();
        var (build, paks) = Build(folder);
        var body = BigBody[..1024];
        var time = new FakeTimeProvider();
        var server = new RangeServer(body) { ChunkSize = 256, OnRead = () => time.Advance(TimeSpan.FromSeconds(1)), ManifestJson = Manifest(Hash(body), body.Length) };
        var assets = new ServerAssets(new HttpClient(server), folder.Combine("Cache"), _ => { }, time: time);
        var seen = new List<ServerAssetsProgress>();
        assets.ProgressChanged += () => seen.Add(assets.Progress);

        Assert.Equal(ServerAssetsProgress.None, assets.Progress);
        var result = await assets.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken);

        Assert.Contains("installed", result);
        Assert.Equal(body, File.ReadAllBytes(Path.Combine(paks, "pakchunkCoreServerAssets-WindowsClient_P.pak")));
        Assert.Equal(["Checking", "Downloading", "Verifying", "Installing", "Done"], seen.Select(item => item.Phase.ToString()).Distinct());
        Assert.All(seen.Where(item => item.Phase == ServerAssetsPhase.Downloading), item => Assert.Equal("3.0", item.Version));
        var quarter = seen.Last(item => item.Phase == ServerAssetsPhase.Downloading && item.BytesDone == 256);
        Assert.Equal(256, quarter.BytesPerSecond, 1);
        Assert.Equal(TimeSpan.FromSeconds(3), quarter.Remaining);
        Assert.Equal(1024, seen.Last(item => item.Phase == ServerAssetsPhase.Verifying).BytesDone);
        Assert.Equal(ServerAssetsPhase.Done, assets.Progress.Phase);
        Assert.False(assets.Progress.Visible);
        Assert.Empty(Directory.GetFiles(paks, "*.download"));
    }

    [Fact]
    public async Task ThrottlesByteProgressEvents()
    {
        using var folder = new TempFolder();
        var (build, _) = Build(folder);
        var time = new FakeTimeProvider();
        var server = new RangeServer(BigBody) { ChunkSize = 1000, ManifestJson = Manifest(Hash(BigBody), BigBody.Length) };
        var assets = new ServerAssets(new HttpClient(server), folder.Combine("Cache"), _ => { }, time: time);
        var raised = 0;
        assets.ProgressChanged += () => ++raised;

        await assets.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken);

        // A thousand chunks arrive within one frozen instant: far fewer events than that, one per phase.
        Assert.InRange(raised, 4, 12);
    }

    [Fact]
    public async Task KeepsAFailedDownloadForTheNextTryAndInstallsOnRetry()
    {
        using var folder = new TempFolder();
        var (build, paks) = Build(folder);
        var server = new RangeServer(BigBody) { Plan = index => index == 0 ? (StallAfter: null, CutAfter: 300_000) : (null, 0), ChunkSize = 100_000, ManifestJson = Manifest(Hash(BigBody), BigBody.Length) };
        var assets = new ServerAssets(new HttpClient(server), folder.Combine("Cache"), _ => { }, retryDelay: _ => TimeSpan.Zero);
        var finished = new SemaphoreSlim(0);
        assets.Checked += () => finished.Release();

        assets.Pending(build, 3901517, true, CancellationToken.None);
        Assert.True(await finished.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        Assert.Equal(ServerAssetsPhase.Failed, assets.Progress.Phase);
        Assert.Contains("failed", assets.Progress.Describe());
        Assert.Null(assets.Pending(build, 3901517, true, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(paks, "pakchunkCoreServerAssets-WindowsClient_P.pak")));
        var partial = Assert.Single(Directory.GetFiles(paks, "*.download"));
        Assert.Equal(300_000, new FileInfo(partial).Length);

        server.Plan = _ => (null, null);
        server.Ranges.Clear();
        assets.Retry();
        Assert.True(await finished.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        Assert.Equal(ServerAssetsPhase.Done, assets.Progress.Phase);
        Assert.Equal(BigBody, File.ReadAllBytes(Path.Combine(paks, "pakchunkCoreServerAssets-WindowsClient_P.pak")));
        Assert.Equal(300_000, server.Ranges.First()!.Ranges.Single().From);
        Assert.Empty(Directory.GetFiles(paks, "*.download"));
    }

    [Fact]
    public async Task RecoversFromAStalledConnectionWithoutFailing()
    {
        using var folder = new TempFolder();
        var (build, paks) = Build(folder);
        var server = new RangeServer(BigBody) { Plan = index => index == 0 ? (StallAfter: 200_000, CutAfter: null) : (null, null), ChunkSize = 100_000, ManifestJson = Manifest(Hash(BigBody), BigBody.Length) };
        var log = new List<string>();
        var assets = new ServerAssets(new HttpClient(server), folder.Combine("Cache"), log.Add, stallTimeout: TimeSpan.FromMilliseconds(300), retryDelay: _ => TimeSpan.Zero);

        var result = await assets.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken);

        Assert.Contains("installed", result);
        Assert.Equal(BigBody, File.ReadAllBytes(Path.Combine(paks, "pakchunkCoreServerAssets-WindowsClient_P.pak")));
        Assert.Equal(200_000, server.Ranges[1]!.Ranges.Single().From);
        Assert.Contains(log, line => line.Contains("no data received", StringComparison.Ordinal) && line.Contains("resuming", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DoesNotResumeAPartialFileOfAnotherPak()
    {
        using var folder = new TempFolder();
        var (build, paks) = Build(folder);
        File.WriteAllBytes(Path.Combine(paks, "pakchunkCoreServerAssets-WindowsClient_P.pak.deadbeef0000.download"), new byte[5000]);
        var server = new RangeServer(BigBody) { ManifestJson = Manifest(Hash(BigBody), BigBody.Length) };
        var assets = new ServerAssets(new HttpClient(server), folder.Combine("Cache"), _ => { });

        Assert.Contains("installed", await assets.EnsureAsync(build, 3901517, true, TestContext.Current.CancellationToken));

        Assert.Null(server.Ranges.Single());
        Assert.Empty(Directory.GetFiles(paks, "*.download"));
    }

    /// <summary>
    /// A server for one body that honours Range. <see cref="Plan"/> says, per request, where the body stalls or the
    /// connection is cut (counted in bytes of that response).
    /// </summary>
    private sealed class RangeServer(byte[] body) : HttpMessageHandler
    {
        private readonly Lock _gate = new();

        public List<RangeHeaderValue?> Ranges { get; } = [];

        public Func<int, (int? StallAfter, int? CutAfter)> Plan { get; set; } = _ => (null, null);

        public bool SupportsRange { get; init; } = true;

        public int ChunkSize { get; init; } = int.MaxValue;

        public TimeSpan ReadDelay { get; init; }

        public Action? OnRead { get; init; }

        /// <summary>The manifest served at <see cref="ServerAssets.ManifestUrl"/>; every other URL is the body.</summary>
        public string? ManifestJson { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.ToString() == ServerAssets.ManifestUrl)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ManifestJson ?? "") });
            }

            int index;
            lock (_gate)
            {
                index = Ranges.Count;
                Ranges.Add(request.Headers.Range);
            }

            var from = SupportsRange && request.Headers.Range?.Ranges.SingleOrDefault()?.From is { } start ? (int)start : 0;
            if (from >= body.Length)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            }

            var (stall, cut) = Plan(index);
            var slice = body[from..];
            var content = new StreamContent(new PlanStream(slice, stall, cut, ChunkSize, ReadDelay, OnRead));
            content.Headers.ContentLength = slice.Length;
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
            if (from > 0)
            {
                content.Headers.ContentRange = new ContentRangeHeaderValue(from, body.Length - 1, body.Length);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class PlanStream(byte[] data, int? stallAfter, int? cutAfter, int chunk, TimeSpan delay, Action? onRead) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            onRead?.Invoke();
            if (cutAfter is { } cut && _position >= cut)
            {
                throw new IOException("the connection was reset");
            }

            if (stallAfter is { } stall && _position >= stall)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            var limit = Math.Min(data.Length, Math.Min(cutAfter ?? int.MaxValue, stallAfter ?? int.MaxValue));
            var count = Math.Min(Math.Min(buffer.Length, chunk), limit - _position);
            if (count <= 0)
            {
                return 0;
            }

            data.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
