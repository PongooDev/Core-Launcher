using System.IO;
using System.Windows;
using System.Windows.Threading;
using Core.Launcher.Hosting;

namespace Core.Launcher;

public partial class App : Application
{
    private Mutex? _single;
    private LauncherHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _single = new Mutex(true, @"Local\Core-Launcher", out var first);
        if (!first)
        {
            MessageBox.Show("Core Launcher is already running.", "Core Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        _host = new LauncherHost(DataFolder(e.Args) is { } folder ? new LauncherPaths(Path.GetFullPath(folder)) : LauncherPaths.Default);
        _host.Start();

        var window = new MainWindow(new MainViewModel(_host));
        window.Closed += (_, _) => Shutdown();
        MainWindow = window;
        window.Show();
    }

    /// <summary><c>--data &lt;folder&gt;</c> keeps settings, DLLs and logs in that folder instead of %LOCALAPPDATA%\Core-Launcher.</summary>
    private static string? DataFolder(string[] args)
    {
        var index = Array.FindIndex(args, arg => arg.Equals("--data", StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
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
