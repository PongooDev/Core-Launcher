using Core.Launcher.Hosting;
using Core.Launcher.Hosting.Backend;
using Core.Launcher.Hosting.Builds;
using Core.Launcher.Hosting.Components;
using Core.Launcher.Hosting.Dlls;
using Core.Launcher.Hosting.Logging;
using Core.Launcher.Hosting.Native;
using Core.Launcher.Hosting.Processes;
using Core.Launcher.Hosting.Servers;
using Core.Launcher.Hosting.Settings;
using Core.Server.Cli.Config;
using Core.Server.Cli.Metrics;

namespace Core.Server.Cli.Hosting;

public sealed record ServerHostStatus(
    BuildSettings Build,
    BackendEndpoint Endpoint,
    string Region,
    string PcLogin,
    int TotalConfigured,
    IReadOnlyList<ServerSlotView> Slots,
    SystemSnapshot SystemMetrics,
    TotalProcessSnapshot ProcessMetrics,
    IReadOnlyList<string> RecentEvents,
    int StandardServers = 2,
    int BattleRoyaleServers = 2,
    IReadOnlyDictionary<int, string>? SlotTypes = null,
    ServerAssetsProgress? ServerAssets = null);

public sealed class ServerHostRunner : IAsyncDisposable
{
    private readonly ServerHostConfig _config;
    private readonly string? _iniPath;
    private readonly LauncherPaths _paths;
    private readonly JobObject _job = new();
    private readonly HttpClient _http = GitHubClient.CreateHttpClient();
    private readonly BackendSessions _sessions;
    private readonly LogChannel _log;
    private readonly DllStore _dlls;
    private readonly GameLauncher _launcher;
    private readonly SystemMetrics _systemMetrics = new();
    private readonly ProcessMetrics _processMetrics = new();
    private readonly List<string> _events = [];
    private readonly Lock _gate = new();
    private readonly Dictionary<int, ServerState> _lastStates = [];

    private GameServerStarter? _starter;
    private ServerPool? _pool;
    private BuildSettings? _build;
    private BackendEndpoint? _endpoint;
    private bool _started;

    public ServerHostRunner(ServerHostConfig config, string? iniPath = null)
    {
        _config = config;
        _iniPath = iniPath;
        _paths = config.ResolvePaths();
        _log = new LogChannel("ServerHost", Path.Combine(_paths.Logs, "ServerHost.log"), timestamp: true);
        _sessions = new BackendSessions(BackendSessions.CreateHttpClient(), TimeProvider.System);

        var github = new GitHubClient(_http);
        _dlls = new DllStore(_paths, github, _log.Write);
        _launcher = new GameLauncher(_job, _dlls, _paths, _log);
    }

    public LauncherPaths Paths => _paths;

    public ServerPool? Pool => _pool;

    public BuildSettings? Build => _build;

    public BackendEndpoint? Endpoint => _endpoint;

    public event Action? Changed;

    public async Task StartAsync(CancellationToken cancellation = default)
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }

        _log.Write("Initializing CoreServerHost...");

        // Configure DLL store
        if (_config.DllsFolder.Length > 0 && Directory.Exists(_config.DllsFolder))
        {
            _dlls.Configure(new DllSettings { Source = DllSource.Local, LocalFolder = Path.GetFullPath(_config.DllsFolder) });
            _log.Write($"Using local DLLs from {_config.DllsFolder}");
        }
        else
        {
            _dlls.Configure(new DllSettings { Source = DllSource.Release });
            AddEvent("[*] Checking Core V2 release DLLs...");
            try
            {
                await _dlls.RefreshAsync(null, cancellation);
                AddEvent($"[+] Core DLLs ready ({_dlls.ReleaseTag ?? "cached"})");
            }
            catch (Exception exception)
            {
                _log.Write($"DLL refresh warning: {exception.Message}");
                AddEvent($"[!] Core DLL refresh warning: {exception.Message}");
            }
        }

        // Detect or load build
        var detected = BuildDetector.Detect(_config.BuildPath);
        if (detected is null || detected.Version == BuildVersion.Unknown)
        {
            throw new InvalidOperationException($"Could not detect a valid Fortnite build in: {_config.BuildPath}");
        }

        _build = KnownBuilds.Create(detected.Folder, detected.Version, _config.AllowUnsupportedBuilds);
        _endpoint = _config.ParseEndpoint();

        var serverAssets = new ServerAssets(_http, _paths.ServerAssets, _log.Write);
        serverAssets.ProgressChanged += () => Changed?.Invoke();
        _starter = new GameServerStarter(_config, _build, _endpoint, _launcher, _paths, _sessions, _log, AddEvent, serverAssets);
        _pool = new ServerPool(_starter, _log, TimeProvider.System);

        var totalInstances = _config.StandardServers + (KnownBuilds.HasBattleRoyale(_build.Changelist) ? _config.BattleRoyaleServers : 0);
        for (var i = 1; i <= _config.StandardServers; i++)
        {
            _starter.SetSlotType(i, false);
        }
        for (var i = _config.StandardServers + 1; i <= totalInstances; i++)
        {
            _starter.SetSlotType(i, true);
        }

        _pool.Configure(totalInstances, TimeSpan.FromSeconds(_config.ReadyTimeoutSeconds));
        _pool.Changed += OnPoolChanged;
        _pool.SetEnabled(true);
        _pool.StartTimer();

        AddEvent($"[+] Server pool initialized: {totalInstances} instances target for {_endpoint.Url}");
        _log.Write($"Target build: {_build.Name} (CL {_build.Changelist})");
    }

    private void OnPoolChanged()
    {
        if (_pool is null) return;

        foreach (var slot in _pool.Slots)
        {
            _lastStates.TryGetValue(slot.Instance, out var prev);
            if (slot.State == ServerState.Ready && prev != ServerState.Ready)
            {
                var ports = slot.Ports.Count > 0 ? $"on UDP {slot.Ports[0]}" : "";
                AddEvent($"[+] Server #{slot.Instance} ready {ports}".Trim());
            }
            else if (prev == ServerState.Ready && slot.State != ServerState.Ready && _pool.Enabled)
            {
                AddEvent($"[!] Server #{slot.Instance} is no longer ready (now {slot.State})");
            }
            _lastStates[slot.Instance] = slot.State;
        }

        Changed?.Invoke();
    }

    public void AddEvent(string text)
    {
        lock (_events)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            _events.Add($"{timestamp}  {text}");
            if (_events.Count > 20)
            {
                _events.RemoveAt(0);
            }
        }

        _log.Write(text);
        Changed?.Invoke();
    }

    public ServerHostStatus GetSnapshot()
    {
        var build = _build ?? KnownBuilds.Create("", BuildVersion.Unknown, false);
        var endpoint = _endpoint ?? _config.ParseEndpoint();
        var slots = _pool?.Slots ?? [];
        var pids = slots.Where(s => s.ProcessId.HasValue).Select(s => s.ProcessId!.Value).ToList();

        var sys = _systemMetrics.Sample();
        var proc = _processMetrics.Sample(pids);

        List<string> eventsCopy;
        lock (_events)
        {
            eventsCopy = [.. _events.TakeLast(6)];
        }

        var totalInstances = _config.StandardServers + (KnownBuilds.HasBattleRoyale(build.Changelist) ? _config.BattleRoyaleServers : 0);
        var pcLogin = _starter?.ServerLogin(1)
                      ?? (_config.ServerLogin.Contains("{n}", StringComparison.OrdinalIgnoreCase)
                          ? _config.ServerLogin.Replace("{n}", "1", StringComparison.OrdinalIgnoreCase)
                          : $"{_config.ServerLogin}1");

        var slotTypes = new Dictionary<int, string>();
        foreach (var s in slots)
        {
            var isBr = _starter?.HostsBattleRoyale(s.Instance)
                       ?? (KnownBuilds.HasBattleRoyale(build.Changelist) && s.Instance > _config.StandardServers);
            slotTypes[s.Instance] = isBr ? "BattleRoyale" : "SaveTheWorld";
        }

        return new ServerHostStatus(
            build,
            endpoint,
            _config.Region,
            pcLogin,
            totalInstances,
            slots,
            sys,
            proc,
            eventsCopy,
            _config.StandardServers,
            _config.BattleRoyaleServers,
            slotTypes,
            _starter?.ServerAssetsProgress);
    }

    public bool HostsBattleRoyale(int instance) =>
        _starter?.HostsBattleRoyale(instance)
        ?? (_build is not null && KnownBuilds.HasBattleRoyale(_build.Changelist) && instance > _config.StandardServers);

    public bool AddServer(bool battleRoyale)
    {
        lock (_gate)
        {
            if (_pool is null || _build is null || _starter is null) return false;

            if (battleRoyale)
            {
                if (!KnownBuilds.HasBattleRoyale(_build.Changelist))
                {
                    AddEvent("[!] Cannot add Battle Royale server: build does not support Battle Royale");
                    return false;
                }
                _config.BattleRoyaleServers++;
            }
            else
            {
                _config.StandardServers++;
            }

            var totalInstances = _config.StandardServers + (KnownBuilds.HasBattleRoyale(_build.Changelist) ? _config.BattleRoyaleServers : 0);
            _starter.SetSlotType(totalInstances, battleRoyale);
            _pool.Configure(totalInstances, TimeSpan.FromSeconds(_config.ReadyTimeoutSeconds));

            var typeName = battleRoyale ? "Battle Royale" : "Save The World";
            AddEvent($"[+] Added {typeName} server (slot #{totalInstances})");

            PersistConfig();
        }

        _pool.Tick();
        Changed?.Invoke();
        return true;
    }

    public bool RemoveServer(int slotInstance)
    {
        lock (_gate)
        {
            if (_pool is null || _build is null || _starter is null) return false;

            var slots = _pool.Slots;
            var targetSlot = slots.FirstOrDefault(s => s.Instance == slotInstance);
            if (targetSlot is null) return false;

            var isBr = _starter.HostsBattleRoyale(slotInstance);
            if (isBr && _config.BattleRoyaleServers > 0)
            {
                _config.BattleRoyaleServers--;
            }
            else if (!isBr && _config.StandardServers > 0)
            {
                _config.StandardServers--;
            }
            else if (_config.StandardServers > 0)
            {
                _config.StandardServers--;
            }
            else if (_config.BattleRoyaleServers > 0)
            {
                _config.BattleRoyaleServers--;
            }

            _starter.RemoveSlotType(slotInstance);

            var typeName = isBr ? "Battle Royale" : "Save The World";
            AddEvent($"[-] Removing {typeName} server (slot #{slotInstance})...");

            _pool.RemoveSlot(slotInstance);

            var totalInstances = _config.StandardServers + (KnownBuilds.HasBattleRoyale(_build.Changelist) ? _config.BattleRoyaleServers : 0);
            _pool.Configure(totalInstances, TimeSpan.FromSeconds(_config.ReadyTimeoutSeconds));

            PersistConfig();
        }

        _pool.Tick();
        Changed?.Invoke();
        return true;
    }

    public void PersistConfig()
    {
        if (string.IsNullOrEmpty(_iniPath) || !File.Exists(_iniPath)) return;
        try
        {
            var ini = SettingsIni.Load(_iniPath);
            _config.SyncToIni(ini);
            ini.Save(_iniPath);
        }
        catch (Exception ex)
        {
            _log.Write($"Warning: Could not save settings.ini: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _log.Write("Shutting down CoreServerHost...");
        if (_pool is not null)
        {
            _pool.SetEnabled(false);
            await _pool.StopAllAsync(TimeSpan.FromSeconds(10));
            _pool.Dispose();
        }

        _sessions.Dispose();
        _job.Dispose();
        _http.Dispose();
    }
}

