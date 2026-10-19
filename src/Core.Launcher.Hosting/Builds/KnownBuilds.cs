using Core.Launcher.Hosting.Dlls;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Hosting.Builds;

/// <summary>Per-build launch settings, keyed by changelist. A build missing here gets the same shape with no DLLs.</summary>
public static class KnownBuilds
{
    public const int Cert = 3532353;

    // No -FORCELOGFLUSH: it can deadlock the game at start-up (see SettingsStore's version 1 migration).
    private const string CertClientArguments = "-epicapp=Fortnite -epicenv=Prod -epicportal -SkipPatchCheck";

    /// <summary>
    /// The region a Cert server advertises. Its UQosRegionManager reads -McpRegion= and otherwise answers "NONE", which
    /// never matches the Cert client's matchMakingRequest (REGION_s EQUAL NA). Core-Docs/Session-Plan.md §6 L1.
    /// </summary>
    public const string CertServerRegion = "-McpRegion=NA";

    // Core-Docs/Tools/run_server.py and Backend-Evidence-3532353.md (launch line); DLL order from CoreV2 (redirect first).
    private static readonly Dictionary<int, Func<BuildSettings>> Builds = new()
    {
        [Cert] = () => new BuildSettings
        {
            Name = "Cert CL-3532353",
            ClientArguments = CertClientArguments,
            ServerArguments = CertClientArguments + " -nullrhi -nosplash -nosound " + CertServerRegion,
            UseHttpProxy = true,
            ClientDlls = [DllRole.Redirect, DllRole.Client],
            ServerDlls = [DllRole.Redirect, DllRole.Server],
        },
    };

    public static bool IsKnown(int changelist) => Builds.ContainsKey(changelist);

    /// <summary>"Cert CL-3532353" (every supported build, for messages).</summary>
    public static string SupportedNames => string.Join(", ", Builds.Values.Select(create => create().Name));

    /// <summary>
    /// Whether Core can run this build as set up: it is a supported build, or someone has given it the Core DLLs by
    /// hand. An unsupported build left as added would start the client against Epic's own services and "servers"
    /// without Core.dll.
    /// </summary>
    public static bool CanPlay(BuildSettings build) => build.UseHttpProxy || build.ClientDlls.Contains(DllRole.Redirect);

    public static bool CanHost(BuildSettings build) => build.ServerDlls.Contains(DllRole.Server);

    public static string NotSupported(BuildSettings build) =>
        IsKnown(build.Changelist)
            ? $"{build.Name} has the Core DLLs turned off (Settings → Builds)"
            : $"Core doesn't support {build.Name} yet. It supports {SupportedNames}.";

    public static BuildSettings Create(string folder, BuildVersion version)
    {
        var build = Builds.TryGetValue(version.Changelist, out var known)
            ? known()
            : new BuildSettings
            {
                Name = version.Changelist == 0 ? "Fortnite build"
                    : version.ShortName.Length > 0 ? $"Fortnite {version.ShortName} CL-{version.Changelist}"
                    : $"Fortnite CL-{version.Changelist}",
                ClientArguments = "-epicapp=Fortnite -epicenv=Prod -epicportal -SkipPatchCheck",
                ServerArguments = "-epicapp=Fortnite -epicenv=Prod -epicportal -SkipPatchCheck -nullrhi -nosplash -nosound",
            };

        build.Path = folder;
        build.Changelist = version.Changelist;
        return build;
    }
}
