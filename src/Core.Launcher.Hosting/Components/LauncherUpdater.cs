using System.Reflection;

namespace Core.Launcher.Hosting.Components;

/// <summary>
/// Updates CoreLauncher.exe from the launcher's own latest release: the new exe is downloaded beside the running one and
/// checked, the running one is renamed to .old (Windows allows renaming a running exe), the new one takes its name, and
/// the launcher restarts. The .old file is removed on the next start.
/// </summary>
public sealed class LauncherUpdater
{
    public const string Repository = "PongooDev/Core-Launcher";
    public const string AssetName = "CoreLauncher.exe";

    private readonly GitHubClient _github;
    private readonly Action<string> _log;

    public LauncherUpdater(GitHubClient github, Version current, string? executable, Action<string> log)
    {
        _github = github;
        Current = current;
        Executable = executable;
        _log = log;
    }

    public Version Current { get; }

    /// <summary>The running CoreLauncher.exe, or null when this isn't a published single-file build (then it never updates itself).</summary>
    public string? Executable { get; }

    public Release? Available { get; private set; }

    public static Version RunningVersion => typeof(LauncherUpdater).Assembly.GetName().Version is { } version ? new Version(version.Major, version.Minor, version.Build) : new Version(0, 0, 0);

    /// <summary>The exe to update: only a published single-file build, whose assemblies have no file location of their own.</summary>
    public static string? PublishedExecutable(Assembly app) => string.IsNullOrEmpty(app.Location) ? Environment.ProcessPath : null;

    public async Task<Release?> CheckAsync(CancellationToken cancellation)
    {
        var release = await _github.LatestReleaseAsync(Repository, cancellation);
        Available = release is not null && ParseVersion(release.Tag) is { } version && version > Current && release.Asset(AssetName) is not null ? release : null;
        if (Available is not null)
        {
            _log($"Core Launcher {Available.Tag} is available (running {Current.ToString(3)})");
        }

        return Available;
    }

    /// <summary>Puts the new exe in place of the running one. The caller restarts the launcher afterwards.</summary>
    public async Task InstallAsync(Release release, IProgress<string>? progress, CancellationToken cancellation)
    {
        var executable = Executable ?? throw new InvalidOperationException("Only a published CoreLauncher.exe can update itself");
        var asset = release.Asset(AssetName) ?? throw new InvalidOperationException($"Release {release.Tag} has no {AssetName}");
        var downloaded = executable + ".new";
        await _github.DownloadAssetAsync(asset, downloaded, progress, cancellation);
        Swap(executable, downloaded);
        _log($"Core Launcher {release.Tag} installed; restarting");
    }

    public static void Swap(string executable, string downloaded)
    {
        var old = executable + ".old";
        File.Move(executable, old, true);
        try
        {
            File.Move(downloaded, executable);
        }
        catch
        {
            File.Move(old, executable);
            throw;
        }
    }

    public static void CleanUp(string? executable)
    {
        if (executable is null)
        {
            return;
        }

        foreach (var leftover in new[] { executable + ".old", executable + ".new" })
        {
            try
            {
                File.Delete(leftover);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>"v1.2.3", "1.2.3" or "v1.2.3-beta" → 1.2.3; anything else → null.</summary>
    public static Version? ParseVersion(string tag)
    {
        var text = tag.TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(text, out var version) ? new Version(version.Major, version.Minor, Math.Max(version.Build, 0)) : null;
    }
}
