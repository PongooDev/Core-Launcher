using Core.Launcher.Hosting.Dlls;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Hosting.Processes;

public static class LaunchArguments
{
    /// <summary>Core-Backend accepts any password, so every account logs in with this one until accounts get real credentials.</summary>
    public const string Password = "Core";

    public static string Client(BuildSettings build, int backendPort, string playerName) =>
        CommandLine.Join([build.ClientArguments, .. Common(build, build.ClientDlls, backendPort, playerName)]);

    /// <summary>
    /// Servers always run -unattended, as Epic's dedicated servers do: FMessageDialog::Open then logs the message and
    /// answers OK instead of opening a modal window that blocks startup. FApp::IsUnattended() reads it once, so it has to
    /// be on the original command line. Clients never get it.
    /// </summary>
    public static string Server(BuildSettings build, int backendPort, string login) =>
        CommandLine.Join([build.ServerArguments, .. Common(build, build.ServerDlls, backendPort, login), HasSwitch(build.ServerArguments, "unattended") ? "" : "-unattended"]);

    private static bool HasSwitch(string arguments, string name) =>
        arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(argument => argument.Equals("-" + name, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Common(BuildSettings build, IReadOnlyList<DllRole> dlls, int backendPort, string login)
    {
        if (build.UseHttpProxy)
        {
            yield return $"-httpproxy=127.0.0.1:{backendPort}";
        }

        // CoreRedirect sends Epic's hosts here (CoreV2 Core/Private/Configuration.cpp, default http://127.0.0.1:5595).
        if (dlls.Contains(DllRole.Redirect))
        {
            yield return $"-CoreBackend=http://127.0.0.1:{backendPort}";
        }

        yield return CommandLine.Switch("AUTH_LOGIN", login);
        yield return CommandLine.Switch("AUTH_PASSWORD", Password);
        yield return "-AUTH_TYPE=epic";
    }
}
