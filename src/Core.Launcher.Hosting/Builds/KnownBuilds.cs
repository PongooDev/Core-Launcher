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

/// <summary>A region servers can advertise: its code for -McpRegion= and the name the client's settings show for it.</summary>
public sealed record ServerRegion(string Code, string Name);

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
    /// Fortnite 1.8, ++Fortnite+Release-Live CL-3724489 on UE 4.16.0. Plays like 1.7.2; its exe drops -nullrhi unless the
    /// command line whitelist lets it through (<see cref="ExePatcher"/>).
    /// </summary>
    public const int Fortnite18 = 3724489;

    /// <summary>Fortnite 1.8.1, ++Fortnite+Release-Cert CL-3729133 on UE 4.16.0: 1.8's patch (voice chat muting, Battle Royale
    /// fixes).</summary>
    public const int Fortnite181 = 3729133;

    /// <summary>Fortnite 1.8.2, ++Fortnite+Release-Cert CL-3741772 on UE 4.16.0: the Bush and the Battle Royale Season Shop.</summary>
    public const int Fortnite182 = 3741772;

    /// <summary>Fortnite 1.9, ++Fortnite+Release-Live CL-3757339 on UE 4.16.0: Battle Royale launch pads and per playlist loot.</summary>
    public const int Fortnite19 = 3757339;

    /// <summary>Fortnite 1.9.1, ++Fortnite+Release-Cert CL-3775276 on UE 4.16.0: smoke grenades and the end of Fortnitemares.</summary>
    public const int Fortnite191 = 3775276;

    /// <summary>Fortnite 1.10, ++Fortnite+Release-Cert CL-3790078 on UE 4.19.0: fixed Mutant Storm targets, the 50v50 mode and the Winter Bus, on the first UE 4.19 build.</summary>
    public const int Fortnite110 = 3790078;

    /// <summary>Fortnite 1.11, ++Fortnite+Release-Cert CL-3807424 on UE 4.19.0: the Winterfest update, with the holiday map, Snowfall and the Frostnite mode.</summary>
    public const int Fortnite111 = 3807424;

    /// <summary>Fortnite 2.1, ++Fortnite+Release-Cert CL-3825894 on UE 4.19.0: the first 2.x build, in Season 2.</summary>
    public const int Fortnite21 = 3825894;

    /// <summary>Fortnite 2.2, ++Fortnite+Release-Cert CL-3841827 on UE 4.19.0: Season 2's 2.2.0 update.</summary>
    public const int Fortnite22 = 3841827;

    /// <summary>Fortnite 2.3, ++Fortnite+Release-Cert CL-3847564 on UE 4.19.0: Season 2's 2.3.0 update.</summary>
    public const int Fortnite23 = 3847564;

    /// <summary>Fortnite 2.4, ++Fortnite+Release-Cert CL-3858292 on UE 4.19.0: Season 2's 2.4.0 update.</summary>
    public const int Fortnite24 = 3858292;

    /// <summary>Fortnite 2.4.2, ++Fortnite+Release-Next CL-3870737 on UE 4.19.0: Season 2's 2.4.2 update.</summary>
    public const int Fortnite242 = 3870737;

    /// <summary>Fortnite 2.5, ++Fortnite+Release-2.5 CL-3889387 on UE 4.20.0: Season 2's 2.5.0 update.</summary>
    public const int Fortnite25 = 3889387;

    /// <summary>Fortnite 3.0, ++Fortnite+Release-3.0.0 CL-3901517 on UE 4.20.0: the first build of Season 3.</summary>
    public const int Fortnite30 = 3901517;

    /// <summary>Fortnite 3.1, ++Fortnite+Release-3.1 CL-3915963 on UE 4.20.0: Season 3's 3.1.0 update.</summary>
    public const int Fortnite31 = 3915963;

    /// <summary>Fortnite 3.1.1, ++Fortnite+Release-3.1 CL-3917250 on UE 4.20.0: 3.1.1 — crash fixes on top of 3.1.</summary>
    public const int Fortnite311 = 3917250;

    /// <summary>Fortnite 3.2, ++Fortnite+Release-3.2 CL-3935073 on UE 4.20.0: Season 3's 3.2 update.</summary>
    public const int Fortnite32 = 3935073;

    /// <summary>
    /// Fortnite 3.3, ++Fortnite+Release-3.3 CL-3942182 on UE 4.20.0, released 15 March 2018: Battle Royale gets Remote Explosives
    /// (a rare, aim-down-sights detonated explosive, stacks of 4) and the Supply Llama (3 per match at random spots, opened
    /// like a chest), loses the Smoke Grenade to the vault, and the Hunting Rifle drops its 'No Scope' name. Treasure chests
    /// get 500 health and ammo boxes 250. The Blitz mode (short storm, more loot) is enabled on 19 March. Save the World gets
    /// the Luck of the Storm questline and the Shamrock Reclaimer Outlander, and reverts v3.2's Critical Chance change.
    /// </summary>
    public const int Fortnite33 = 3942182;

    /// <summary>
    /// Fortnite 3.5.1, ++Fortnite+Release-3.5 CL-4000805 on UE 4.20.0, the 11 April 2018 update (v3.50 plus its 3.5.1 hotfix):
    /// Battle Royale gets Replay Mode, the Port-A-Fort (an epic thrown item that builds a three-story metal fort at the impact
    /// point; stacks of 1, up to 5), a short equip time on slower weapons, a weaker Guided Missile and the updated 50v50 mode.
    /// Save the World gets the Cyberpunk Heroes and Neon Weapons. The 3.5.1 hotfix fixes sprint-fire and Replay crashes.
    /// </summary>
    public const int Fortnite351 = 4000805;

    /// <summary>
    /// Fortnite 3.5.2, ++Fortnite+Release-3.5 CL-4008490 on UE 4.20, a later build of 3.5's content update. The archive is Battle
    /// Royale only, like 3.5.1's, with only pakchunk0, so Save the World content is not in it.
    /// </summary>
    public const int Fortnite352 = 4008490;

    /// <summary>
    /// Fortnite 3.6, ++Fortnite+Release-3.6 CL-4019403 on UE 4.20. The archive is Battle Royale only, like 3.5.x's, with only
    /// pakchunk0, so Save the World content is not in it.
    /// </summary>
    public const int Fortnite36 = 4019403;

    /// <summary>
    /// Fortnite 4.0, ++Fortnite+Release-4.0 CL-4039451 on UE 4.20, Season 4's launch build. Unlike 3.5 and 3.6, the archive is a
    /// full install with the Save the World paks (pakchunk1, 2, 5, 7, 8 and 9) next to Battle Royale's.
    /// </summary>
    public const int Fortnite40 = 4039451;

    /// <summary>
    /// Fortnite 4.1, ++Fortnite+Release-4.1 CL-4053532 on UE 4.20: Season 4's 4.1 update. Like 4.0, the archive is a full install
    /// with the Save the World paks next to Battle Royale's.
    /// </summary>
    public const int Fortnite41 = 4053532;

    /// <summary>Fortnite 4.2, ++Fortnite+Release-4.2 CL-4072250 on UE 4.20: Season 4's 4.2 update.</summary>
    public const int Fortnite42 = 4072250;

    /// <summary>
    /// What a Battle Royale server gets. Core takes the map and playlist from the build's own data (on 1.7.2 the Battle
    /// Royale zone theme's Athena_Terrain and solo playlist; the match's playlist comes with its assignment) and
    /// registers the server with the matchmaking service that Battle Royale's players queue on, so the launcher needs no
    /// per-build map. Core-Docs/Athena-Plan.md.
    /// </summary>
    public const string BattleRoyaleServerArguments = "-CoreMode=BattleRoyale";

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

    /// <summary>
    /// The regions a server can advertise (<see cref="Backend.BackendRegion"/>): the client's Matchmaking Region list, the
    /// visible Datacenters of its DefaultEngine.ini (1.8.2: NA, EU, OCE; 1.9 and later add BR and ASIA). A player who
    /// picks one queues for its servers, and Core-Backend sends players whose region has no server to another region's
    /// (Core-Backend #121).
    /// </summary>
    public static IReadOnlyList<ServerRegion> ServerRegions { get; } =
    [
        new("NA", "North America"), new("EU", "Europe"), new("OCE", "Oceania"), new("BR", "Brazil"), new("ASIA", "Asia"),
    ];

    /// <summary>
    /// The build's server arguments with the hosted servers' region (the backend's first Server Region,
    /// <see cref="Backend.BackendRegion"/>) in place of the default <see cref="CertServerRegion"/>. A build whose arguments
    /// name another region keeps it (<see cref="OwnServerRegion"/>): that was set for the build by hand.
    /// </summary>
    public static string WithServerRegion(string arguments, string? region)
    {
        if (string.IsNullOrWhiteSpace(region) || OwnServerRegion(arguments) is not null)
        {
            return arguments;
        }

        var split = Split(arguments);
        var own = RegionSwitch(split);
        return string.Join(' ', split.Where(argument => argument != own).Append($"-McpRegion={region.Trim()}"));
    }

    /// <summary>The region a build's server arguments name by hand (an -McpRegion= other than <see cref="CertServerRegion"/>), else null.</summary>
    public static string? OwnServerRegion(string arguments) =>
        RegionSwitch(Split(arguments)) is { } own && !own.Equals(CertServerRegion, StringComparison.OrdinalIgnoreCase)
            ? own[(own.IndexOf('=') + 1)..]
            : null;

    private static string? RegionSwitch(IEnumerable<string> arguments) =>
        arguments.FirstOrDefault(argument => SwitchName(argument).Equals("-McpRegion", StringComparison.OrdinalIgnoreCase));

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
        new(Fortnite172, "1.7.2", "Fortnite 1.7.2 (CL-3700114)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite18, "1.8", "Fortnite 1.8 (CL-3724489)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite181, "1.8.1", "Fortnite 1.8.1 (CL-3729133)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite182, "1.8.2", "Fortnite 1.8.2 (CL-3741772)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite19, "1.9", "Fortnite 1.9 (CL-3757339)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite191, "1.9.1", "Fortnite 1.9.1 (CL-3775276)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite110, "1.10", "Fortnite 1.10 (CL-3790078)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite111, "1.11", "Fortnite 1.11 (CL-3807424)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite21, "2.1", "Fortnite 2.1 (CL-3825894)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite22, "2.2", "Fortnite 2.2 (CL-3841827)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite23, "2.3", "Fortnite 2.3 (CL-3847564)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite24, "2.4", "Fortnite 2.4 (CL-3858292)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite242, "2.4.2", "Fortnite 2.4.2 (CL-3870737)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite25, "2.5", "Fortnite 2.5 (CL-3889387)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite30, "3.0", "Fortnite 3.0 (CL-3901517)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite31, "3.1", "Fortnite 3.1 (CL-3915963)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite311, "3.1.1", "Fortnite 3.1.1 (CL-3917250)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite32, "3.2", "Fortnite 3.2 (CL-3935073)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite33, "3.3", "Fortnite 3.3 (CL-3942182)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite351, "3.5.1", "Fortnite 3.5.1 (CL-4000805)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite352, "3.5.2", "Fortnite 3.5.2 (CL-4008490)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite36, "3.6", "Fortnite 3.6 (CL-4019403)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite40, "4.0", "Fortnite 4.0 (CL-4039451)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite41, "4.1", "Fortnite 4.1 (CL-4053532)", BuildSupport.Full, [], BattleRoyale: true),
        new(Fortnite42, "4.2", "Fortnite 4.2 (CL-4072250)", BuildSupport.InProgress, [], BattleRoyale: true),
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

    /// <summary>How many servers the launcher keeps running for this build: Battle Royale's come on top of the others.</summary>
    public static int ServerCount(ServerSettings servers, BuildSettings? build) =>
        servers.Instances + (build is not null && HasBattleRoyale(build.Changelist) ? servers.BattleRoyaleInstances : 0);

    /// <summary>The known build's name ("Fortnite 1.2 (CL-3541083)"), or null for an unsupported changelist.</summary>
    public static string? NameOf(int changelist) => Find(changelist)?.Name;

    /// <summary>"Fortnite 1.1 to 1.7.2" (the fully supported builds, for messages).</summary>
    public static string SupportedNames => Versions(BuildSupport.Full);

    /// <summary>"Fortnite 1.8" (the build whose support is in progress, for messages), or "" when none is.</summary>
    public static string InProgressNames => Versions(BuildSupport.InProgress);

    /// <summary>"Fortnite 1.1 to 1.7.2, and support for Fortnite 1.8 is in progress".</summary>
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
