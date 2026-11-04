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
    public const int SupportedSchema = 1;

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

            if (schema != SupportedSchema)
            {
                throw new InvalidDataException($"manifest.json is schema {schema}; this launcher reads schema {SupportedSchema} (update the launcher)");
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
                    builds[changelist] = ParseEntry(changelist, build.Value);
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

    private static ServerAssetsEntry ParseEntry(int changelist, JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"manifest.json: CL {changelist} isn't an object");
        }

        var pak = ParseFile(changelist, "pak", entry, "pak", ".pak");
        ServerAssetsFile? sig = null;
        if (entry.TryGetProperty("sig", out var sigValue))
        {
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

    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly string _cacheFolder;
    private readonly Action<string> _log;
    private readonly Lock _gate = new();
    private string? _key;
    private Task<string>? _check;

    public ServerAssets(HttpClient http, string cacheFolder, Action<string> log)
    {
        _http = http;
        _cacheFolder = cacheFolder;
        _log = log;
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
                _key = key;
                var check = Task.Run(() => EnsureAsync(buildRoot, changelist, enabled, cancellation));
                _check = check;
                check.ContinueWith(_ => Checked?.Invoke(), TaskScheduler.Default);
            }

            return _check.IsCompleted ? null : "checking server assets";
        }
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
            result = await EnsureCoreAsync(buildRoot, changelist, enabled, cancellation);
        }
        catch (Exception exception)
        {
            result = $"skipped ({exception.Message}); the server starts without them";
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
        if (installed is null)
        {
            // No pak: a sig or an unfinished download left behind on its own goes.
            Remove(paks);
        }

        var manifest = enabled && changelist > 0 ? await ManifestAsync(cancellation) : null;
        var decision = Decide(enabled, changelist, manifest, installed);
        switch (decision.Action)
        {
            case ServerAssetsAction.Remove:
                Remove(paks);
                return $"removed {Path.GetFileName(destination)}{(installed?.Sig is null ? "" : $" and {Path.GetFileName(SigPath(destination))}")}: {decision.Reason}";
            case ServerAssetsAction.Install:
                var entry = manifest!.For(changelist)!;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    timeout.CancelAfter(DownloadTimeout);
                    await InstallAsync(entry, destination, timeout.Token);
                }

                var sig = entry.Sig is null ? "" : $", with {Path.GetFileName(SigPath(destination))} ({entry.Sig.Size} bytes, SHA-256 {entry.Sig.Sha256})";
                return $"{decision.Reason}: installed {Path.GetFileName(destination)} ({entry.Size} bytes, SHA-256 {entry.Sha256}{sig}; {string.Join(", ", entry.Contents)})";
            default:
                return decision.Action == ServerAssetsAction.UpToDate ? $"{decision.Reason} as {Path.GetFileName(destination)}" : $"none: {decision.Reason}";
        }
    }

    /// <summary>
    /// Installs the entry's pak, and its sig when it has one, as one: both are downloaded to temporary files and checked
    /// against the manifest before either is moved into place, so a pak never sits in the Paks folder without its sig
    /// (on a build that signs its paks, the engine refuses to start with an unsigned pak).
    /// </summary>
    private async Task InstallAsync(ServerAssetsEntry entry, string destination, CancellationToken cancellation)
    {
        var sigDestination = SigPath(destination);
        var pakTemporary = destination + ".download";
        var sigTemporary = sigDestination + ".download";
        try
        {
            await Downloads.VerifiedAsync(_http, entry.Url, pakTemporary, entry.Pak, entry.Size, entry.Sha256, null, cancellation);
            if (entry.Sig is { } sig)
            {
                await Downloads.VerifiedAsync(_http, sig.Url, sigTemporary, sig.File, sig.Size, sig.Sha256, null, cancellation);
            }

            // Both verified. The old pak goes before its sig, and the new sig comes before its pak, so at no point is a
            // pak installed without the sig it needs; a sig left from an older entry goes when the new one has none.
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
            File.Delete(pakTemporary);
            File.Delete(sigTemporary);
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
    /// nothing else. Paks go before sigs, so a failure part way never leaves a pak without its sig.
    /// </summary>
    public static int Remove(string paksFolder)
    {
        if (!Directory.Exists(paksFolder))
        {
            return 0;
        }

        string[] order = [".pak", ".sig", ".pak.download", ".sig.download"];
        var files = Directory.EnumerateFiles(paksFolder, PakPrefix + "*")
            .Select(file => (File: file, Rank: Array.FindIndex(order, ending => Path.GetFileName(file).EndsWith(ending, StringComparison.OrdinalIgnoreCase))))
            .Where(file => file.Rank >= 0)
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
