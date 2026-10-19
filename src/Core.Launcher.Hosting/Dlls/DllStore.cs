using System.Security.Cryptography;
using System.Text.Json;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Hosting.Dlls;

public sealed class DllException(string message) : Exception(message);

/// <summary>Turns a DLL role into the file to load: from the CoreV2 release (downloaded once per tag) or a copy of a local build.</summary>
public sealed class DllStore
{
    private const string MarkerName = "release.json";

    private readonly LauncherPaths _paths;
    private readonly ReleaseClient _releases;
    private readonly Action<string> _log;
    private readonly Lock _gate = new();
    private DllSettings _settings = new();
    private string? _releaseFolder;

    public DllStore(LauncherPaths paths, ReleaseClient releases, Action<string> log)
    {
        _paths = paths;
        _releases = releases;
        _log = log;
    }

    public string? ReleaseTag { get; private set; }

    public void Configure(DllSettings settings)
    {
        lock (_gate)
        {
            _settings = settings;
        }
    }

    public string Describe()
    {
        lock (_gate)
        {
            return _settings.Source == DllSource.Local
                ? $"Local: {(_settings.LocalFolder.Length > 0 ? _settings.LocalFolder : "no folder set")}"
                : $"Release: {ReleaseTag ?? "none downloaded"} ({_settings.Repository})";
        }
    }

    /// <summary>Downloads the latest release's DLLs if they aren't here yet. Offline or without a release, the newest downloaded one stays in use.</summary>
    public async Task RefreshAsync(CancellationToken cancellation)
    {
        DllSettings settings;
        lock (_gate)
        {
            settings = _settings;
        }

        if (settings.Source != DllSource.Release)
        {
            return;
        }

        try
        {
            var release = await _releases.LatestAsync(settings.Repository, cancellation);
            if (release is null)
            {
                _log($"{settings.Repository} has no release yet");
            }
            else
            {
                await DownloadAsync(release, cancellation);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or IOException or TaskCanceledException or JsonException)
        {
            _log($"Couldn't check {settings.Repository} for a release: {exception.Message}");
        }

        if (ReleaseTag is null && NewestDownloaded() is { } cached)
        {
            UseRelease(cached.Folder, cached.Tag);
            _log($"Using the downloaded release {cached.Tag}");
        }
    }

    private async Task DownloadAsync(Release release, CancellationToken cancellation)
    {
        var folder = Path.Combine(_paths.ReleaseDlls, SafeName(release.Tag));
        if (File.Exists(Path.Combine(folder, MarkerName)))
        {
            UseRelease(folder, release.Tag);
            return;
        }

        var assets = release.Assets.Where(asset => CoreDlls.All.Any(role => CoreDlls.FileName(role).Equals(asset.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        if (assets.Count == 0)
        {
            _log($"Release {release.Tag} has none of {string.Join(", ", CoreDlls.All.Select(CoreDlls.FileName))}");
            return;
        }

        Directory.CreateDirectory(folder);
        foreach (var asset in assets)
        {
            _log($"Downloading {asset.Name} ({asset.Size:N0} bytes) from release {release.Tag}");
            await _releases.DownloadAsync(asset, Path.Combine(folder, asset.Name), cancellation);
        }

        await File.WriteAllTextAsync(Path.Combine(folder, MarkerName),
            JsonSerializer.Serialize(new ReleaseMarker(release.Tag, release.PublishedAt, assets.Select(asset => asset.Name).ToList())), cancellation);
        UseRelease(folder, release.Tag);
        _log($"Release {release.Tag} ready");
    }

    private void UseRelease(string folder, string tag)
    {
        lock (_gate)
        {
            _releaseFolder = folder;
            ReleaseTag = tag;
        }
    }

    private (string Folder, string Tag)? NewestDownloaded()
    {
        if (!Directory.Exists(_paths.ReleaseDlls))
        {
            return null;
        }

        return Directory.EnumerateDirectories(_paths.ReleaseDlls)
            .Select(folder => (Folder: folder, Marker: ReadMarker(folder)))
            .Where(entry => entry.Marker is not null)
            .OrderByDescending(entry => entry.Marker!.PublishedAt)
            .Select(entry => ((string Folder, string Tag)?)(entry.Folder, entry.Marker!.Tag))
            .FirstOrDefault();
    }

    private static ReleaseMarker? ReadMarker(string folder)
    {
        try
        {
            return JsonSerializer.Deserialize<ReleaseMarker>(File.ReadAllText(Path.Combine(folder, MarkerName)));
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The path to load for <paramref name="role"/>, checked to be an x64 DLL.</summary>
    public string Resolve(DllRole role)
    {
        lock (_gate)
        {
            var name = CoreDlls.FileName(role);
            var path = _settings.Source == DllSource.Local ? CopyLocal(name) : FromRelease(name);
            if (PeImage.CheckX64(path, dll: true) is { } problem)
            {
                throw new DllException(problem);
            }

            return path;
        }
    }

    private string FromRelease(string name)
    {
        if (_releaseFolder is null)
        {
            throw new DllException($"No {_settings.Repository} release is downloaded, so there's no {name}. Check for updates, or use a local DLL folder in Settings.");
        }

        var path = Path.Combine(_releaseFolder, name);
        return File.Exists(path) ? path : throw new DllException($"Release {ReleaseTag} has no {name}");
    }

    // The game loads a copy, so the build output isn't locked while servers run and every restart picks up a new build.
    private string CopyLocal(string name)
    {
        if (_settings.LocalFolder.Length == 0)
        {
            throw new DllException("No local DLL folder is set (Settings → DLLs)");
        }

        var source = Path.Combine(_settings.LocalFolder, name);
        if (!File.Exists(source))
        {
            throw new DllException($"{source} doesn't exist");
        }

        string hash;
        using (var file = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            hash = Convert.ToHexStringLower(SHA256.HashData(file));
        }

        var folder = Path.Combine(_paths.LocalDlls, hash[..16]);
        var target = Path.Combine(folder, name);
        if (File.Exists(target))
        {
            return target;
        }

        Directory.CreateDirectory(folder);
        File.Copy(source, target + ".tmp", true);
        var pdb = Path.ChangeExtension(source, ".pdb");
        if (File.Exists(pdb))
        {
            File.Copy(pdb, Path.Combine(folder, Path.GetFileName(pdb)), true);
        }

        File.Move(target + ".tmp", target, true);
        _log($"Copied {source} ({hash[..16]})");
        return target;
    }

    /// <summary>Removes local copies no process has loaded and all but the two newest releases. Files in use are skipped.</summary>
    public void CleanUp()
    {
        if (Directory.Exists(_paths.LocalDlls))
        {
            foreach (var folder in Directory.EnumerateDirectories(_paths.LocalDlls))
            {
                TryDelete(folder);
            }
        }

        if (Directory.Exists(_paths.ReleaseDlls))
        {
            var old = Directory.EnumerateDirectories(_paths.ReleaseDlls)
                .OrderByDescending(folder => ReadMarker(folder)?.PublishedAt ?? DateTimeOffset.MinValue)
                .Skip(2);
            foreach (var folder in old)
            {
                TryDelete(folder);
            }
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string SafeName(string tag) => string.Concat(tag.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private sealed record ReleaseMarker(string Tag, DateTimeOffset PublishedAt, List<string> Assets);
}
