using Core.Launcher.Hosting.Dlls;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Hosting.Builds;

/// <summary>Per-build launch settings, keyed by changelist. A build missing here gets the same shape with no DLLs.</summary>
public static class KnownBuilds
{
    private const string CertClientArguments = "-epicapp=Fortnite -epicenv=Prod -epicportal -SkipPatchCheck -FORCELOGFLUSH";

    // Core-Docs/Tools/run_server.py and Backend-Evidence-3532353.md (launch line).
    private static readonly Dictionary<int, Func<BuildSettings>> Builds = new()
    {
        [3532353] = () => new BuildSettings
        {
            Name = "Cert CL-3532353",
            ClientArguments = CertClientArguments,
            ServerArguments = CertClientArguments + " -nullrhi -nosplash -nosound",
            UseHttpProxy = true,
            ClientDlls = [DllRole.Redirect, DllRole.Client],
            ServerDlls = [DllRole.Server],
        },
    };

    public static bool IsKnown(int changelist) => Builds.ContainsKey(changelist);

    public static BuildSettings Create(string folder, BuildVersion version)
    {
        var build = Builds.TryGetValue(version.Changelist, out var known)
            ? known()
            : new BuildSettings
            {
                Name = $"{version.BranchName.TrimStart('+')} CL-{version.Changelist}",
                ClientArguments = "-epicapp=Fortnite -epicenv=Prod -epicportal -SkipPatchCheck",
                ServerArguments = "-epicapp=Fortnite -epicenv=Prod -epicportal -SkipPatchCheck -nullrhi -nosplash -nosound",
            };

        build.Path = folder;
        return build;
    }
}
