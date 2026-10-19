using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Core.Launcher.Hosting.Components;

/// <summary>A Core-Backend version to install: a release tag, or the default branch's head while the repository has no release.</summary>
public sealed record BackendVersion(string Id, string ArchiveUrl, string Description);

public sealed record InstalledBackend(string Id, string Folder);

/// <summary>
/// The Core-Backend the launcher installs and updates for players: the source of its latest release (or its default
/// branch while it has none), with its production dependencies installed by npm ci from package-lock.json.
/// </summary>
public sealed class BackendPackage
{
    private const int KeptVersions = 2;

    private readonly LauncherPaths _paths;
    private readonly GitHubClient _github;
    private readonly NodeRuntime _node;
    private readonly Action<string> _log;

    public BackendPackage(LauncherPaths paths, GitHubClient github, NodeRuntime node, Action<string> log)
    {
        _paths = paths;
        _github = github;
        _node = node;
        _log = log;
    }

    private string MarkerPath => Path.Combine(_paths.Backend, "installed.json");

    /// <summary>The installed version, or null when there is none (or its folder is incomplete).</summary>
    public InstalledBackend? Installed
    {
        get
        {
            try
            {
                var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(MarkerPath));
                if (marker is not { Id.Length: > 0, Folder.Length: > 0 })
                {
                    return null;
                }

                var folder = Path.Combine(_paths.BackendVersions, marker.Folder);
                return IsComplete(folder) ? new InstalledBackend(marker.Id, folder) : null;
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                return null;
            }
        }
    }

    public async Task<BackendVersion> LatestAsync(string repository, CancellationToken cancellation)
    {
        if (await _github.LatestReleaseAsync(repository, cancellation) is { } release)
        {
            return new BackendVersion(release.Tag, GitHubClient.SourceArchiveUrl(repository, release.Tag), $"release {release.Tag}");
        }

        var (branch, commit) = await _github.DefaultBranchHeadAsync(repository, cancellation);
        var id = $"{branch}-{commit[..7]}";
        return new BackendVersion(id, GitHubClient.SourceArchiveUrl(repository, commit), $"{branch} at {commit[..7]} (no release yet)");
    }

    /// <summary>Installs the latest version unless it is the installed one. Returns the newly installed version, or null.</summary>
    public async Task<InstalledBackend?> UpdateAsync(string repository, IProgress<string>? progress, CancellationToken cancellation)
    {
        progress?.Report($"Checking {repository}");
        var latest = await LatestAsync(repository, cancellation);
        if (Installed is { } installed && installed.Id == latest.Id)
        {
            progress?.Report($"Core-Backend {installed.Id} is up to date");
            return null;
        }

        return await InstallAsync(latest, progress, cancellation);
    }

    public async Task<InstalledBackend> InstallAsync(BackendVersion version, IProgress<string>? progress, CancellationToken cancellation)
    {
        var node = await _node.EnsureAsync(progress, cancellation);
        var folder = Path.Combine(_paths.BackendVersions, SafeName(version.Id));
        var staging = folder + ".staging";
        var archive = folder + ".zip";
        DeleteFolder(staging);

        _log($"Installing Core-Backend {version.Description} from {version.ArchiveUrl}");
        await Downloads.FileAsync(_github.Http, version.ArchiveUrl, archive, null, null,
            progress is null ? null : new Progress<string>(text => progress.Report($"Downloading Core-Backend {version.Id}: {text}")), cancellation);
        progress?.Report($"Unpacking Core-Backend {version.Id}");
        await Task.Run(() => Downloads.ExtractWithoutTopFolder(archive, staging), cancellation);
        File.Delete(archive);

        if (!File.Exists(Path.Combine(staging, "src", "main.js")) || !File.Exists(Path.Combine(staging, "package.json")))
        {
            throw new InvalidDataException($"Core-Backend {version.Id} has no src\\main.js or package.json");
        }

        if (!Directory.Exists(Path.Combine(staging, "node_modules")))
        {
            progress?.Report("Installing Core-Backend's dependencies (npm ci)");
            await InstallDependenciesAsync(node, staging, cancellation);
        }

        DeleteFolder(folder);
        Directory.Move(staging, folder);

        var installed = new InstalledBackend(version.Id, folder);
        var marker = MarkerPath + ".tmp";
        await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new Marker(version.Id, Path.GetFileName(folder))), cancellation);
        File.Move(marker, MarkerPath, true);
        _log($"Core-Backend {version.Id} installed");
        RemoveOldVersions(folder);
        return installed;
    }

    private async Task InstallDependenciesAsync(string node, string folder, CancellationToken cancellation)
    {
        var info = new ProcessStartInfo(node)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(NodeRuntime.NpmScript(node));
        info.ArgumentList.Add(File.Exists(Path.Combine(folder, "package-lock.json")) ? "ci" : "install");
        info.ArgumentList.Add("--omit=dev");
        info.ArgumentList.Add("--no-audit");
        info.ArgumentList.Add("--no-fund");
        info.Environment["npm_config_update_notifier"] = "false";

        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, line) => Append(output, line.Data);
        process.ErrorDataReceived += (_, line) => Append(output, line.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellation);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw;
        }

        process.WaitForExit();
        foreach (var line in output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            _log($"npm: {line}");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"npm {info.ArgumentList[1]} failed with exit code {process.ExitCode} (see Launcher.log)");
        }
    }

    private static void Append(StringBuilder output, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (output)
        {
            output.AppendLine(line);
        }
    }

    private static bool IsComplete(string folder) =>
        File.Exists(Path.Combine(folder, "src", "main.js")) && Directory.Exists(Path.Combine(folder, "node_modules"));

    // The previous version stays too, in case the running backend still uses it.
    private void RemoveOldVersions(string current)
    {
        var old = new DirectoryInfo(_paths.BackendVersions).GetDirectories()
            .Where(folder => !folder.FullName.Equals(current, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(folder => folder.CreationTimeUtc)
            .Skip(KeptVersions - 1);
        foreach (var folder in old)
        {
            DeleteFolder(folder.FullName);
        }
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Folder is relative to Backend\versions, so the data folder can move.
    private sealed record Marker(string Id, string Folder);

    private static string SafeName(string id) => string.Concat(id.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
}
