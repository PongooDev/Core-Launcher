using System.Diagnostics;
using Core.Launcher.Hosting.Components;
using Core.Server.Cli.Interface;

namespace Core.Server.Cli.Hosting;

public enum UpdateAction
{
    /// <summary>No newer release (or none this exe can use): start the servers.</summary>
    None,

    /// <summary>A newer release exists but auto updates are off, or this run already updated: only log it.</summary>
    LogOnly,

    /// <summary>Download the release, swap the exe and restart.</summary>
    Install,
}

/// <summary>
/// Checks the launcher's latest release before the servers start and, with AutoUpdates on, replaces CoreServerHost.exe by it
/// (<see cref="LauncherUpdater"/>: download beside the exe, size and SHA-256 checked, rename the running exe to .old, rename
/// the new one into place) and restarts with the same arguments. A failure is logged and the servers start anyway.
/// </summary>
public static class SelfUpdate
{
    public const string AssetName = "CoreServerHost.exe";

    /// <summary>Set for the restarted copy so one run never updates twice.</summary>
    public const string UpdatedVariable = "CORE_SERVER_HOST_UPDATED";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    public static UpdateAction Decide(bool updateAvailable, bool autoUpdates, bool alreadyUpdated, bool canReplaceItself) =>
        !updateAvailable ? UpdateAction.None
        : autoUpdates && !alreadyUpdated && canReplaceItself ? UpdateAction.Install
        : UpdateAction.LogOnly;

    /// <summary>
    /// Returns the restarted copy's exit code when the exe was updated (the caller exits with it), or null to carry on
    /// and start the servers.
    /// </summary>
    public static async Task<int?> RunAsync(bool autoUpdates, string[] args, CancellationToken cancellation)
    {
        var executable = LauncherUpdater.PublishedExecutable(typeof(SelfUpdate).Assembly);
        LauncherUpdater.CleanUp(executable);
        var alreadyUpdated = Environment.GetEnvironmentVariable(UpdatedVariable) == "1";

        try
        {
            using var http = GitHubClient.CreateHttpClient();
            var updater = new LauncherUpdater(new GitHubClient(http), LauncherUpdater.RunningVersion, executable, _ => { }, AssetName);

            using var checking = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            checking.CancelAfter(CheckTimeout);
            var release = await updater.CheckAsync(checking.Token);
            var action = Decide(release is not null, autoUpdates, alreadyUpdated, executable is not null);
            if (action == UpdateAction.None)
            {
                return null;
            }

            var running = LauncherUpdater.RunningVersion.ToString(3);
            if (action == UpdateAction.LogOnly)
            {
                var reason = !autoUpdates ? "AutoUpdates is off in settings.ini"
                    : alreadyUpdated ? "already updated this run"
                    : "this isn't a published CoreServerHost.exe";
                Output.Info($"CoreServerHost {release!.Tag} is available (running {running}); {reason}, so it isn't installed.");
                return null;
            }

            Output.Step($"Updating CoreServerHost {running} to {release!.Tag}...");
            var progress = new Progress<string>(text => Output.Detail(text));
            await updater.InstallAsync(release, progress, cancellation);
            Output.Success($"CoreServerHost {release.Tag} installed; restarting.");
            return await RestartAsync(executable!, args, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Output.Warn($"Couldn't check for or install an update ({exception.Message}); starting the servers anyway.");
            return null;
        }
    }

    /// <summary>Starts the new exe with the same arguments in this console and waits for it, so the shell prompt and Ctrl+C behave as before.</summary>
    private static async Task<int> RestartAsync(string executable, string[] args, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment[UpdatedVariable] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The updated CoreServerHost.exe didn't start");
        await process.WaitForExitAsync(cancellation);
        return process.ExitCode;
    }
}
