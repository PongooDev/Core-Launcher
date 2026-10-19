using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Core.Launcher.Hosting.Logging;
using Core.Launcher.Hosting.Native;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Hosting.Backend;

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
/// ready on "Listening on port &lt;n&gt;", exit 1 = port taken, exit 2 = bad options. Or, with Start off, watches one that is already running.
/// </summary>
public sealed partial class BackendHost : IAsyncDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(60);

    private readonly JobObject _job;
    private readonly LogChannel _output;
    private readonly LogChannel _log;
    private readonly Lock _gate = new();
    private BackendSettings _settings = new();
    private CancellationTokenSource? _stop;
    private Task _run = Task.CompletedTask;

    public BackendHost(JobObject job, LogChannel output, LogChannel log)
    {
        _job = job;
        _output = output;
        _log = log;
    }

    public BackendState State { get; private set; } = BackendState.Stopped;

    public int? Port { get; private set; }

    public string? Error { get; private set; }

    public int? ProcessId { get; private set; }

    public bool Running => !_run.IsCompleted;

    public event Action? Changed;

    public void Configure(BackendSettings settings)
    {
        lock (_gate)
        {
            _settings = settings;
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
            var settings = _settings;
            var token = _stop.Token;
            _run = Task.Run(() => settings.Start ? SuperviseAsync(settings, token) : WatchAsync(settings, token));
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

    private async Task SuperviseAsync(BackendSettings settings, CancellationToken stop)
    {
        var failures = 0;
        while (!stop.IsCancellationRequested)
        {
            if (Validate(settings) is { } problem)
            {
                Fail(problem);
                return;
            }

            var exit = await RunOnceAsync(settings, stop);
            if (exit is null)
            {
                return;
            }

            var (code, readyFor) = exit.Value;
            switch (code)
            {
                case 1:
                    Fail($"Port {settings.Port} is already in use. If Core-Backend is already running, turn off \"Start Core-Backend\" in Settings.");
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
    private async Task<(int Code, TimeSpan ReadyFor)?> RunOnceAsync(BackendSettings settings, CancellationToken stop)
    {
        var info = new ProcessStartInfo(settings.Node)
        {
            WorkingDirectory = settings.Folder,
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
        info.ArgumentList.Add(settings.Port.ToString());
        info.ArgumentList.Add("--data");
        info.ArgumentList.Add(settings.ResolvedDataFolder);

        Port = null;
        Set(BackendState.Starting, null);
        _log.Write($"Backend: starting {settings.Node} {string.Join(' ', info.ArgumentList.Select(Processes.CommandLine.Quote))} in {settings.Folder}");

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
            Fail($"Couldn't start {settings.Node}: {exception.Message}. Install Node.js 22 or newer, or set its path in Settings.");
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

    private async Task WatchAsync(BackendSettings settings, CancellationToken stop)
    {
        Port = settings.Port;
        _log.Write($"Backend: using the one already running on port {settings.Port}");
        while (!stop.IsCancellationRequested)
        {
            var reachable = await AcceptsConnectionsAsync(settings.Port, stop);
            Set(reachable ? BackendState.Ready : BackendState.Unreachable, reachable ? null : $"Nothing is listening on port {settings.Port}");
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

    public static string? Validate(BackendSettings settings)
    {
        if (settings.Folder.Length == 0)
        {
            return "The Core-Backend folder isn't set (Settings → Backend)";
        }

        if (!File.Exists(Path.Combine(settings.Folder, "src", "main.js")))
        {
            return $"{settings.Folder} isn't a Core-Backend checkout (no src\\main.js)";
        }

        if (!Directory.Exists(Path.Combine(settings.Folder, "node_modules")))
        {
            return $"Core-Backend's dependencies aren't installed: run npm install in {settings.Folder}";
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

    public async ValueTask DisposeAsync() => await StopAsync();

    [GeneratedRegex(@"Listening on port (\d+)")]
    private static partial Regex ListeningLine();
}
