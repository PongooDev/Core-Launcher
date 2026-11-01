using System.Diagnostics;
using Core.Launcher.Hosting.Builds;
using Core.Server.Cli.Config;
using Core.Server.Cli.Hosting;
using Core.Server.Cli.Interface;
using Core.Server.Cli.Terminal;

namespace Core.Server.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ConsoleEx.Initialise();

        var iniPath = SettingsIni.ResolveIniPath(AppDomain.CurrentDomain.BaseDirectory);
        var createdNewIni = false;

        if (!File.Exists(iniPath))
        {
            var newIni = new SettingsIni();
            newIni.PopulateDefaults();

            // Try to auto-detect any Fortnite build nearby to help the user
            var autoDetected = BuildDetector.Detect(Environment.CurrentDirectory)
                               ?? BuildDetector.Detect(Path.GetDirectoryName(Environment.CurrentDirectory) ?? "");
            if (autoDetected is not null && autoDetected.Version != BuildVersion.Unknown)
            {
                newIni.Set("Server", "BuildPath", autoDetected.Folder);
            }

            newIni.Save(iniPath);
            createdNewIni = true;
        }

        var ini = SettingsIni.Load(iniPath);
        var config = ServerHostConfig.FromIniAndArgs(ini, args);

        // Always update settings.ini with the active configuration (CLI overrides, backfilled settings, etc.)
        config.SyncToIni(ini);
        try
        {
            ini.Save(iniPath);
        }
        catch (IOException)
        {
        }

        if (config.HelpRequested)
        {
            PrintHelp();
            return 0;
        }

        if (config.VersionRequested)
        {
            ConsoleEx.WriteLine("CoreServerHost v0.1.0");
            return 0;
        }

        // Check if build is valid
        var detected = config.BuildPath.Length > 0 ? BuildDetector.Detect(config.BuildPath) : null;
        if (detected is null || detected.Version == BuildVersion.Unknown)
        {
            ShowOnboarding(iniPath, createdNewIni, config.BuildPath);
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        ConsoleEx.EnterAppScreen();
        ConsoleEx.HideCursor();
        try
        {
            Console.Title = $"CoreServerHost · Starting servers ({config.Region})...";
        }
        catch (IOException)
        {
        }

        var runner = new ServerHostRunner(config, iniPath);
        var renderer = new DashboardRenderer();

        try
        {
            await runner.StartAsync(cts.Token);

            var periodicTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            var needsRender = true;

            runner.Changed += () => needsRender = true;

            while (!cts.IsCancellationRequested)
            {
                if (needsRender)
                {
                    needsRender = false;
                    var snapshot = runner.GetSnapshot();
                    renderer.Render(snapshot);

                    var readyCount = snapshot.Slots.Count(s => s.State == Core.Launcher.Hosting.Servers.ServerState.Ready);
                    try
                    {
                        Console.Title = $"CoreServerHost · {readyCount}/{snapshot.TotalConfigured} servers ready ({snapshot.Region})";
                    }
                    catch (IOException)
                    {
                    }
                }

                // Handle keyboard shortcuts
                while (ConsoleEx.KeyAvailable)
                {
                    var key = ConsoleEx.ReadKey();
                    switch (key.KeyChar)
                    {
                        case 'n' or 'N':
                            HandleNewServerPrompt(runner, renderer);
                            needsRender = true;
                            break;

                        case 'd' or 'D':
                            HandleRemoveServerPrompt(runner, renderer);
                            needsRender = true;
                            break;

                        case 'c' or 'C':
                            try
                            {
                                Process.Start(new ProcessStartInfo("notepad.exe", iniPath) { UseShellExecute = true });
                                runner.AddEvent("[i] Opened settings.ini in Notepad");
                            }
                            catch (Exception ex)
                            {
                                runner.AddEvent($"[!] Could not open settings.ini: {ex.Message}");
                            }
                            break;

                        case 'a' or 'A':
                            var adminUrl = $"{runner.Endpoint?.Url ?? config.BackendUrl}/admin";
                            try
                            {
                                Process.Start(new ProcessStartInfo(adminUrl) { UseShellExecute = true });
                                runner.AddEvent($"[i] Opened Admin Panel: {adminUrl}");
                            }
                            catch (Exception ex)
                            {
                                runner.AddEvent($"[!] Could not open browser: {ex.Message}");
                            }
                            break;

                        case 'l' or 'L':
                            try
                            {
                                Process.Start(new ProcessStartInfo("explorer.exe", runner.Paths.Logs) { UseShellExecute = true });
                                runner.AddEvent("[i] Opened logs folder in Explorer");
                            }
                            catch (Exception ex)
                            {
                                runner.AddEvent($"[!] Could not open logs folder: {ex.Message}");
                            }
                            break;
                    }
                }

                try
                {
                    if (await periodicTimer.WaitForNextTickAsync(cts.Token))
                    {
                        needsRender = true;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ConsoleEx.ShowCursor();
            ConsoleEx.LeaveAppScreen();
            ConsoleEx.WriteLine();
            Output.Error($"Fatal error: {ex.Message}");
            return 1;
        }
        finally
        {
            ConsoleEx.ShowCursor();
            ConsoleEx.LeaveAppScreen();
            ConsoleEx.WriteLine();
            Output.Info("Stopping all game servers and shutting down...");
            await runner.DisposeAsync();
            Output.Success("Clean shutdown complete.");
        }

        return 0;
    }

    private static void ShowOnboarding(string iniPath, bool isNew, string currentPath)
    {
        ConsoleEx.Clear();
        Logo.Render();

        ConsoleEx.WriteLine();
        ConsoleEx.WriteLine(Output.Indent + Theme.Emphasis("▌", Theme.White) + " " + Theme.Emphasis("Setup Required", Theme.Text));
        ConsoleEx.WriteLine();

        if (currentPath.Length > 0)
        {
            ConsoleEx.WriteLine(Output.Indent + Theme.Paint($"[!] No valid Fortnite build found at: {currentPath}", Theme.Warning));
        }
        else
        {
            ConsoleEx.WriteLine(Output.Indent + Theme.Paint("[!] No Fortnite build path is configured yet.", Theme.Warning));
        }

        ConsoleEx.WriteLine();
        ConsoleEx.WriteLine(Output.Indent + Theme.Paint("A configuration file is ready at:", Theme.Dim));
        ConsoleEx.WriteLine(Output.Indent + Theme.Emphasis($"→ {iniPath}", Theme.White));
        ConsoleEx.WriteLine();

        ConsoleEx.WriteLine(Output.Indent + Theme.Emphasis("Quick Setup Steps:", Theme.Text));
        ConsoleEx.WriteLine(Output.Indent + Theme.Paint(" 1. Open settings.ini in a text editor.", Theme.Dim));
        ConsoleEx.WriteLine(Output.Indent + Theme.Paint(" 2. Set 'BuildPath' to your Fortnite build folder (must contain Engine & FortniteGame).", Theme.Dim));
        ConsoleEx.WriteLine(Output.Indent + Theme.Paint(" 3. Set 'BackendUrl' if connecting to a remote or custom backend.", Theme.Dim));
        ConsoleEx.WriteLine(Output.Indent + Theme.Paint(" 4. Run CoreServerHost.exe again.", Theme.Dim));
        ConsoleEx.WriteLine();

        ConsoleEx.WriteLine(Output.Indent + Theme.Paint("Press ", Theme.Muted) +
                            Theme.Emphasis("[Enter]", Theme.Text) +
                            Theme.Paint(" to open settings.ini in Notepad now, or any other key to exit...", Theme.Muted));

        var key = ConsoleEx.ReadKey();
        if (key.Key == ConsoleKey.Enter)
        {
            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe", iniPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ConsoleEx.WriteLine(Output.Indent + Theme.Paint($"Could not launch Notepad: {ex.Message}", Theme.Danger));
            }
        }
    }

    private static void HandleNewServerPrompt(ServerHostRunner runner, DashboardRenderer renderer)
    {
        var build = runner.Build;
        var hasBr = build is not null && KnownBuilds.HasBattleRoyale(build.Changelist);

        var options = new List<PromptOption>
        {
            new("1", "Save The World", "Campaign mode", true)
        };

        if (hasBr)
        {
            options.Add(new("2", "Battle Royale", "PvP matchmaking mode", true));
        }
        else
        {
            options.Add(new("2", "Battle Royale", "Unavailable (Build has no BR)", false));
        }

        var selectedIndex = 0;

        while (true)
        {
            var snapshot = runner.GetSnapshot();
            var dialog = new PromptDialog("Create Server", "Select server type to launch:", options, selectedIndex);
            renderer.Render(snapshot, dialog);

            var key = ConsoleEx.ReadKey();
            if (key.Key == ConsoleKey.Escape)
            {
                runner.AddEvent("[i] Server creation cancelled");
                break;
            }

            if (key.Key == ConsoleKey.UpArrow)
            {
                selectedIndex = (selectedIndex - 1 + options.Count) % options.Count;
                continue;
            }

            if (key.Key == ConsoleKey.DownArrow)
            {
                selectedIndex = (selectedIndex + 1) % options.Count;
                continue;
            }

            if (key.KeyChar == '1')
            {
                runner.AddServer(battleRoyale: false);
                break;
            }

            if (key.KeyChar == '2')
            {
                if (hasBr)
                {
                    runner.AddServer(battleRoyale: true);
                }
                else
                {
                    runner.AddEvent("[!] Battle Royale is not supported by this build");
                }
                break;
            }

            if (key.Key == ConsoleKey.Enter)
            {
                var chosen = options[selectedIndex];
                if (!chosen.Enabled)
                {
                    runner.AddEvent("[!] That option is not available for this build");
                    break;
                }

                if (chosen.Key == "1")
                {
                    runner.AddServer(battleRoyale: false);
                }
                else if (chosen.Key == "2")
                {
                    runner.AddServer(battleRoyale: true);
                }
                break;
            }
        }
    }

    private static void HandleRemoveServerPrompt(ServerHostRunner runner, DashboardRenderer renderer)
    {
        var initialSnapshot = runner.GetSnapshot();
        if (initialSnapshot.Slots.Count == 0)
        {
            runner.AddEvent("[!] No servers configured to remove");
            return;
        }

        var selectedIndex = 0;

        while (true)
        {
            var currentSnapshot = runner.GetSnapshot();
            if (currentSnapshot.Slots.Count == 0)
            {
                break;
            }

            var options = new List<PromptOption>();
            for (var i = 0; i < currentSnapshot.Slots.Count; i++)
            {
                var slot = currentSnapshot.Slots[i];
                var isBr = runner.HostsBattleRoyale(slot.Instance);
                var typeName = isBr ? "BattleRoyale" : "SaveTheWorld";
                var portDesc = slot.Ports.Count > 0 ? $"UDP {slot.Ports[0]}" : "";
                var statusDesc = $"{slot.State}" + (portDesc.Length > 0 ? $" · {portDesc}" : "");
                var pidDesc = slot.ProcessId.HasValue ? $"PID {slot.ProcessId}" : "No PID";
                var keyChar = (i + 1) <= 9 ? (i + 1).ToString() : ((char)('a' + (i - 9))).ToString();

                options.Add(new PromptOption(
                    keyChar,
                    $"Slot #{slot.Instance} ({typeName})",
                    $"{statusDesc}  {pidDesc}",
                    true));
            }

            if (selectedIndex >= options.Count)
            {
                selectedIndex = options.Count - 1;
            }

            var dialog = new PromptDialog("Remove Server", "Select a server slot to stop and remove:", options, selectedIndex);
            renderer.Render(currentSnapshot, dialog);

            var key = ConsoleEx.ReadKey();
            if (key.Key == ConsoleKey.Escape)
            {
                runner.AddEvent("[i] Server removal cancelled");
                break;
            }

            if (key.Key == ConsoleKey.UpArrow)
            {
                selectedIndex = (selectedIndex - 1 + options.Count) % options.Count;
                continue;
            }

            if (key.Key == ConsoleKey.DownArrow)
            {
                selectedIndex = (selectedIndex + 1) % options.Count;
                continue;
            }

            // Direct key press matching (e.g. '1'..'9')
            var matched = options.FirstOrDefault(o => o.Key.Equals(key.KeyChar.ToString(), StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                var slotIndex = options.IndexOf(matched);
                if (slotIndex >= 0 && slotIndex < currentSnapshot.Slots.Count)
                {
                    var targetSlot = currentSnapshot.Slots[slotIndex];
                    runner.RemoveServer(targetSlot.Instance);
                    break;
                }
            }

            if (key.Key == ConsoleKey.Enter)
            {
                if (selectedIndex >= 0 && selectedIndex < currentSnapshot.Slots.Count)
                {
                    var targetSlot = currentSnapshot.Slots[selectedIndex];
                    runner.RemoveServer(targetSlot.Instance);
                }
                break;
            }
        }
    }

    private static void PrintHelp()
    {
        Logo.Render();
        ConsoleEx.WriteLine();
        ConsoleEx.WriteLine(Output.Indent + Theme.Emphasis("USAGE:", Theme.Text));
        ConsoleEx.WriteLine(Output.Indent + "  CoreServerHost.exe [options]");
        ConsoleEx.WriteLine();
        ConsoleEx.WriteLine(Output.Indent + Theme.Emphasis("OPTIONS:", Theme.Text));
        ConsoleEx.WriteLine(Output.Indent + "  --build <path>          Path to Fortnite build folder (Engine + FortniteGame)");
        ConsoleEx.WriteLine(Output.Indent + "  --backend <url>         Target Core-Backend address (default: http://127.0.0.1:5595)");
        ConsoleEx.WriteLine(Output.Indent + "  --region <region>       Matchmaking region: NA, EU, OCE, BR, ASIA (default: NA)");
        ConsoleEx.WriteLine(Output.Indent + "  --servers <count>       Standard / Save the World server count (default: 2)");
        ConsoleEx.WriteLine(Output.Indent + "  --br-servers <count>    Battle Royale server count (default: 2)");
        ConsoleEx.WriteLine(Output.Indent + "  --login <pattern>       Server login name pattern (default: CoreServer{n})");
        ConsoleEx.WriteLine(Output.Indent + "  --pc-name <name>        Custom PC name for friend's remote backend");
        ConsoleEx.WriteLine(Output.Indent + "  --ready-timeout <sec>   Seconds before restarting hung server (default: 180)");
        ConsoleEx.WriteLine(Output.Indent + "  --allow-unsupported     Allow unverified Fortnite builds");
        ConsoleEx.WriteLine(Output.Indent + "  --dlls <path>           Custom Core DLLs folder");
        ConsoleEx.WriteLine(Output.Indent + "  --data <path>           Custom data/logs folder (%LOCALAPPDATA%\\Core-Launcher)");
        ConsoleEx.WriteLine(Output.Indent + "  -h, --help              Show help information");
        ConsoleEx.WriteLine(Output.Indent + "  -v, --version           Show version information");
        ConsoleEx.WriteLine();
        ConsoleEx.WriteLine(Output.Indent + Theme.Paint("All options can also be configured permanently in settings.ini.", Theme.Muted));
        ConsoleEx.WriteLine();
    }
}

