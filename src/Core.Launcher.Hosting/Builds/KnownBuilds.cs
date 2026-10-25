using Core.Launcher.Hosting.Dlls;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Hosting.Builds;

/// <summary>
/// Per-build launch settings, keyed by changelist. A build missing here gets the same shape with no DLLs, or Core's
/// defaults when <see cref="LauncherSettings.AllowUnsupportedBuilds"/> is on.
/// </summary>
public static class KnownBuilds
{
    public const int Cert = 3532353;

    /// <summary>The Settings → Builds switch that lets unsupported builds use Core, by its label.</summary>
    public const string AllowUnsupportedSetting = "Allow unsupported builds";

    // No -FORCELOGFLUSH: it can deadlock the game at start-up (see SettingsStore's version 1 migration).
    private const string ClientArguments = "-epicapp=Fortnite -epicenv=Prod -epicportal -SkipPatchCheck";

    private const string ServerArguments = ClientArguments + " -nullrhi -nosplash -nosound";

    /// <summary>
    /// The region a Cert server advertises. Its UQosRegionManager reads -McpRegion= and otherwise answers "NONE", which
    /// never matches the Cert client's matchMakingRequest (REGION_s EQUAL NA). Core-Docs/Session-Plan.md §6 L1.
    /// </summary>
    public const string CertServerRegion = "-McpRegion=NA";

    // Core-Docs/Tools/run_server.py and Backend-Evidence-3532353.md (launch line); DLL order from CoreV2 (redirect first).
    private static readonly Dictionary<int, Func<BuildSettings>> Builds = new()
    {
        [Cert] = () => WithCore("Cert CL-3532353"),
    };

    /// <summary>
    /// Core's launch settings: what every supported build gets, and what <see cref="AllowUnsupportedSetting"/> gives
    /// the others.
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

    public static bool IsKnown(int changelist) => Builds.ContainsKey(changelist);

    /// <summary>"Cert CL-3532353" (every supported build, for messages).</summary>
    public static string SupportedNames => string.Join(", ", Builds.Values.Select(create => create().Name));

    /// <summary>
    /// Whether Core can run this build as set up: it is a supported build, or it has the Core DLLs (by hand, or from
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
        $"Core doesn't support {name} yet. It supports {SupportedNames}; turn on {AllowUnsupportedSetting} in Settings → Builds to try it.";

    /// <summary>The warning for an unsupported build while <see cref="AllowUnsupportedSetting"/> is on.</summary>
    public static string UnsupportedWarning(string name) =>
        $"{name} isn't supported and may not work: Core is only tested on {SupportedNames}.";

    public static BuildSettings Create(string folder, BuildVersion version, bool allowUnsupported)
    {
        BuildSettings build;
        if (Builds.TryGetValue(version.Changelist, out var known))
        {
            build = known();
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

    /// <summary>Whether every Core option (-httpproxy and the DLLs) is off, as for an unsupported build when it was added.</summary>
    public static bool HasCoreOff(BuildSettings build) => !build.UseHttpProxy && build.ClientDlls.Count == 0 && build.ServerDlls.Count == 0;

    /// <summary>
    /// Turns <see cref="LauncherSettings.AllowUnsupportedBuilds"/> on or off. On, every unsupported build that still has
    /// every Core option off gets Core's defaults; off, the ones that got them that way and still have them unchanged go
    /// back to how they were. Builds set up by hand are left alone. Returns the builds it changed.
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
}
