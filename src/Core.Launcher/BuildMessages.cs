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
                ? $"Found a Fortnite build, but couldn't tell which version it is. It may not work: Core is only tested on {KnownBuilds.SupportSummary}."
                : $"Found a Fortnite build, but couldn't tell which version it is, so Core can't use it. Turn on {KnownBuilds.AllowUnsupportedSetting} in Settings → Builds to try it.", "BusyBrush");
        }

        var name = KnownBuilds.Create(build.Folder, version, allowUnsupported).Name;
        switch (KnownBuilds.SupportOf(version.Changelist))
        {
            case BuildSupport.Full:
                return ($"{name} is supported", "GoodBrush");
            case BuildSupport.InProgress:
                return ($"Found {name}. You can play and host on it, but Core's support for it is in progress: some things may not work yet.", "BusyBrush");
        }

        return (allowUnsupported
            ? $"Found {name}. It isn't supported and may not work: Core is only tested on {KnownBuilds.SupportSummary}."
            : $"Found {name}, but Core doesn't support it yet. It supports {KnownBuilds.SupportSummary}. Turn on {KnownBuilds.AllowUnsupportedSetting} in Settings → Builds to try it.", "BusyBrush");
    }
}
