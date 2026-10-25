using Core.Launcher.Hosting.Builds;

namespace Core.Launcher;

/// <summary>What a picked build folder turned out to be, in words for the player, with the brush to show it in.</summary>
public static class BuildMessages
{
    /// <param name="allowUnsupported">Settings → Builds → Allow unsupported builds: an unsupported build is a warning, not a blocker.</param>
    public static (string Text, string Brush) Describe(DetectedBuild? build, bool allowUnsupported)
    {
        if (build is null)
        {
            return ($"No Fortnite build here. Pick the folder that has Engine and FortniteGame in it ({BuildDetector.ClientExecutable}).", "BadBrush");
        }

        var version = build.Version;
        if (version.Changelist == 0)
        {
            return (allowUnsupported
                ? $"Found a Fortnite build, but couldn't tell which version it is. It may not work: Core is only tested on {KnownBuilds.SupportedNames}."
                : $"Found a Fortnite build, but couldn't tell which version it is, so Core can't use it. Turn on {KnownBuilds.AllowUnsupportedSetting} in Settings → Builds to try it.", "BusyBrush");
        }

        var name = KnownBuilds.Create(build.Folder, version, allowUnsupported).Name;
        if (KnownBuilds.IsKnown(version.Changelist))
        {
            return ($"{name}{(version.Engine is { } engine ? $" ({engine})" : "")} is supported", "GoodBrush");
        }

        return (allowUnsupported
            ? $"Found {name}. It isn't supported and may not work: Core is only tested on {KnownBuilds.SupportedNames}."
            : $"Found {name}, but Core doesn't support it yet. It supports {KnownBuilds.SupportedNames}; turn on {KnownBuilds.AllowUnsupportedSetting} in Settings → Builds to try it.", "BusyBrush");
    }
}
