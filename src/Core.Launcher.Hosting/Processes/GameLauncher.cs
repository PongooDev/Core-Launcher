using Core.Launcher.Hosting.Builds;
using Core.Launcher.Hosting.Dlls;
using Core.Launcher.Hosting.Logging;
using Core.Launcher.Hosting.Native;
using Core.Launcher.Hosting.Servers;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Hosting.Processes;

public sealed record GameStart(string Label, BuildSettings Build, IReadOnlyList<DllRole> Dlls, string Arguments, bool Minimized, LogChannel Output);

/// <summary>Starts the build's exe with its Core DLLs and follows the DLLs' logs.</summary>
public sealed class GameLauncher
{
    private readonly JobObject _job;
    private readonly DllStore _dlls;
    private readonly LauncherPaths _paths;
    private readonly LogChannel _log;
    private readonly HashSet<string> _checkedExecutables = new(StringComparer.OrdinalIgnoreCase);

    public GameLauncher(JobObject job, DllStore dlls, LauncherPaths paths, LogChannel log)
    {
        _job = job;
        _dlls = dlls;
        _paths = paths;
        _log = log;
    }

    /// <summary>Throws <see cref="DllException"/> or <see cref="InvalidOperationException"/> with a message for the user when the game can't start.</summary>
    public RunningGame Start(GameStart start)
    {
        var executable = start.Build.ExecutablePath;
        if (PeImage.CheckX64(executable, dll: false) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        if (start.Dlls.Contains(DllRole.Server))
        {
            PatchWhitelist(executable);
        }

        var dlls = start.Dlls.Select(role => (Role: role, Path: _dlls.Resolve(role))).ToList();
        _log.Write($"{start.Label}: starting {executable} {start.Arguments.Replace($"-AUTH_PASSWORD={LaunchArguments.Password}", "-AUTH_PASSWORD=***")}");
        foreach (var dll in dlls)
        {
            _log.Write($"{start.Label}: loading {dll.Path}");
        }

        var process = GameProcess.Start(new GameLaunch(executable, start.Arguments, [.. dlls.Select(dll => dll.Path)], start.Minimized), _job);
        _log.Write($"{start.Label}: pid {process.Id}, {dlls.Count} DLL(s) queued, resumed");
        return new RunningGame(start, process, dlls, _paths, _log);
    }

    /// <summary>Once per exe a session: lets -nullrhi through its command line whitelist (<see cref="ExePatcher"/>).</summary>
    private void PatchWhitelist(string executable)
    {
        if (!_checkedExecutables.Add(executable))
        {
            return;
        }

        try
        {
            if (ExePatcher.PatchWhitelist(executable) == ExePatcher.Result.Patched)
            {
                _log.Write($"Patched {executable}: its command line whitelist now lets -nullrhi, -nosplash, -nosound and -log through");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Core's server makes the same change in memory, so the server still starts.
            _log.Write($"Couldn't patch {executable} ({exception.Message}); Core lets -nullrhi through in memory instead");
        }
    }
}

public sealed class RunningGame : IServerInstance
{
    public const string ReadyLine = "Server ready";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ModuleTimeout = TimeSpan.FromSeconds(15);
    private const int KeptCoreLogs = 100;

    private readonly GameStart _start;
    private readonly List<(DllRole Role, string Path)> _dlls;
    private readonly LauncherPaths _paths;
    private readonly LogChannel _log;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();

    internal RunningGame(GameStart start, GameProcess process, List<(DllRole Role, string Path)> dlls, LauncherPaths paths, LogChannel log)
    {
        _start = start;
        Process = process;
        _dlls = dlls;
        _paths = paths;
        _log = log;
        StartedAt = DateTime.UtcNow;
        Finished = Task.Run(FollowAsync);
        _ = CheckModulesAsync();
    }

    public GameProcess Process { get; }

    public int Id => Process.Id;

    public DateTime StartedAt { get; }

    public Task<uint> Exited => Process.Exited;

    /// <summary>Completes when the server DLL logs "Server ready".</summary>
    public Task Ready => _ready.Task;

    /// <summary>Completes once the process has exited and its logs have been read to the end and moved.</summary>
    public Task Finished { get; }

    public void Terminate() => Process.Terminate();

    public IReadOnlyList<int> UdpPorts() => Process.UdpPorts();

    private async Task FollowAsync()
    {
        var directory = Path.GetDirectoryName(_start.Build.ExecutablePath)!;
        var tails = _dlls.Select(dll =>
        {
            var prefix = _dlls.Count > 1 ? $"[{dll.Role}] " : "";
            var role = dll.Role;
            return new LogTail(Path.Combine(directory, CoreDlls.LogFileName(role, Id)), line => OnLine(role, prefix + line));
        }).ToList();

        var lockedReported = new HashSet<LogTail>();
        var exited = Process.Exited;
        while (!exited.IsCompleted)
        {
            foreach (var tail in tails)
            {
                tail.Poll();
                if (tail.Locked && lockedReported.Add(tail))
                {
                    _start.Output.Write($"(launcher) {Path.GetFileName(tail.FilePath)} can't be read while the game runs: the DLL opened it without shared read. It is shown here after the process exits.");
                }
            }

            await Task.WhenAny(exited, Task.Delay(PollInterval));
        }

        foreach (var tail in tails)
        {
            tail.Poll(final: true);
            if (!tail.Exists)
            {
                _start.Output.Write($"(launcher) {Path.GetFileName(tail.FilePath)} was never written");
            }
        }

        var code = await exited;
        _start.Output.Write($"(launcher) exited with 0x{code:X8}");
        _log.Write($"{_start.Label}: pid {Id} exited with 0x{code:X8} after {DateTime.UtcNow - StartedAt:hh\\:mm\\:ss}");
        _stop.Cancel();
        MoveLogs(tails);
    }

    private void OnLine(DllRole role, string line)
    {
        _start.Output.Write(line);
        if (role == DllRole.Server && !_ready.Task.IsCompleted && line.Contains(ReadyLine, StringComparison.Ordinal))
        {
            _ready.TrySetResult();
        }
    }

    private async Task CheckModulesAsync()
    {
        try
        {
            var missing = await Process.WaitForModulesAsync([.. _dlls.Select(dll => dll.Path)], ModuleTimeout, _stop.Token);
            foreach (var dll in _dlls)
            {
                var loaded = !missing.Contains(dll.Path);
                _log.Write($"{_start.Label}: {Path.GetFileName(dll.Path)} {(loaded ? "is loaded" : Process.Exited.IsCompleted ? "was not seen loaded before the process exited" : "is NOT loaded")}");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void MoveLogs(List<LogTail> tails)
    {
        try
        {
            Directory.CreateDirectory(_paths.CoreLogs);
            foreach (var tail in tails.Where(tail => tail.Exists))
            {
                File.Move(tail.FilePath, Path.Combine(_paths.CoreLogs, Path.GetFileName(tail.FilePath)), true);
            }

            foreach (var old in new DirectoryInfo(_paths.CoreLogs).GetFiles().OrderByDescending(file => file.LastWriteTimeUtc).Skip(KeptCoreLogs))
            {
                old.Delete();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Write($"{_start.Label}: couldn't move the Core logs: {exception.Message}");
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        Process.Dispose();
    }
}
