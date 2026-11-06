using System.Globalization;
using System.Text.Json;

namespace Core.Launcher.Hosting.Components;

/// <summary>A release asset the manifest names: its file name, download URL, SHA-256 and size.</summary>
public sealed record ServerAssetsFile(string File, string Url, string Sha256, long Size);

/// <summary>
/// One build's server assets pak in the Core-ServerAssets manifest, and its <see cref="Sig"/>: the signature file a
/// build that signs its paks needs next to every pak (null for builds that don't).
/// </summary>
public sealed record ServerAssetsEntry(int Changelist, string Version, string Pak, string Url, string Sha256, long Size, IReadOnlyList<string> Contents, ServerAssetsFile? Sig = null);

/// <summary>
/// Core-ServerAssets' manifest.json: <c>{ "schema": 1, "builds": { "&lt;changelist&gt;": { "version", "pak", "url",
/// "sha256", "size", "contents", "sig"? } } }</c>, where the optional <c>sig</c> is <c>{ "file", "url", "sha256", "size" }</c>.
/// </summary>
public sealed record ServerAssetsManifest(int Schema, IReadOnlyDictionary<int, ServerAssetsEntry> Builds)
{
    /// <summary>
    /// The newest schema this launcher reads. Schema 2 added <c>sig</c>, and a manifest with a sig must say schema 2:
    /// launchers before 1.0.14 would ignore a sig, installing a pak its build refuses to start with, but they (and
    /// 1.0.14) read only schema 1, so on schema 2 they read no manifest at all and install nothing.
    /// </summary>
    public const int SupportedSchema = 2;

    /// <summary>The first schema with <c>sig</c>.</summary>
    public const int SigSchema = 2;

    public ServerAssetsEntry? For(int changelist) => Builds.GetValueOrDefault(changelist);

    /// <summary>Reads a manifest, refusing it (<see cref="InvalidDataException"/>) unless every entry is complete and safe to download.</summary>
    public static ServerAssetsManifest Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schemaValue) || !schemaValue.TryGetInt32(out var schema))
            {
                throw new InvalidDataException("manifest.json has no schema");
            }

            if (schema < 1 || schema > SupportedSchema)
            {
                throw new InvalidDataException($"manifest.json is schema {schema}; this launcher reads schemas 1 to {SupportedSchema} (update the launcher)");
            }

            var builds = new Dictionary<int, ServerAssetsEntry>();
            if (root.TryGetProperty("builds", out var list))
            {
                if (list.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("manifest.json's builds isn't an object");
                }

                foreach (var build in list.EnumerateObject())
                {
                    var changelist = ParseChangelist(build.Name);
                    builds[changelist] = ParseEntry(schema, changelist, build.Value);
                }
            }

            return new ServerAssetsManifest(schema, builds);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"manifest.json isn't valid JSON: {exception.Message}", exception);
        }
    }

    private static int ParseChangelist(string name) =>
        name.Length > 0 && name.All(char.IsAsciiDigit) && int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var changelist) && changelist > 0
            ? changelist
            : throw new InvalidDataException($"manifest.json: \"{name}\" isn't a changelist");

    private static ServerAssetsEntry ParseEntry(int schema, int changelist, JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"manifest.json: CL {changelist} isn't an object");
        }

        var pak = ParseFile(changelist, "pak", entry, "pak", ".pak");
        ServerAssetsFile? sig = null;
        if (entry.TryGetProperty("sig", out var sigValue))
        {
            if (schema < SigSchema)
            {
                throw new InvalidDataException($"manifest.json: CL {changelist} has a sig, which needs schema {SigSchema} (older launchers would install its pak without it)");
            }

            if (sigValue.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"manifest.json: CL {changelist}'s sig isn't an object");
            }

            sig = ParseFile(changelist, "sig", sigValue, "file", ".sig");
        }

        var version = entry.TryGetProperty("version", out var versionValue) && versionValue.ValueKind == JsonValueKind.String ? versionValue.GetString() ?? "" : "";
        var contents = entry.TryGetProperty("contents", out var contentsValue) && contentsValue.ValueKind == JsonValueKind.Array
            ? contentsValue.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList()
            : [];
        return new ServerAssetsEntry(changelist, version, pak.File, pak.Url, pak.Sha256, pak.Size, contents, sig);
    }

    /// <summary>
    /// One downloadable file of an entry (<paramref name="what"/> is "pak" or "sig"): <paramref name="nameField"/>, a bare
    /// file name ending in <paramref name="extension"/>, and its url, sha256 and size.
    /// </summary>
    private static ServerAssetsFile ParseFile(int changelist, string what, JsonElement element, string nameField, string extension)
    {
        string Text(string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                ? text
                : throw new InvalidDataException($"manifest.json: CL {changelist}'s {what} has no {name}");

        var file = Text(nameField);
        if (!file.EndsWith(extension, StringComparison.OrdinalIgnoreCase) || file.IndexOfAny(['/', '\\', ':']) >= 0 || file.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"manifest.json: CL {changelist}'s {what} \"{file}\" isn't a {extension} file name");
        }

        // Only Core-ServerAssets' own releases: the manifest lives there, so a file from anywhere else is a mistake.
        var url = Text("url");
        if (!url.StartsWith(ServerAssets.ReleaseUrlPrefix, StringComparison.Ordinal) || !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            throw new InvalidDataException($"manifest.json: CL {changelist}'s {what} url isn't a {ServerAssets.Repository} release download");
        }

        var sha256 = Text("sha256").ToLowerInvariant();
        if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException($"manifest.json: CL {changelist}'s {what} sha256 isn't 64 hex digits");
        }

        if (!element.TryGetProperty("size", out var sizeValue) || !sizeValue.TryGetInt64(out var size) || size <= 0)
        {
            throw new InvalidDataException($"manifest.json: CL {changelist}'s {what} has no size");
        }

        return new ServerAssetsFile(file, url, sha256, size);
    }
}

public enum ServerAssetsAction
{
    /// <summary>Nothing to do: nothing is installed and nothing should be.</summary>
    None,

    /// <summary>The installed pak is the manifest's.</summary>
    UpToDate,

    /// <summary>Download the manifest's pak (none installed, or the installed one differs).</summary>
    Install,

    /// <summary>Delete Core's pak: the feature is off or the manifest no longer lists the build.</summary>
    Remove,

    /// <summary>Leave the installed pak: the manifest couldn't be read, so there's no telling whether it's current.</summary>
    Keep,
}

public sealed record ServerAssetsDecision(ServerAssetsAction Action, string Reason);

/// <summary>An installed file's size and SHA-256.</summary>
public sealed record InstalledFile(long Size, string Sha256);

/// <summary>What is installed in a build's Paks folder under Core's name: the pak's size and SHA-256, and its sig's, if any.</summary>
public sealed record InstalledPak(long Size, string Sha256, InstalledFile? Sig = null);

/// <summary>
/// Server-only assets for hosted servers (Core-ServerAssets): before a build's first server starts, reads the manifest,
/// and installs the build's verified pak (with its sig, when the manifest has one) into its FortniteGame\Content\Paks
/// under Core's own name, or removes them when the build is no longer listed or the feature is off. Only Core's own
/// files are ever written or deleted, and network
/// trouble never stops a server from starting.
/// </summary>
public sealed class ServerAssets
{
    public const string Repository = "PongooDev/Core-ServerAssets";

    public const string ManifestUrl = $"https://raw.githubusercontent.com/{Repository}/main/manifest.json";

    public const string ReleaseUrlPrefix = $"https://github.com/{Repository}/releases/download/";

    /// <summary>Core's pak, before the platform: pakchunkCoreServerAssets-WindowsClient_P.pak.</summary>
    public const string PakPrefix = "pakchunkCoreServerAssets-";

    public static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>A download fails when no byte arrives for this long (it is then tried again, resuming).</summary>
    public static readonly TimeSpan DownloadStallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How many times a download is tried: each try after a failure resumes the partial file.</summary>
    public const int DownloadAttempts = 5;

    /// <summary>The slowest line the overall cap allows for: 128 KiB/s.</summary>
    private const double SlowestBytesPerSecond = 128 * 1024;

    /// <summary>The overall cap on a download with its retries: ten minutes and the time <paramref name="size"/> takes at 128 KiB/s (about 2.5 hours for 1 GB).</summary>
    public static TimeSpan DownloadCap(long size) => TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(size / SlowestBytesPerSecond);

    /// <summary>The most often <see cref="ProgressChanged"/> is raised for byte counts (phase changes always raise it).</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly string _cacheFolder;
    private readonly Action<string> _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _stallTimeout;
    private readonly Func<int, TimeSpan>? _retryDelay;
    private readonly Lock _gate = new();
    private string? _key;
    private Task<string>? _check;
    private (string BuildRoot, int Changelist, bool Enabled, CancellationToken Cancellation)? _last;
    private ServerAssetsProgress _progress = ServerAssetsProgress.None;
    private long _raised;
    private readonly Queue<(long Time, long Bytes)> _samples = new();

    /// <param name="stallTimeout">Overrides <see cref="DownloadStallTimeout"/> (tests).</param>
    /// <param name="time">The clock for speeds and the progress throttle (tests).</param>
    /// <param name="retryDelay">Overrides the wait between download tries (tests).</param>
    public ServerAssets(HttpClient http, string cacheFolder, Action<string> log, TimeSpan? stallTimeout = null, TimeProvider? time = null, Func<int, TimeSpan>? retryDelay = null)
    {
        _http = http;
        _cacheFolder = cacheFolder;
        _log = log;
        _time = time ?? TimeProvider.System;
        _stallTimeout = stallTimeout ?? DownloadStallTimeout;
        _retryDelay = retryDelay;
    }

    /// <summary>The last good manifest, used when GitHub can't be reached.</summary>
    public string CachedManifest => Path.Combine(_cacheFolder, "manifest.json");

    /// <summary>What the last check did, for the server logs; empty until one has finished.</summary>
    public string Status
    {
        get
        {
            lock (_gate)
            {
                return _check is { IsCompletedSuccessfully: true } check ? check.Result : "";
            }
        }
    }

    /// <summary>Raised when a check finishes.</summary>
    public event Action? Checked;

    /// <summary>Where the check is: phase, bytes, speed and time left (<see cref="ServerAssetsProgress.None"/> before the first).</summary>
    public ServerAssetsProgress Progress
    {
        get
        {
            lock (_gate)
            {
                return _progress;
            }
        }
    }

    /// <summary>
    /// Raised when <see cref="Progress"/> changes: at once for a new phase, at most every <see cref="ProgressInterval"/>
    /// for byte counts. Raised on whichever thread is downloading.
    /// </summary>
    public event Action? ProgressChanged;

    /// <summary>
    /// Runs the last check again, for after a failure: the partial download is resumed. Does nothing while a check is
    /// under way or before the first one. Servers already running keep starting without the pak until they restart.
    /// </summary>
    public void Retry()
    {
        lock (_gate)
        {
            if (_last is not { } last || _check is { IsCompleted: false })
            {
                return;
            }

            Begin(last.BuildRoot, last.Changelist, last.Enabled, last.Cancellation);
        }
    }

    private void Publish(ServerAssetsProgress progress, bool force)
    {
        lock (_gate)
        {
            _progress = progress;
            var now = _time.GetTimestamp();
            if (!force && _time.GetElapsedTime(_raised, now) < ProgressInterval)
            {
                return;
            }

            _raised = now;
        }

        ProgressChanged?.Invoke();
    }

    /// <summary>The download's state as <paramref name="received"/> of <paramref name="total"/> bytes arrive, with the speed over the last few seconds.</summary>
    private void Downloading(ServerAssetsEntry entry, string file, long received, long? total)
    {
        double speed = 0;
        TimeSpan? remaining = null;
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            if (_samples.Count == 0 || _time.GetElapsedTime(_samples.Last().Time, now) >= TimeSpan.FromMilliseconds(100))
            {
                _samples.Enqueue((now, received));
            }

            while (_samples.Count > 2 && _time.GetElapsedTime(_samples.Peek().Time, now) > SpeedWindow)
            {
                _samples.Dequeue();
            }

            var oldest = _samples.Peek();
            var seconds = _time.GetElapsedTime(oldest.Time, now).TotalSeconds;
            if (seconds >= 1 && received > oldest.Bytes)
            {
                speed = (received - oldest.Bytes) / seconds;
                if (total is { } all && all > received)
                {
                    remaining = TimeSpan.FromSeconds((all - received) / speed);
                }
            }
        }

        Publish(new ServerAssetsProgress(ServerAssetsPhase.Downloading, entry.Version, file, received, total ?? 0, speed, remaining), received == total);
    }

    public static string PaksFolder(string buildRoot) => Path.Combine(buildRoot, "FortniteGame", "Content", "Paks");

    /// <summary>
    /// Core's pak name in <paramref name="paksFolder"/>: the build's own platform (pakchunk0-WindowsClient.pak gives
    /// WindowsClient), and _P, the engine's patch suffix, so it mounts above the client paks and its assets win over
    /// the stripped copies some of them hold.
    /// </summary>
    public static string PakFileName(string paksFolder)
    {
        var platform = "WindowsClient";
        if (Directory.Exists(paksFolder))
        {
            var game = Directory.EnumerateFiles(paksFolder, "pakchunk*-*.pak")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => name is not null && !name.StartsWith(PakPrefix, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (game is not null)
            {
                var suffix = game[(game.LastIndexOf('-') + 1)..];
                suffix = suffix.EndsWith("_P", StringComparison.OrdinalIgnoreCase) ? suffix[..^2] : suffix;
                if (suffix.Length > 0 && suffix.All(char.IsAsciiLetterOrDigit))
                {
                    platform = suffix;
                }
            }
        }

        return $"{PakPrefix}{platform}_P.pak";
    }

    public static ServerAssetsDecision Decide(bool enabled, int changelist, ServerAssetsManifest? manifest, InstalledPak? installed)
    {
        if (!enabled)
        {
            return installed is null
                ? new(ServerAssetsAction.None, "turned off (Settings → Servers)")
                : new(ServerAssetsAction.Remove, "turned off (Settings → Servers)");
        }

        if (changelist <= 0)
        {
            return installed is null
                ? new(ServerAssetsAction.None, "the build's changelist is unknown")
                : new(ServerAssetsAction.Keep, "the build's changelist is unknown, keeping the installed pak");
        }

        if (manifest is null)
        {
            return installed is null
                ? new(ServerAssetsAction.None, "the manifest couldn't be read")
                : new(ServerAssetsAction.Keep, "the manifest couldn't be read, keeping the installed pak");
        }

        if (manifest.For(changelist) is not { } entry)
        {
            return installed is null
                ? new(ServerAssetsAction.None, $"none published for CL-{changelist} yet")
                : new(ServerAssetsAction.Remove, $"CL-{changelist} is no longer in the manifest");
        }

        // Up to date only when the pak matches and so does its sig: the manifest's, or none when the manifest has none.
        if (installed is not null && Matches(installed.Size, installed.Sha256, entry.Size, entry.Sha256)
            && (entry.Sig is null ? installed.Sig is null : installed.Sig is { } sig && Matches(sig.Size, sig.Sha256, entry.Sig.Size, entry.Sig.Sha256)))
        {
            return new(ServerAssetsAction.UpToDate, $"{entry.Pak}{(entry.Sig is null ? "" : $" and {entry.Sig.File}")} for CL-{changelist} is installed");
        }

        return new(ServerAssetsAction.Install, installed is null ? $"installing {entry.Pak} for CL-{changelist}" : $"updating to {entry.Pak} for CL-{changelist}");
    }

    private static bool Matches(long size, string sha256, long expectedSize, string expectedSha256) =>
        size == expectedSize && string.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>The sig that goes with Core's pak: the same name with .sig, as the engine looks for it.</summary>
    public static string SigPath(string pakPath) => Path.ChangeExtension(pakPath, ".sig");

    /// <summary>
    /// Null once the server assets of this build and setting have been checked, otherwise why servers wait. The first
    /// call for a build (or after <see cref="Reset"/>) starts the check.
    /// </summary>
    public string? Pending(string buildRoot, int changelist, bool enabled, CancellationToken cancellation)
    {
        lock (_gate)
        {
            var key = $"{Path.GetFullPath(buildRoot)}|{changelist}|{enabled}";
            if (_key != key || _check is null)
            {
                Begin(buildRoot, changelist, enabled, cancellation);
            }

            return _check!.IsCompleted ? null : _progress.Describe() is { Length: > 0 } line ? line : "checking server assets";
        }
    }

    /// <summary>Starts a check; the caller holds the lock.</summary>
    private void Begin(string buildRoot, int changelist, bool enabled, CancellationToken cancellation)
    {
        _key = $"{Path.GetFullPath(buildRoot)}|{changelist}|{enabled}";
        _last = (buildRoot, changelist, enabled, cancellation);
        _samples.Clear();
        _progress = new ServerAssetsProgress(ServerAssetsPhase.Checking);
        var check = Task.Run(() => EnsureAsync(buildRoot, changelist, enabled, cancellation));
        _check = check;
        check.ContinueWith(_ => Checked?.Invoke(), TaskScheduler.Default);
    }

    /// <summary>Makes the next <see cref="Pending"/> check again (settings changed).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _key = null;
        }
    }

    /// <summary>Installs, keeps or removes the build's pak; returns what it did. Never throws: failures are logged and returned.</summary>
    public async Task<string> EnsureAsync(string buildRoot, int changelist, bool enabled, CancellationToken cancellation)
    {
        string result;
        try
        {
            Publish(new ServerAssetsProgress(ServerAssetsPhase.Checking), true);
            result = await EnsureCoreAsync(buildRoot, changelist, enabled, cancellation);
            Publish(ServerAssetsProgress.None with { Phase = ServerAssetsPhase.Done }, true);
        }
        catch (Exception exception)
        {
            var reason = exception is OperationCanceledException && !cancellation.IsCancellationRequested ? "took too long" : exception.Message;
            result = $"skipped ({reason}); the server starts without them";
            Publish(new ServerAssetsProgress(ServerAssetsPhase.Failed, Progress.Version, Progress.File, Error: reason), true);
        }

        _log($"Server assets: {result}");
        return result;
    }

    private async Task<string> EnsureCoreAsync(string buildRoot, int changelist, bool enabled, CancellationToken cancellation)
    {
        var paks = PaksFolder(buildRoot);
        if (!Directory.Exists(paks))
        {
            return $"skipped: {paks} doesn't exist";
        }

        var destination = Path.Combine(paks, PakFileName(paks));
        var installed = await InstalledAsync(destination, cancellation);
        var manifest = enabled && changelist > 0 ? await ManifestAsync(cancellation) : null;
        if (installed is null)
        {
            // No pak: a sig or an unfinished download left behind on its own goes, except the partial pak that the
            // manifest's entry resumes.
            Remove(paks, manifest?.For(changelist) is { } wanted ? [Path.GetFileName(PartialPath(destination, wanted.Sha256))] : null);
        }

        var decision = Decide(enabled, changelist, manifest, installed);
        switch (decision.Action)
        {
            case ServerAssetsAction.Remove:
                Remove(paks);
                return $"removed {Path.GetFileName(destination)}{(installed?.Sig is null ? "" : $" and {Path.GetFileName(SigPath(destination))}")}: {decision.Reason}";
            case ServerAssetsAction.Install:
                var entry = manifest!.For(changelist)!;
                Publish(new ServerAssetsProgress(ServerAssetsPhase.Downloading, entry.Version, entry.Pak, 0, entry.Size), true);
                Remove(paks, [Path.GetFileName(PartialPath(destination, entry.Sha256))], partialsOnly: true);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    timeout.CancelAfter(DownloadCap(entry.Size));
                    await InstallAsync(entry, destination, timeout.Token);
                }

                var sig = entry.Sig is null ? "" : $", with {Path.GetFileName(SigPath(destination))} ({entry.Sig.Size} bytes, SHA-256 {entry.Sig.Sha256})";
                return $"{decision.Reason}: installed {Path.GetFileName(destination)} ({entry.Size} bytes, SHA-256 {entry.Sha256}{sig}; {string.Join(", ", entry.Contents)})";
            default:
                return decision.Action == ServerAssetsAction.UpToDate ? $"{decision.Reason} as {Path.GetFileName(destination)}" : $"none: {decision.Reason}";
        }
    }

    /// <summary>The unfinished download of a pak with this SHA-256; the hash in the name keeps a partial of an older entry from being resumed.</summary>
    private static string PartialPath(string path, string sha256) => $"{path}.{sha256[..12]}.download";

    private DownloadOptions Options(ServerAssetsEntry entry, string file, long size) => new()
    {
        StallTimeout = _stallTimeout,
        Resume = true,
        Attempts = DownloadAttempts,
        RetryDelay = _retryDelay,
        Received = (received, total) => Downloading(entry, file, received, total ?? size),
        Hashed = (hashed, total) => Publish(new ServerAssetsProgress(ServerAssetsPhase.Verifying, entry.Version, file, hashed, total), hashed == total),
        Retrying = (attempt, exception) => _log($"Server assets: {file}: {exception.Message}; trying again ({attempt + 1} of {DownloadAttempts}), resuming"),
    };

    /// <summary>
    /// Installs the entry's pak, and its sig when it has one, as one: both are downloaded to temporary files and checked
    /// against the manifest before either is moved into place, so a pak never sits in the Paks folder without its sig
    /// (on a build that signs its paks, the engine refuses to start with an unsigned pak). A pak that fails to download
    /// part way stays as a partial file for the next try to resume; one that fails its checks is deleted.
    /// </summary>
    private async Task InstallAsync(ServerAssetsEntry entry, string destination, CancellationToken cancellation)
    {
        var sigDestination = SigPath(destination);
        var pakTemporary = PartialPath(destination, entry.Sha256);
        var sigTemporary = sigDestination + ".download";
        try
        {
            await DownloadAsync(entry, entry.Pak, entry.Url, pakTemporary, entry.Size, entry.Sha256, cancellation);
            if (entry.Sig is { } sig)
            {
                _samples.Clear();
                await DownloadAsync(entry, sig.File, sig.Url, sigTemporary, sig.Size, sig.Sha256, cancellation);
            }

            // Both verified. The old pak goes before its sig, and the new sig comes before its pak, so at no point is a
            // pak installed without the sig it needs; a sig left from an older entry goes when the new one has none.
            Publish(new ServerAssetsProgress(ServerAssetsPhase.Installing, entry.Version, entry.Pak, entry.Size, entry.Size), true);
            File.Delete(destination);
            File.Delete(sigDestination);
            if (entry.Sig is not null)
            {
                File.Move(sigTemporary, sigDestination);
            }

            try
            {
                File.Move(pakTemporary, destination);
            }
            catch
            {
                File.Delete(sigDestination);
                throw;
            }
        }
        finally
        {
            File.Delete(sigTemporary);
        }
    }

    private async Task DownloadAsync(ServerAssetsEntry entry, string file, string url, string temporary, long size, string sha256, CancellationToken cancellation)
    {
        try
        {
            await Downloads.VerifiedAsync(_http, url, temporary, file, size, sha256, null, cancellation, Options(entry, file, size));
        }
        catch (InvalidDataException)
        {
            // A finished download that isn't the manifest's is useless to resume.
            File.Delete(temporary);
            throw;
        }
    }

    private static async Task<InstalledPak?> InstalledAsync(string path, CancellationToken cancellation)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var sig = SigPath(path);
        return new InstalledPak(
            new FileInfo(path).Length,
            await Downloads.Sha256Async(path, cancellation),
            File.Exists(sig) ? new InstalledFile(new FileInfo(sig).Length, await Downloads.Sha256Async(sig, cancellation)) : null);
    }

    /// <summary>
    /// Deletes Core's pak and sig (any platform) and unfinished downloads of them from <paramref name="paksFolder"/>, and
    /// nothing else, except the file names in <paramref name="keep"/> (a partial download to resume); with
    /// <paramref name="partialsOnly"/>, only unfinished downloads. Paks go before sigs, so a failure part way never leaves a pak without its sig.
    /// </summary>
    public static int Remove(string paksFolder, IReadOnlyCollection<string>? keep = null, bool partialsOnly = false)
    {
        if (!Directory.Exists(paksFolder))
        {
            return 0;
        }

        string[] order = [".pak", ".sig", ".download"];
        var files = Directory.EnumerateFiles(paksFolder, PakPrefix + "*")
            .Select(file => (File: file, Rank: Array.FindIndex(order, ending => Path.GetFileName(file).EndsWith(ending, StringComparison.OrdinalIgnoreCase))))
            .Where(file => file.Rank >= 0 && (!partialsOnly || file.Rank == 2) && keep?.Contains(Path.GetFileName(file.File), StringComparer.OrdinalIgnoreCase) != true)
            .OrderBy(file => file.Rank)
            .ToList();
        foreach (var (file, _) in files)
        {
            File.Delete(file);
        }

        return files.Count;
    }

    /// <summary>Removes Core's pak from every build given, for when the feature is turned off; failures are logged.</summary>
    public void RemoveFrom(IEnumerable<string> buildRoots)
    {
        foreach (var root in buildRoots.Where(root => root.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Remove(PaksFolder(root)) > 0)
                {
                    _log($"Server assets: turned off, removed Core's pak from {PaksFolder(root)}");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _log($"Server assets: couldn't remove Core's pak from {PaksFolder(root)} ({exception.Message}); it goes when its servers next start");
            }
        }
    }

    /// <summary>The manifest from GitHub, cached when it parses; the cached copy when GitHub can't be reached; null without either.</summary>
    public async Task<ServerAssetsManifest?> ManifestAsync(CancellationToken cancellation)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(ManifestTimeout);
            var json = await _http.GetStringAsync(ManifestUrl, timeout.Token);
            var manifest = ServerAssetsManifest.Parse(json);
            try
            {
                Directory.CreateDirectory(_cacheFolder);
                await File.WriteAllTextAsync(CachedManifest + ".tmp", json, cancellation);
                File.Move(CachedManifest + ".tmp", CachedManifest, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _log($"Server assets: couldn't cache the manifest ({exception.Message})");
            }

            return manifest;
        }
        catch (Exception exception) when (!cancellation.IsCancellationRequested && exception is HttpRequestException or OperationCanceledException or InvalidDataException or IOException)
        {
            var why = exception is OperationCanceledException ? $"no answer in {ManifestTimeout.TotalSeconds:0} s" : exception.Message;
            if (File.Exists(CachedManifest))
            {
                try
                {
                    var cached = ServerAssetsManifest.Parse(await File.ReadAllTextAsync(CachedManifest, cancellation));
                    _log($"Server assets: couldn't read the manifest ({why}); using the copy from {File.GetLastWriteTime(CachedManifest):yyyy-MM-dd HH:mm}");
                    return cached;
                }
                catch (Exception cacheException) when (cacheException is IOException or InvalidDataException)
                {
                    _log($"Server assets: the cached manifest is unreadable too ({cacheException.Message})");
                }
            }

            _log($"Server assets: couldn't read the manifest ({why})");
            return null;
        }
    }
}
