using Core.Launcher.Hosting.Backend;
using Core.Launcher.Hosting.Builds;
using Core.Launcher.Hosting.Dlls;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Hosting.Processes;

public static class LaunchArguments
{
    /// <summary>Core-Backend accepts any password, so every account logs in with this one until accounts get real credentials.</summary>
    public const string Password = "Core";

    public static string Client(BuildSettings build, int backendPort, string playerName) => Client(build, BackendEndpoint.Local(backendPort), playerName);

    /// <summary>A client of the backend at <paramref name="backend"/>: this PC's, or a friend's.</summary>
    public static string Client(BuildSettings build, BackendEndpoint backend, string playerName) =>
        CommandLine.Join([build.ClientArguments, .. Common(build, build.ClientDlls, backend, playerName)]);

    /// <summary>
    /// Switches every server needs, whatever its build's arguments say. -nullrhi: the engine's own null-RHI decision for
    /// servers is folded out of the client exe. -unattended: as Epic's dedicated servers run, FMessageDialog::Open logs
    /// the message and answers OK instead of opening a modal window that blocks startup (FApp::IsUnattended() reads it
    /// once, so it has to be on the original command line). Clients never get them.
    /// </summary>
    public static IReadOnlyList<string> ServerSwitches { get; } = ["-nullrhi", "-unattended"];

    /// <param name="battleRoyale">A Battle Royale server (<see cref="KnownBuilds.BattleRoyaleServerArguments"/>).</param>
    public static string Server(BuildSettings build, int backendPort, string login, bool battleRoyale = false) =>
        CommandLine.Join([build.ServerArguments, .. Common(build, build.ServerDlls, BackendEndpoint.Local(backendPort), login),
            .. ServerSwitches.Where(name => !HasSwitch(build.ServerArguments, name)), .. battleRoyale ? [KnownBuilds.BattleRoyaleServerArguments] : Array.Empty<string>()]);

    private static bool HasSwitch(string arguments, string name) =>
        arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(argument => argument.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Common(BuildSettings build, IReadOnlyList<DllRole> dlls, BackendEndpoint backend, string login)
    {
        if (build.UseHttpProxy)
        {
            yield return $"-httpproxy={backend.Authority}";
        }

        // CoreRedirect sends Epic's hosts here (CoreV2 Core/Private/Configuration.cpp, default http://127.0.0.1:5595):
        // it puts this in front of each request's path, so any host and port work.
        if (dlls.Contains(DllRole.Redirect))
        {
            yield return $"-CoreBackend={backend.Url}";
        }

        yield return CommandLine.Switch("AUTH_LOGIN", login);
        yield return CommandLine.Switch("AUTH_PASSWORD", Password);
        yield return "-AUTH_TYPE=epic";
    }
}
