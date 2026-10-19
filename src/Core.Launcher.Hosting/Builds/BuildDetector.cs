namespace Core.Launcher.Hosting.Builds;

public sealed record DetectedBuild(string Folder, BuildVersion Version);

public static class BuildDetector
{
    /// <summary>Finds the build root (the folder with Engine\Build\Build.version) from the folder the user picked, its Fortnite subfolder, or a folder inside the build.</summary>
    public static DetectedBuild? Detect(string picked)
    {
        foreach (var candidate in Candidates(picked))
        {
            if (BuildVersion.Read(candidate) is { } version)
            {
                return new DetectedBuild(candidate, version);
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates(string picked)
    {
        var folder = Path.GetFullPath(picked);
        yield return folder;
        yield return Path.Combine(folder, "Fortnite");

        var parent = Directory.GetParent(folder);
        for (var depth = 0; parent is not null && depth < 5; ++depth, parent = parent.Parent)
        {
            yield return parent.FullName;
        }
    }
}
