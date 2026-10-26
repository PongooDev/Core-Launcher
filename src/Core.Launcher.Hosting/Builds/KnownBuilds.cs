using System.Text.RegularExpressions;
using Core.Launcher.Hosting.Dlls;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Hosting.Builds;

/// <summary>How far Core supports a build (<see cref="KnownBuilds.SupportOf"/>).</summary>
public enum BuildSupport
{
    /// <summary>Not a build Core knows: turned away unless <see cref="LauncherSettings.AllowUnsupportedBuilds"/> is on.</summary>
    Unsupported,

    /// <summary>Core knows the build and is working on it: it plays and hosts like a supported build, marked as in progress.</summary>
    InProgress,

    /// <summary>Supported: Core is tested on it.</summary>
    Full,
}

/// <summary>
/// Per-build launch settings, keyed by changelist. A build missing here gets the same shape with no DLLs, or Core's
/// defaults when <see cref="LauncherSettings.AllowUnsupportedBuilds"/> is on.
/// </summary>
public static partial class KnownBuilds
{
    /// <summary>Fortnite 1.1, ++Fortnite+Release-Cert (its files carry no version number; players call it 1.1).</summary>
    public const int Cert = 3532353;

    /// <summary>
    /// Fortnite 1.2, also ++Fortnite+Release-Cert on UE 4.16.0. Same layout and launch line as 1.1: its exe takes the same
    /// switches (-httpproxy, -McpRegion, -SkipPatchCheck, -nullrhi, -nosound, -AUTH_*).
    /// </summary>
    public const int Fortnite12 = 3541083;

    /// <summary>Fortnite 1.7.2, ++Fortnite+Release-Cert CL-3700114 on UE 4.16.0: the first build with Battle Royale.</summary>
    public const int Fortnite172 = 3700114;

    /// <summary>
    /// What a Battle Royale server gets: CoreV2's -CoreMap starts it on Athena_Terrain with the solo playlist (the match's
    /// own playlist comes with its assignment), and -EnableMMS registers it with the matchmaking service that Battle
    /// Royale's players queue on. Core-Docs/Athena-Boot-1.7.2.md.
    /// </summary>
    public const string BattleRoyaleServerArguments = "-CoreMap=/Game/Athena/Maps/Athena_Terrain?playlistId=2 -EnableMMS";

    /// <summary>The Settings → Builds switch that lets unsupported builds use Core, by its label.</summary>
    public const string AllowUnsupportedSetting = "Allow unsupported builds";

    /// <summary>What Home shows next to an in-progress build.</summary>
    public const string InProgressNote = "support in progress, some things may not work yet";

    // No -FORCELOGFLUSH: it can deadlock the game at start-up (see SettingsStore's version 1 migration).
    private const string ClientArguments = "-epicapp=Fortnite -epicenv=Prod -epicportal -SkipPatchCheck";

    private const string ServerArguments = ClientArguments + " -nullrhi -nosplash -nosound";

    /// <summary>
    /// The region a Cert server advertises. Its UQosRegionManager reads -McpRegion= and otherwise answers "NONE", which
    /// never matches the Cert client's matchMakingRequest (REGION_s EQUAL NA). Core-Docs/Session-Plan.md §6 L1.
    /// </summary>
    public const string CertServerRegion = "-McpRegion=NA";

    /// <param name="Version">The Fortnite version players call it, for <see cref="SupportSummary"/>.</param>
    /// <param name="FormerNames">Names earlier launchers gave this build, which <see cref="CurrentName"/> renames.</param>
    /// <param name="BattleRoyale">Whether the build has Battle Royale, which some of its servers host.</param>
    private sealed record KnownBuild(int Changelist, string Version, string Name, BuildSupport Support, string[] FormerNames, bool BattleRoyale = false);

    // Core-Docs/Tools/run_server.py and Backend-Evidence-3532353.md (launch line); DLL order from CoreV2 (redirect first).
    // Every known build uses Core's launch settings (WithCore). Oldest first, as Core supports them.
    private static readonly KnownBuild[] Builds =
    [
        new(Cert, "1.1", "Fortnite 1.1 (Cert CL-3532353)", BuildSupport.Full, ["Cert CL-3532353"]),
        new(Fortnite12, "1.2", "Fortnite 1.2 (CL-3541083)", BuildSupport.Full, []),
        new(Fortnite172, "1.7.2", "Fortnite 1.7.2 (CL-3700114)", BuildSupport.InProgress, [], BattleRoyale: true),
    ];

    private static KnownBuild? Find(int changelist) => Array.Find(Builds, build => build.Changelist == changelist);

    /// <summary>
    /// Core's launch settings: what every known build gets, and what <see cref="AllowUnsupportedSetting"/> gives the
    /// others.
    /// </summary>
    private static BuildSettings WithCore(string name) => new()
    {
        Name = name,
        ClientArguments = ClientArguments,
        ServerArguments = ServerArguments + " " + CertServerRegion,
        UseHttpProxy = true,
        ClientDlls = [DllRole.Redirect, DllRole.Client],
        ServerDlls = [DllRole.Redirect, DllRole.Server],
    };

    /// <summary>An unsupported build as added while <see cref="AllowUnsupportedSetting"/> is off: every Core option off.</summary>
    private static BuildSettings WithoutCore(string name) => new()
    {
        Name = name,
        ClientArguments = ClientArguments,
        ServerArguments = ServerArguments,
    };

    /// <summary>A build Core supports fully or is working on: it plays and hosts without <see cref="AllowUnsupportedSetting"/>.</summary>
    public static bool IsKnown(int changelist) => Find(changelist) is not null;

    public static BuildSupport SupportOf(int changelist) => Find(changelist)?.Support ?? BuildSupport.Unsupported;

    /// <summary>Whether Core hosts Battle Royale on this build (<see cref="ServerSettings.BattleRoyaleInstances"/>).</summary>
    public static bool HasBattleRoyale(int changelist) => Find(changelist)?.BattleRoyale ?? false;

    /// <summary>The known build's name ("Fortnite 1.2 (CL-3541083)"), or null for an unsupported changelist.</summary>
    public static string? NameOf(int changelist) => Find(changelist)?.Name;

    /// <summary>"Fortnite 1.1 to 1.2" (the fully supported builds, for messages).</summary>
    public static string SupportedNames => Versions(BuildSupport.Full);

    /// <summary>"Fortnite 1.7.2" (the builds whose support is in progress, for messages), or "" when none is.</summary>
    public static string InProgressNames => Versions(BuildSupport.InProgress);

    /// <summary>"Fortnite 1.1 to 1.2, and support for Fortnite 1.7.2 is in progress".</summary>
    public static string SupportSummary =>
        InProgressNames.Length == 0 ? SupportedNames : $"{SupportedNames}, and support for {InProgressNames} is in progress";

    /// <summary>
    /// The builds with this support as one range of versions, so the messages stay short however many there are. Core
    /// moves forward one build at a time and skips builds nobody has a copy of, so they are every build in it.
    /// </summary>
    private static string Versions(BuildSupport support)
    {
        var versions = Builds.Where(build => build.Support == support).Select(build => build.Version).ToArray();
        return versions.Length switch
        {
            0 => "",
            1 => $"Fortnite {versions[0]}",
            _ => $"Fortnite {versions[0]} to {versions[^1]}",
        };
    }

    /// <summary>
    /// Whether Core can run this build as set up: it is a known build, or it has the Core DLLs (by hand, or from
    /// <see cref="AllowUnsupportedSetting"/>). An unsupported build left as added would start the client against Epic's
    /// own services and "servers" without Core.dll.
    /// </summary>
    public static bool CanPlay(BuildSettings build) => build.UseHttpProxy || build.ClientDlls.Contains(DllRole.Redirect);

    public static bool CanHost(BuildSettings build) => build.ServerDlls.Contains(DllRole.Server);

    /// <summary>Why the build can't play or host (<see cref="CanPlay"/>, <see cref="CanHost"/>).</summary>
    public static string NotSupported(BuildSettings build, bool allowUnsupported) =>
        IsKnown(build.Changelist) || allowUnsupported
            ? $"{build.Name} has the Core DLLs turned off (Settings → Builds)"
            : NotSupportedYet(build.Name);

    /// <summary>The message for an unsupported build while <see cref="AllowUnsupportedSetting"/> is off.</summary>
    private static string NotSupportedYet(string name) =>
        $"Core doesn't support {name} yet. It supports {SupportSummary}. Turn on {AllowUnsupportedSetting} in Settings → Builds to try it.";

    /// <summary>The warning for an unsupported build while <see cref="AllowUnsupportedSetting"/> is on.</summary>
    public static string UnsupportedWarning(string name) =>
        $"{name} isn't supported and may not work: Core is only tested on {SupportSummary}.";

    /// <summary>The note for a build whose support is in progress (<see cref="BuildSupport.InProgress"/>).</summary>
    public static string InProgressWarning(int changelist) =>
        $"Core's support for {NameOf(changelist) ?? $"CL-{changelist}"} is in progress: some things may not work yet.";

    /// <summary>What a game's log starts with about its build: null for a fully supported one.</summary>
    public static string? SupportWarning(BuildSettings build) => SupportOf(build.Changelist) switch
    {
        BuildSupport.Full => null,
        BuildSupport.InProgress => InProgressWarning(build.Changelist),
        _ => UnsupportedWarning(build.Name),
    };

    public static BuildSettings Create(string folder, BuildVersion version, bool allowUnsupported)
    {
        BuildSettings build;
        if (Find(version.Changelist) is { } known)
        {
            build = WithCore(known.Name);
        }
        else
        {
            var name = version.Changelist == 0 ? "Fortnite build"
                : version.ShortName.Length > 0 ? $"Fortnite {version.ShortName} CL-{version.Changelist}"
                : $"Fortnite CL-{version.Changelist}";
            build = allowUnsupported ? WithCore(name) : WithoutCore(name);
            build.CoreDefaultsAdded = allowUnsupported;
        }

        build.Path = folder;
        build.Changelist = version.Changelist;
        return build;
    }

    /// <summary>
    /// Makes a saved build of a known changelist that was added while Core didn't know it the known build. It keeps its
    /// name, path and options, but is no longer marked as given Core's defaults by <see cref="AllowUnsupportedSetting"/>
    /// (so turning that off leaves it alone); one that still has every Core option off, as it was added with the switch
    /// off, gets Core's; and its servers get <see cref="CertServerRegion"/> unless they set a region.
    /// </summary>
    public static void MakeKnown(BuildSettings build)
    {
        if (Find(build.Changelist) is not { } known)
        {
            return;
        }

        var core = WithCore(known.Name);
        build.CoreDefaultsAdded = false;
        if (HasCoreOff(build))
        {
            build.UseHttpProxy = core.UseHttpProxy;
            build.ClientDlls = core.ClientDlls;
            build.ServerDlls = core.ServerDlls;
        }

        build.ServerArguments = WithArguments(build.ServerArguments, CertServerRegion);
    }

    /// <summary>
    /// The known build's name for a build saved under a name an earlier launcher made up for it: a former known name
    /// ("Cert CL-3532353") or an unsupported build's ("Fortnite Cert CL-3541083"), keeping Add build's " (2)". Null for
    /// a name set by hand, a current name, or an unsupported changelist.
    /// </summary>
    public static string? CurrentName(string name, int changelist)
    {
        if (Find(changelist) is not { } known)
        {
            return null;
        }

        var match = CopySuffix().Match(name);
        var baseName = match.Groups["name"].Value;
        var generated = known.FormerNames.Contains(baseName, StringComparer.OrdinalIgnoreCase)
            || Regex.IsMatch(baseName, $@"^Fortnite (?:\S+ )?CL-{changelist}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return generated ? known.Name + match.Groups["copy"].Value : null;
    }

    /// <summary>Whether every Core option (-httpproxy and the DLLs) is off, as for an unsupported build when it was added.</summary>
    public static bool HasCoreOff(BuildSettings build) => !build.UseHttpProxy && build.ClientDlls.Count == 0 && build.ServerDlls.Count == 0;

    /// <summary>
    /// Turns <see cref="LauncherSettings.AllowUnsupportedBuilds"/> on or off. On, every unsupported build that still has
    /// every Core option off gets Core's defaults; off, the ones that got them that way and still have them unchanged go
    /// back to how they were. Known builds (supported or in progress) and builds set up by hand are left alone. Returns
    /// the builds it changed.
    /// </summary>
    public static IReadOnlyList<BuildSettings> SetAllowUnsupported(LauncherSettings settings, bool allow)
    {
        settings.AllowUnsupportedBuilds = allow;
        var changed = new List<BuildSettings>();
        foreach (var build in settings.Builds.Where(build => !IsKnown(build.Changelist)))
        {
            if (allow && HasCoreOff(build))
            {
                var core = WithCore(build.Name);
                build.UseHttpProxy = core.UseHttpProxy;
                build.ClientDlls = core.ClientDlls;
                build.ServerDlls = core.ServerDlls;
                build.ClientArguments = WithArguments(build.ClientArguments, core.ClientArguments);
                build.ServerArguments = WithArguments(build.ServerArguments, core.ServerArguments);
                build.CoreDefaultsAdded = true;
                changed.Add(build);
            }
            else if (!allow && build.CoreDefaultsAdded)
            {
                build.CoreDefaultsAdded = false;
                if (HasCoreDefaults(build))
                {
                    var core = WithCore(build.Name);
                    var without = WithoutCore(build.Name);
                    build.UseHttpProxy = false;
                    build.ClientDlls = [];
                    build.ServerDlls = [];
                    build.ClientArguments = WithoutArguments(build.ClientArguments, core.ClientArguments, without.ClientArguments);
                    build.ServerArguments = WithoutArguments(build.ServerArguments, core.ServerArguments, without.ServerArguments);
                    changed.Add(build);
                }
            }
        }

        return changed;
    }

    private static bool HasCoreDefaults(BuildSettings build)
    {
        var core = WithCore(build.Name);
        return build.UseHttpProxy == core.UseHttpProxy
            && build.ClientDlls.SequenceEqual(core.ClientDlls)
            && build.ServerDlls.SequenceEqual(core.ServerDlls)
            && Split(core.ClientArguments).All(Split(build.ClientArguments).Contains)
            && Split(core.ServerArguments).All(Split(build.ServerArguments).Contains);
    }

    /// <summary>Adds each of <paramref name="defaults"/> the arguments don't already set (a -Name=value counts as set whatever its value).</summary>
    private static string WithArguments(string arguments, string defaults)
    {
        var present = Split(arguments).Select(SwitchName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return string.Join(' ', Split(arguments).Concat(Split(defaults).Where(argument => !present.Contains(SwitchName(argument)))));
    }

    /// <summary>Removes the arguments <paramref name="core"/> adds over <paramref name="without"/>.</summary>
    private static string WithoutArguments(string arguments, string core, string without)
    {
        var added = Split(core).Except(Split(without), StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return string.Join(' ', Split(arguments).Where(argument => !added.Contains(argument)));
    }

    private static string[] Split(string arguments) => arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string SwitchName(string argument) => argument.Split('=', 2)[0];

    /// <summary>A build name and the " (2)" Add build gives a second build of the same name.</summary>
    [GeneratedRegex(@"^(?<name>.*?)(?<copy> \(\d+\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex CopySuffix();
}
