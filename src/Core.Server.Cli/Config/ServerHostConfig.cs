using Core.Launcher.Hosting;
using Core.Launcher.Hosting.Backend;

namespace Core.Server.Cli.Config;

public sealed record ServerHostConfig
{
    public string BuildPath { get; init; } = "";
    public string BackendUrl { get; init; } = "http://127.0.0.1:5595";
    public string Region { get; init; } = "NA";
    public int StandardServers { get; set; } = 2;
    public int BattleRoyaleServers { get; set; } = 2;
    public string ServerLogin { get; init; } = "CoreServer{n}";
    public string PcName { get; init; } = "";
    public int ReadyTimeoutSeconds { get; init; } = 180;
    public bool AllowUnsupportedBuilds { get; init; }
    public string DllsFolder { get; init; } = "";
    public string DataFolder { get; init; } = "";
    public bool HelpRequested { get; init; }
    public bool VersionRequested { get; init; }

    public static ServerHostConfig FromIniAndArgs(SettingsIni ini, string[] args)
    {
        var buildPath = ini.Get("Server", "BuildPath") ?? "";
        var backendUrl = ini.Get("Server", "BackendUrl") ?? "http://127.0.0.1:5595";
        var region = ini.Get("Server", "Region") ?? "NA";
        var standard = int.TryParse(ini.Get("Server", "StandardServers"), out var std) ? Math.Max(0, std) : 2;
        var br = int.TryParse(ini.Get("Server", "BattleRoyaleServers"), out var brVal) ? Math.Max(0, brVal) : 2;
        var serverLogin = ini.Get("Server", "ServerLogin") ?? ini.Get("Server", "Login") ?? "CoreServer{n}";
        var pcName = ini.Get("Server", "PcName") ?? "";
        var timeout = int.TryParse(ini.Get("Server", "ReadyTimeout"), out var t) ? Math.Max(0, t) : 180;
        var unsupported = bool.TryParse(ini.Get("Server", "AllowUnsupportedBuilds"), out var unsupp) && unsupp;
        var dllsFolder = ini.Get("Paths", "DllsFolder") ?? "";
        var dataFolder = ini.Get("Paths", "DataFolder") ?? "";

        var help = false;
        var version = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) || arg.Equals("-h", StringComparison.OrdinalIgnoreCase))
            {
                help = true;
            }
            else if (arg.Equals("--version", StringComparison.OrdinalIgnoreCase) || arg.Equals("-v", StringComparison.OrdinalIgnoreCase))
            {
                version = true;
            }
            else if (arg.Equals("--build", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                buildPath = args[++i];
            }
            else if (arg.Equals("--backend", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                backendUrl = args[++i];
            }
            else if (arg.Equals("--region", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                region = args[++i];
            }
            else if (arg.Equals("--servers", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[++i], out var s))
            {
                standard = Math.Max(0, s);
            }
            else if (arg.Equals("--br-servers", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[++i], out var b))
            {
                br = Math.Max(0, b);
            }
            else if ((arg.Equals("--login", StringComparison.OrdinalIgnoreCase) || arg.Equals("--server-login", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                serverLogin = args[++i];
            }
            else if (arg.Equals("--pc-name", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                pcName = args[++i];
            }
            else if (arg.Equals("--ready-timeout", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[++i], out var rt))
            {
                timeout = Math.Max(0, rt);
            }
            else if (arg.Equals("--allow-unsupported", StringComparison.OrdinalIgnoreCase))
            {
                unsupported = true;
            }
            else if (arg.Equals("--dlls", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                dllsFolder = args[++i];
            }
            else if (arg.Equals("--data", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                dataFolder = args[++i];
            }
        }

        return new ServerHostConfig
        {
            BuildPath = buildPath.Trim(),
            BackendUrl = backendUrl.Trim(),
            Region = region.Trim(),
            StandardServers = standard,
            BattleRoyaleServers = br,
            ServerLogin = serverLogin.Trim(),
            PcName = pcName.Trim(),
            ReadyTimeoutSeconds = timeout,
            AllowUnsupportedBuilds = unsupported,
            DllsFolder = dllsFolder.Trim(),
            DataFolder = dataFolder.Trim(),
            HelpRequested = help,
            VersionRequested = version,
        };
    }

    public BackendEndpoint ParseEndpoint()
    {
        // http://host[:port] (5595 by default), or https://host[:port] (443) for a backend behind an https reverse proxy.
        return BackendEndpoint.TryParse(BackendUrl, out var endpoint, out _) ? endpoint : BackendEndpoint.Local(BackendEndpoint.DefaultPort);
    }

    public LauncherPaths ResolvePaths() =>
        DataFolder.Length > 0 ? new LauncherPaths(Path.GetFullPath(DataFolder)) : LauncherPaths.Default;

    public void SyncToIni(SettingsIni ini)
    {
        ini.Set("Server", "BuildPath", BuildPath);
        ini.Set("Server", "BackendUrl", BackendUrl);
        ini.Set("Server", "Region", Region);
        ini.Set("Server", "StandardServers", StandardServers.ToString());
        ini.Set("Server", "BattleRoyaleServers", BattleRoyaleServers.ToString());
        ini.Set("Server", "ServerLogin", ServerLogin);
        ini.Set("Server", "PcName", PcName);
        ini.Set("Server", "ReadyTimeout", ReadyTimeoutSeconds.ToString());
        ini.Set("Server", "AllowUnsupportedBuilds", AllowUnsupportedBuilds ? "true" : "false");
        ini.Set("Paths", "DllsFolder", DllsFolder);
        ini.Set("Paths", "DataFolder", DataFolder);
    }
}

