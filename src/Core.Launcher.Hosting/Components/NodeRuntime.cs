using System.Text.Json;

namespace Core.Launcher.Hosting.Components;

public sealed record NodeRelease(string Version, string ArchiveName, string ArchiveUrl, string ChecksumsUrl);

/// <summary>A private Node.js for the managed Core-Backend: the newest LTS from nodejs.org, checked against its SHASUMS256.txt.</summary>
public sealed class NodeRuntime
{
    public const int MinimumMajor = 22;

    private const string Dist = "https://nodejs.org/dist/";

    private readonly LauncherPaths _paths;
    private readonly HttpClient _http;
    private readonly Action<string> _log;

    public NodeRuntime(LauncherPaths paths, HttpClient http, Action<string> log)
    {
        _paths = paths;
        _http = http;
        _log = log;
    }

    private string MarkerPath => Path.Combine(_paths.Runtime, "node.json");

    /// <summary>The installed node.exe, or null.</summary>
    public string? Executable => Installed is { Folder.Length: > 0 } installed && Path.Combine(_paths.Runtime, installed.Folder, "node.exe") is var executable && File.Exists(executable) ? executable : null;

    public string? Version => Executable is null ? null : Installed?.Version;

    private InstalledNode? Installed
    {
        get
        {
            try
            {
                return JsonSerializer.Deserialize<InstalledNode>(File.ReadAllText(MarkerPath));
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>The installed node.exe, installing the newest LTS first when there is none.</summary>
    public async Task<string> EnsureAsync(IProgress<string>? progress, CancellationToken cancellation)
    {
        if (Executable is { } existing)
        {
            return existing;
        }

        progress?.Report("Finding the newest Node.js LTS");
        var release = PickRelease(await _http.GetStringAsync(Dist + "index.json", cancellation), MinimumMajor)
            ?? throw new InvalidDataException($"nodejs.org lists no LTS release {MinimumMajor} or newer for Windows x64");
        var checksum = ChecksumFor(await _http.GetStringAsync(release.ChecksumsUrl, cancellation), release.ArchiveName)
            ?? throw new InvalidDataException($"{release.ArchiveName} isn't in its SHASUMS256.txt");

        var archive = Path.Combine(_paths.Runtime, release.ArchiveName);
        _log($"Downloading Node.js {release.Version} from {release.ArchiveUrl}");
        await Downloads.FileAsync(_http, release.ArchiveUrl, archive, null, checksum,
            progress is null ? null : new Progress<string>(text => progress.Report($"Downloading Node.js {release.Version}: {text}")), cancellation);

        progress?.Report($"Unpacking Node.js {release.Version}");
        var folder = Path.Combine(_paths.Runtime, Path.GetFileNameWithoutExtension(release.ArchiveName));
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, true);
        }

        await Task.Run(() => Downloads.ExtractWithoutTopFolder(archive, folder), cancellation);
        File.Delete(archive);

        var executable = Path.Combine(folder, "node.exe");
        if (!File.Exists(executable))
        {
            throw new InvalidDataException($"{release.ArchiveName} has no node.exe");
        }

        var marker = MarkerPath + ".tmp";
        await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new InstalledNode(release.Version, Path.GetFileName(folder))), cancellation);
        File.Move(marker, MarkerPath, true);
        _log($"Node.js {release.Version} installed");
        return executable;
    }

    /// <summary>npm's entry script inside the runtime (Node's Windows zip ships npm).</summary>
    public static string NpmScript(string nodeExecutable) => Path.Combine(Path.GetDirectoryName(nodeExecutable)!, "node_modules", "npm", "bin", "npm-cli.js");

    /// <summary>The newest LTS release at least <paramref name="minimumMajor"/> with a Windows x64 zip. index.json lists the newest first.</summary>
    public static NodeRelease? PickRelease(string indexJson, int minimumMajor)
    {
        using var document = JsonDocument.Parse(indexJson);
        foreach (var release in document.RootElement.EnumerateArray())
        {
            var version = release.GetProperty("version").GetString()!;
            var lts = release.TryGetProperty("lts", out var value) && value.ValueKind == JsonValueKind.String;
            var windows = release.TryGetProperty("files", out var files) && files.EnumerateArray().Any(file => file.GetString() == "win-x64-zip");
            if (lts && windows && int.TryParse(version.TrimStart('v').Split('.')[0], out var major) && major >= minimumMajor)
            {
                var archive = $"node-{version}-win-x64.zip";
                return new NodeRelease(version, archive, $"{Dist}{version}/{archive}", $"{Dist}{version}/SHASUMS256.txt");
            }
        }

        return null;
    }

    /// <summary>The SHA-256 SHASUMS256.txt gives for <paramref name="fileName"/> ("&lt;hash&gt;  &lt;file&gt;" lines).</summary>
    public static string? ChecksumFor(string checksums, string fileName)
    {
        foreach (var line in checksums.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Trim() == fileName)
            {
                return parts[0].ToLowerInvariant();
            }
        }

        return null;
    }

    // Folder is relative to Runtime, so the data folder can move.
    private sealed record InstalledNode(string Version, string Folder);
}
