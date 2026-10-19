using Core.Launcher.Hosting.Builds;

namespace Core.Launcher;

/// <summary>What a picked build folder turned out to be, in words for the player, with the brush to show it in.</summary>
public static class BuildMessages
{
    public static (string Text, string Brush) Describe(DetectedBuild? build)
    {
        if (build is null)
        {
            return ($"No Fortnite build here. Pick the folder that has Engine and FortniteGame in it ({BuildDetector.ClientExecutable}).", "BadBrush");
        }

        var version = build.Version;
        if (version.Changelist == 0)
        {
            return ("Found a Fortnite build, but couldn't tell which version it is, so Core can't use it.", "BusyBrush");
        }

        var name = KnownBuilds.Create(build.Folder, version).Name;
        if (KnownBuilds.IsKnown(version.Changelist))
        {
            return ($"{name}{(version.Engine is { } engine ? $" ({engine})" : "")} is supported", "GoodBrush");
        }

        return ($"Found {name}, but Core doesn't support it yet. It supports {KnownBuilds.SupportedNames}.", "BusyBrush");
    }
}
