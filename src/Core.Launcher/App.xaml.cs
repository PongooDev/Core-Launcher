using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Core.Launcher.Hosting;

namespace Core.Launcher;

public partial class App : Application
{
    private static readonly TimeSpan PreviousExitTimeout = TimeSpan.FromSeconds(30);

    private Mutex? _single;
    private LauncherHost? _host;
    private string[] _args = [];

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _args = e.Args;
        DispatcherUnhandledException += OnUnhandledException;

        _single = new Mutex(true, @"Local\Core-Launcher", out var first);
        if (!first)
        {
            MessageBox.Show("Core Launcher is already running.", "Core Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        await WaitForPreviousAsync();
        var host = new LauncherHost(Option("--data") is { } folder ? new LauncherPaths(Path.GetFullPath(folder)) : LauncherPaths.Default);
        _host = host;

        if (!host.Settings.SetupComplete && new SetupWindow(new SetupViewModel(host)).ShowDialog() != true)
        {
            await host.DisposeAsync();
            Shutdown();
            return;
        }

        host.Start();
        var window = new MainWindow(new MainViewModel(host));
        window.Closed += (_, _) => Shutdown();
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Starts the updated exe and closes this one. The new launcher gets <c>--after &lt;pid&gt;</c> and waits for this
    /// process to exit, so the backend's port and the game processes are free before it starts its own.
    /// </summary>
    public void RestartAfterUpdate(string executable)
    {
        _single?.ReleaseMutex();
        _single?.Dispose();
        _single = null;

        var info = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in WithoutOption(_args, "--after"))
        {
            info.ArgumentList.Add(argument);
        }

        info.ArgumentList.Add("--after");
        info.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(info);
        ((MainWindow)MainWindow).CloseWithoutAsking();
    }

    private async Task WaitForPreviousAsync()
    {
        if (Option("--after") is not { } text || !int.TryParse(text, out var processId))
        {
            return;
        }

        try
        {
            using var previous = Process.GetProcessById(processId);
            using var timeout = new CancellationTokenSource(PreviousExitTimeout);
            await previous.WaitForExitAsync(timeout.Token);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OperationCanceledException)
        {
        }
    }

    /// <summary><c>--data &lt;folder&gt;</c> keeps settings, downloads and logs in that folder instead of %LOCALAPPDATA%\Core-Launcher.</summary>
    private string? Option(string name)
    {
        var index = Array.FindIndex(_args, arg => arg.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < _args.Length ? _args[index + 1] : null;
    }

    private static IEnumerable<string> WithoutOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length; ++index)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                ++index;
                continue;
            }

            yield return args[index];
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _host?.Log.Write($"Unhandled: {e.Exception}");
        MessageBox.Show(e.Exception.Message, "Core Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }
}
