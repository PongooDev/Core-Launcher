using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Core.Launcher.Hosting.Native;
using Microsoft.Win32.SafeHandles;

namespace Core.Launcher.Hosting.Processes;

public sealed record GameLaunch(string Executable, string Arguments, IReadOnlyList<string> Dlls, bool StartMinimized);

/// <summary>
/// A game process started the way Core-Docs/Tools/run_server.py does: created suspended, each DLL's LoadLibraryW queued
/// as an APC on the main thread (it runs after loader init, before the exe's CRT and WinMain), then resumed.
/// </summary>
public sealed class GameProcess : IDisposable
{
    private static readonly nint LoadLibraryW = NativeLibrary.GetExport(NativeLibrary.Load("kernel32.dll"), "LoadLibraryW");

    private readonly SafeProcessHandle _process;
    private readonly RegisteredWaitHandle _exitWait;
    private readonly ProcessExitWaitHandle _exitHandle;
    private readonly TaskCompletionSource<uint> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private GameProcess(SafeProcessHandle process, int id)
    {
        _process = process;
        Id = id;
        _exitHandle = new ProcessExitWaitHandle(process);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitHandle, (_, _) => _exited.TrySetResult(ExitCode()), null, Timeout.Infinite, true);
    }

    public int Id { get; }

    public Task<uint> Exited => _exited.Task;

    public static unsafe GameProcess Start(GameLaunch launch, JobObject job)
    {
        var commandLine = CommandLine.Quote(launch.Executable) + (launch.Arguments.Length > 0 ? " " + launch.Arguments : "");
        var startup = new Kernel32.STARTUPINFOW { cb = Marshal.SizeOf<Kernel32.STARTUPINFOW>() };
        if (launch.StartMinimized)
        {
            startup.dwFlags = Kernel32.STARTF_USESHOWWINDOW;
            startup.wShowWindow = Kernel32.SW_SHOWMINNOACTIVE;
        }

        // CreateProcessW may write to the command line buffer, so it gets its own copy.
        var buffer = (commandLine + '\0').ToCharArray();
        Kernel32.PROCESS_INFORMATION information;
        fixed (char* line = buffer)
        {
            if (!Kernel32.CreateProcess(launch.Executable, line, 0, 0, false, Kernel32.CREATE_SUSPENDED, 0,
                    Path.GetDirectoryName(launch.Executable)!, startup, out information))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"CreateProcessW failed for {launch.Executable}");
            }
        }

        var process = new SafeProcessHandle(information.hProcess, true);
        using var thread = new SafeKernelHandle(information.hThread);
        try
        {
            job.Assign(process);
            foreach (var dll in launch.Dlls)
            {
                QueueLoadLibrary(process, thread, dll);
            }

            if (Kernel32.ResumeThread(thread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "ResumeThread failed");
            }
        }
        catch
        {
            Kernel32.TerminateProcess(process, 1);
            process.Dispose();
            throw;
        }

        return new GameProcess(process, (int)information.dwProcessId);
    }

    private static unsafe void QueueLoadLibrary(SafeProcessHandle process, SafeKernelHandle thread, string dll)
    {
        var path = (dll + '\0').ToCharArray();
        var size = (nuint)(path.Length * sizeof(char));
        var remote = Kernel32.VirtualAllocEx(process, 0, size, Kernel32.MEM_COMMIT | Kernel32.MEM_RESERVE, Kernel32.PAGE_READWRITE);
        if (remote == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "VirtualAllocEx failed");
        }

        fixed (char* local = path)
        {
            if (!Kernel32.WriteProcessMemory(process, remote, local, size, out _))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "WriteProcessMemory failed");
            }
        }

        if (Kernel32.QueueUserAPC(LoadLibraryW, thread, remote) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"QueueUserAPC failed for {dll}");
        }
    }

    /// <summary>Waits until every DLL shows up in the process's module list; returns the ones that didn't.</summary>
    public async Task<IReadOnlyList<string>> WaitForModulesAsync(IReadOnlyList<string> dlls, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var missing = dlls.ToList();
        var deadline = DateTime.UtcNow + timeout;
        while (missing.Count > 0 && DateTime.UtcNow < deadline && !Exited.IsCompleted)
        {
            var loaded = LoadedModules();
            missing.RemoveAll(dll => loaded.Contains(Path.GetFullPath(dll)));
            if (missing.Count > 0)
            {
                await Task.Delay(500, cancellation);
            }
        }

        return missing;
    }

    private HashSet<string> LoadedModules()
    {
        var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var process = Process.GetProcessById(Id);
            foreach (ProcessModule module in process.Modules)
            {
                modules.Add(module.FileName);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ArgumentException)
        {
            // The module list can't be read while the loader is still mapping the image; the caller retries.
        }

        return modules;
    }

    public IReadOnlyList<int> UdpPorts() => UdpTable.PortsOwnedBy(Id);

    public void Terminate()
    {
        if (!Exited.IsCompleted)
        {
            Kernel32.TerminateProcess(_process, 1);
        }
    }

    private uint ExitCode() => Kernel32.GetExitCodeProcess(_process, out var code) ? code : uint.MaxValue;

    public void Dispose()
    {
        _exitWait.Unregister(null);
        _exitHandle.Dispose();
        _process.Dispose();
    }

    private sealed class ProcessExitWaitHandle : WaitHandle
    {
        public ProcessExitWaitHandle(SafeProcessHandle process)
        {
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), false);
        }
    }
}
