using Core.Launcher.Hosting;
using Core.Launcher.Hosting.Backend;
using Core.Launcher.Hosting.Builds;
using Core.Launcher.Hosting.Logging;
using Core.Launcher.Hosting.Processes;
using Core.Launcher.Hosting.Servers;
using Core.Launcher.Hosting.Settings;
using Core.Server.Cli.Config;

namespace Core.Server.Cli.Hosting;

public sealed class GameServerStarter : IServerStarter
{
    private readonly ServerHostConfig _config;
    private readonly BuildSettings _build;
    private readonly BackendEndpoint _endpoint;
    private readonly GameLauncher _launcher;
    private readonly LauncherPaths _paths;
    private readonly BackendSessions _sessions;
    private readonly LogChannel _log;
    private readonly Dictionary<int, LogChannel> _serverLogs = [];
    private readonly Action<string>? _onEvent;
    private readonly Dictionary<int, bool> _slotTypes = [];

    public void SetSlotType(int instance, bool isBattleRoyale)
    {
        lock (_slotTypes)
        {
            _slotTypes[instance] = isBattleRoyale;
        }
    }

    public void RemoveSlotType(int instance)
    {
        lock (_slotTypes)
        {
            _slotTypes.Remove(instance);
            var higher = _slotTypes.Keys.Where(k => k > instance).OrderBy(k => k).ToList();
            foreach (var k in higher)
            {
                var val = _slotTypes[k];
                _slotTypes.Remove(k);
                _slotTypes[k - 1] = val;
            }
        }
    }

    public GameServerStarter(
        ServerHostConfig config,
        BuildSettings build,
        BackendEndpoint endpoint,
        GameLauncher launcher,
        LauncherPaths paths,
        BackendSessions sessions,
        LogChannel log,
        Action<string>? onEvent = null)
    {
        _config = config;
        _build = build;
        _endpoint = endpoint;
        _launcher = launcher;
        _paths = paths;
        _sessions = sessions;
        _log = log;
        _onEvent = onEvent;
    }

    public string? Blocker()
    {
        if (!KnownBuilds.CanHost(_build))
        {
            return KnownBuilds.NotSupported(_build, _config.AllowUnsupportedBuilds);
        }

        var isLocal = IsLoopback(_endpoint.Host);
        if (isLocal)
        {
            return null;
        }

        return _sessions.Access(_endpoint) switch
        {
            HostingAccess.Allowed => null,
            HostingAccess.Refused => $"Backend {_endpoint} refused this PC: add this PC's IP under Settings → Trusted Hosting IPs in their admin panel",
            _ => $"Connecting to {_endpoint}...",
        };
    }

    public IServerInstance Start(int instance)
    {
        var battleRoyale = HostsBattleRoyale(instance);
        var login = ServerLogin(instance);
        var arguments = LaunchArguments.Server(_build, _endpoint, login, battleRoyale, _config.Region);
        var output = ServerLog(instance);

        output.Write($"(server-host) --- {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
        output.Write($"(server-host) Starting instance {instance} for {_endpoint.Url} as {login}");

        if (KnownBuilds.SupportWarning(_build) is { } warning)
        {
            output.Write($"(server-host) {warning}");
        }

        WriteServerConfig();

        var game = _launcher.Start(new GameStart($"Server {instance}", _build, _build.ServerDlls, arguments, true, output));
        _onEvent?.Invoke($"[*] Server #{instance} started (PID {game.Id})");
        return game;
    }

    public SessionCheck Session(int instance, DateTimeOffset startedAt, IReadOnlyList<int> ports) =>
        _sessions.Check(_endpoint, ServerLogin(instance), startedAt, ports);

    public bool HostsBattleRoyale(int instance)
    {
        lock (_slotTypes)
        {
            if (_slotTypes.TryGetValue(instance, out var isBr))
            {
                return isBr;
            }
        }

        return KnownBuilds.HasBattleRoyale(_build.Changelist) && instance > _config.StandardServers;
    }

    public string ServerLogin(int instance)
    {
        var template = _config.ServerLogin.Length > 0 ? _config.ServerLogin : ServerSettings.DefaultLogin;

        // Custom login pattern configured in setup.ini or via --login
        if (!string.Equals(template, ServerSettings.DefaultLogin, StringComparison.OrdinalIgnoreCase))
        {
            return template.Contains("{n}", StringComparison.OrdinalIgnoreCase)
                ? template.Replace("{n}", instance.ToString(), StringComparison.OrdinalIgnoreCase)
                : $"{template}{instance}";
        }

        var isLocal = IsLoopback(_endpoint.Host);
        if (isLocal)
        {
            return $"CoreServer{instance}";
        }

        var pc = _config.PcName.Length > 0 ? _config.PcName : Environment.MachineName;
        return $"CoreServer-{ServerSettings.PcName(pc)}-{instance}";
    }

    public LogChannel ServerLog(int instance)
    {
        lock (_serverLogs)
        {
            if (!_serverLogs.TryGetValue(instance, out var log))
            {
                log = new LogChannel($"Server{instance}", Path.Combine(_paths.Logs, $"Server{instance}.log"));
                _serverLogs[instance] = log;
            }

            return log;
        }
    }

    private void WriteServerConfig()
    {
        try
        {
            if (ServerConfig.WriteEngineIni(_paths.ServerSaved))
            {
                _log.Write($"Wrote {ServerConfig.EngineIniPath(_paths.ServerSaved)}: servers run without XMPP");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Write($"Couldn't write the servers' Engine.ini ({exception.Message}); they will try XMPP");
        }
    }

    private static bool IsLoopback(string host) =>
        string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
}
