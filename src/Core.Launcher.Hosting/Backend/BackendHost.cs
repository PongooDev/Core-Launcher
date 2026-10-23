using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Core.Launcher.Hosting.Logging;
using Core.Launcher.Hosting.Native;

namespace Core.Launcher.Hosting.Backend;

/// <summary>
/// What the backend host runs. With <see cref="Start"/> false it only watches a backend already running on <see cref="Port"/>:
/// this PC's, or with <see cref="Remote"/> a friend's at <see cref="Host"/>.
/// </summary>
public sealed record BackendTarget(bool Start, string Node, string Folder, string DataFolder, int Port, string? Problem = null)
{
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>A friend's backend: checked with GET /core/api/status, and it has no admin panel for this PC.</summary>
    public bool Remote { get; init; }

    /// <summary>The friend's name for it, for messages.</summary>
    public string RemoteName { get; init; } = "";

    public BackendEndpoint Endpoint => new(Host, Port);

    public static BackendTarget Running(int port) => new(false, "", "", "", port);

    public static BackendTarget Friend(BackendEndpoint endpoint, string name) =>
        new(false, "", "", "", endpoint.Port) { Host = endpoint.Host, Remote = true, RemoteName = name };

    public static BackendTarget FriendProblem(string problem, string name) => new(false, "", "", "", 0, problem) { Remote = true, RemoteName = name };
}

public enum BackendState
{
    Stopped,
    Starting,
    Ready,
    Restarting,
    Unreachable,
    Failed,
}

/// <summary>
/// Runs Core-Backend as its README's "From a launcher" describes: <c>node src/main.js --port &lt;n&gt; --data &lt;folder&gt;</c>,
/// ready on "Listening on port &lt;n&gt;", exit 1 = port taken, exit 2 = bad options. Or watches one that is already running.
/// </summary>
public sealed partial class BackendHost : IAsyncDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RemoteProbeInterval = TimeSpan.FromSeconds(5);

    private readonly JobObject _job;
    private readonly LogChannel _output;
    private readonly LogChannel _log;
    private readonly Lock _gate = new();
    private readonly HttpClient _http;
    private BackendTarget _target = BackendTarget.Running(5595);
    private CancellationTokenSource? _stop;
    private Task _run = Task.CompletedTask;

    /// <param name="http">Checks a friend's backend; null makes one (tests pass their own handler).</param>
    public BackendHost(JobObject job, LogChannel output, LogChannel log, HttpClient? http = null)
    {
        _job = job;
        _output = output;
        _log = log;
        _http = http ?? RemoteBackendCheck.CreateHttpClient();
    }

    public BackendState State { get; private set; } = BackendState.Stopped;

    public int? Port { get; private set; }

    /// <summary>The host the backend in use answers on: 127.0.0.1, or a friend's.</summary>
    public string Host { get; private set; } = "127.0.0.1";

    /// <summary>Where clients reach the backend in use, while it is ready.</summary>
    public BackendEndpoint? Endpoint => State == BackendState.Ready && Port is { } port ? new BackendEndpoint(Host, port) : null;

    /// <summary>The friend's backend's last check, while one is in use.</summary>
    public RemoteBackendStatus? RemoteStatus { get; private set; }

    public string? Error { get; private set; }

    public int? ProcessId { get; private set; }

    /// <summary>Core-Backend's admin panel (loopback only), served by the backend itself.</summary>
    public const string AdminPath = "/admin";

    /// <summary>The admin panel of the backend in use (started here or already running), or null while it isn't reachable.</summary>
    public string? AdminPanelUrl => State == BackendState.Ready && Port is { } port && !Target.Remote ? $"http://127.0.0.1:{port}{AdminPath}" : null;

    public bool Running => !_run.IsCompleted;

    public event Action? Changed;

    public BackendTarget Target
    {
        get
        {
            lock (_gate)
            {
                return _target;
            }
        }
    }

    public void Configure(BackendTarget target)
    {
        lock (_gate)
        {
            _target = target;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (!_run.IsCompleted)
            {
                return;
            }

            _stop = new CancellationTokenSource();
            var target = _target;
            var token = _stop.Token;
            _run = Task.Run(() => target.Start ? SuperviseAsync(target, token) : WatchAsync(target, token));
        }
    }

    public async Task StopAsync()
    {
        Task run;
        lock (_gate)
        {
            _stop?.Cancel();
            run = _run;
        }

        await run;
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        Start();
    }

    public async Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken cancellation = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (State != BackendState.Ready)
        {
            if (DateTime.UtcNow >= deadline || State == BackendState.Failed || !Running)
            {
                return false;
            }

            await Task.Delay(200, cancellation);
        }

        return true;
    }

    private async Task SuperviseAsync(BackendTarget target, CancellationToken stop)
    {
        var failures = 0;
        while (!stop.IsCancellationRequested)
        {
            if (Validate(target) is { } problem)
            {
                Fail(problem);
                return;
            }

            var exit = await RunOnceAsync(target, stop);
            if (exit is null)
            {
                return;
            }

            var (code, readyFor) = exit.Value;
            switch (code)
            {
                case 1:
                    Fail($"Port {target.Port} is already in use. If Core-Backend is already running, choose \"Already running\" in Settings → Backend.");
                    return;
                case 2:
                    Fail("Core-Backend refused its options (exit code 2); see the Backend log");
                    return;
            }

            failures = readyFor >= StableAfter ? 1 : failures + 1;
            var delay = Backoff.Delay(failures);
            _log.Write($"Backend exited with code {code}; restarting in {delay.TotalSeconds:0} s");
            Set(BackendState.Restarting, null);
            try
            {
                await Task.Delay(delay, stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Set(BackendState.Stopped, null);
    }

    /// <summary>Runs the backend until it exits (its exit code and how long it was ready) or until stopped (null).</summary>
    private async Task<(int Code, TimeSpan ReadyFor)?> RunOnceAsync(BackendTarget target, CancellationToken stop)
    {
        var info = new ProcessStartInfo(target.Node)
        {
            WorkingDirectory = target.Folder,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            CreateNewProcessGroup = true,
        };
        info.ArgumentList.Add(Path.Combine("src", "main.js"));
        info.ArgumentList.Add("--port");
        info.ArgumentList.Add(target.Port.ToString());
        info.ArgumentList.Add("--data");
        info.ArgumentList.Add(target.DataFolder);

        Port = null;
        Host = "127.0.0.1";
        Set(BackendState.Starting, null);
        _log.Write($"Backend: starting {target.Node} {string.Join(' ', info.ArgumentList.Select(Processes.CommandLine.Quote))} in {target.Folder}");

        using var process = new Process { StartInfo = info };
        DateTime? readyAt = null;
        process.OutputDataReceived += (_, line) =>
        {
            if (line.Data is null)
            {
                return;
            }

            _output.Write(line.Data);
            if (readyAt is null && ListeningLine().Match(line.Data) is { Success: true } match)
            {
                readyAt = DateTime.UtcNow;
                Port = int.Parse(match.Groups[1].Value);
                _log.Write($"Backend: listening on port {Port}");
                Set(BackendState.Ready, null);
            }
        };
        process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is not null)
            {
                _output.Write(line.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            Fail($"Couldn't start {target.Node}: {exception.Message}. Install Node.js 22 or newer, or set its path in Settings.");
            return null;
        }

        _job.Assign(process.SafeHandle);
        ProcessId = process.Id;
        _log.Write($"Backend: pid {process.Id}");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(stop);
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(process);
            ProcessId = null;
            Port = null;
            Set(BackendState.Stopped, null);
            return null;
        }

        process.WaitForExit();
        ProcessId = null;
        Port = null;
        return (process.ExitCode, readyAt is { } ready ? DateTime.UtcNow - ready : TimeSpan.Zero);
    }

    private async Task StopProcessAsync(Process process)
    {
        if (ConsoleControl.SendCtrlBreak(process.Id))
        {
            using var timeout = new CancellationTokenSource(StopTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                _log.Write($"Backend: stopped (exit code {process.ExitCode})");
                return;
            }
            catch (OperationCanceledException)
            {
            }
        }

        try
        {
            process.Kill(true);
        }
        catch (InvalidOperationException)
        {
        }

        await process.WaitForExitAsync();
        _log.Write("Backend: killed");
    }

    private async Task WatchAsync(BackendTarget target, CancellationToken stop)
    {
        if (target.Remote)
        {
            await WatchFriendAsync(target, stop);
            return;
        }

        Host = "127.0.0.1";
        Port = target.Port;
        _log.Write($"Backend: using the one already running on port {target.Port}");
        while (!stop.IsCancellationRequested)
        {
            var reachable = await AcceptsConnectionsAsync(target.Port, stop);
            Set(reachable ? BackendState.Ready : BackendState.Unreachable, reachable ? null : $"Nothing is listening on port {target.Port}");
            try
            {
                await Task.Delay(ProbeInterval, stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Port = null;
        Set(BackendState.Stopped, null);
    }

    /// <summary>Checks a friend's backend now and then, so Home can say whether it is up before the game starts.</summary>
    private async Task WatchFriendAsync(BackendTarget target, CancellationToken stop)
    {
        RemoteStatus = null;
        if (target.Problem is { } problem)
        {
            Fail(problem);
            return;
        }

        Host = target.Host;
        Port = target.Port;
        _log.Write($"Backend: using {(target.RemoteName.Length > 0 ? target.RemoteName + "'s" : "a friend's")} backend at {target.Endpoint}");
        Set(BackendState.Starting, null);
        string? reported = null;
        while (!stop.IsCancellationRequested)
        {
            RemoteBackendStatus status;
            try
            {
                status = await RemoteBackendCheck.CheckAsync(_http, target.Endpoint, stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            RemoteStatus = status;
            if (status.Message != reported)
            {
                reported = status.Message;
                _log.Write($"Backend: {status.Message}");
            }

            Set(status.Reachable ? BackendState.Ready : BackendState.Unreachable, status.Reachable ? null : status.Message);
            try
            {
                await Task.Delay(RemoteProbeInterval, stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Port = null;
        RemoteStatus = null;
        Set(BackendState.Stopped, null);
    }

    private static async Task<bool> AcceptsConnectionsAsync(int port, CancellationToken stop)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await client.ConnectAsync("127.0.0.1", port, timeout.Token);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    public static string? Validate(BackendTarget target)
    {
        if (target.Problem is not null)
        {
            return target.Problem;
        }

        if (target.Folder.Length == 0)
        {
            return "The Core-Backend folder isn't set (Settings → Backend)";
        }

        if (!File.Exists(Path.Combine(target.Folder, "src", "main.js")))
        {
            return $"{target.Folder} isn't a Core-Backend checkout (no src\\main.js)";
        }

        if (!Directory.Exists(Path.Combine(target.Folder, "node_modules")))
        {
            return $"Core-Backend's dependencies aren't installed: run npm install in {target.Folder}";
        }

        return null;
    }

    private void Fail(string message)
    {
        _log.Write($"Backend: {message}");
        Set(BackendState.Failed, message);
    }

    private void Set(BackendState state, string? error)
    {
        if (State == state && Error == error)
        {
            return;
        }

        State = state;
        Error = error;
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
    }

    [GeneratedRegex(@"Listening on port (\d+)")]
    private static partial Regex ListeningLine();
}
