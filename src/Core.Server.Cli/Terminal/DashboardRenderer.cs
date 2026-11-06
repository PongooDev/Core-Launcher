using System.Text;
using System.Text.RegularExpressions;
using Core.Launcher.Hosting.Builds;
using Core.Launcher.Hosting.Components;
using Core.Launcher.Hosting.Servers;
using Core.Server.Cli.Hosting;
using Core.Server.Cli.Interface;

namespace Core.Server.Cli.Terminal;

public sealed record PromptOption(string Key, string Label, string? Description = null, bool Enabled = true);

public sealed record PromptDialog(
    string Title,
    string Prompt,
    IReadOnlyList<PromptOption> Options,
    int SelectedIndex = 0);

public sealed partial class DashboardRenderer
{
    public void Render(ServerHostStatus status, PromptDialog? dialog = null)
    {
        var frame = BuildFrame(status, dialog);
        ConsoleEx.Screen(frame);
    }

    public static List<string> BuildFrame(ServerHostStatus status, PromptDialog? dialog = null)
    {
        var lines = new List<string>(Logo.Lines());

        // 1. Endpoints Section
        lines.Add(string.Empty);
        lines.Add(Output.Indent + Theme.Emphasis("▌", Theme.White) + " " + Theme.Emphasis("Endpoints", Theme.Text));

        var isLocal = status.Endpoint.Host == "127.0.0.1" || status.Endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        var adminUrl = $"{status.Endpoint.Url}/admin";
        var clickableAdmin = Hyperlink.Format(adminUrl);
        var adminDesc = isLocal ? "local dashboard" : "remote dashboard";

        var buildName = status.Build.Name;
        var cl = status.Build.Changelist.ToString();
        var buildLabel = buildName.Contains(cl, StringComparison.OrdinalIgnoreCase)
            ? $"{buildName} ({status.Build.ExecutablePath})"
            : $"{buildName} · CL {cl} ({status.Build.ExecutablePath})";

        var panel = new Panel()
            .Row("Endpoint", status.Endpoint.Url)
            .Row("Admin Panel", $"{clickableAdmin}  ({adminDesc})")
            .Row("Build", buildLabel)
            .Row("Region", $"{status.Region}  ·  Login: {status.PcLogin}");

        lines.AddRange(panel.BuildLines());

        // 2. Resources Section
        lines.Add(string.Empty);
        lines.Add(Output.Indent + Theme.Emphasis("▌", Theme.White) + " " + Theme.Emphasis("Resources", Theme.Text));

        var sys = status.SystemMetrics;
        var proc = status.ProcessMetrics;

        var sysRamText = $"{Format.Bytes(sys.UsedRamBytes)} / {Format.Bytes(sys.TotalRamBytes)} ({sys.RamPercent:0}%)";
        var procRamText = $"{Format.Bytes(proc.TotalWorkingSetBytes)} across {proc.ByProcessId.Count} active processes";

        var resourcePanel = new Panel()
            .Row("System", $"CPU: {sys.CpuPercent,5:0.0}%   RAM: {sysRamText}")
            .Row("Servers Total", $"CPU: {proc.TotalCpuPercent,5:0.0}%   RAM: {procRamText}");

        lines.AddRange(resourcePanel.BuildLines());

        // 3. Servers Section
        lines.Add(string.Empty);
        var readyCount = status.Slots.Count(s => s.State == ServerState.Ready);
        lines.Add(Output.Indent + Theme.Emphasis("▌", Theme.White) + " " +
                  Theme.Emphasis("Servers", Theme.Text) + "  " +
                  Theme.Paint($"{readyCount} ready of {status.TotalConfigured} configured", Theme.Muted));

        var table = new Table()
            .Add("Slot", Table.Alignment.Left, minWidth: 4)
            .Add("Type", Table.Alignment.Left, minWidth: 12)
            .Add("Status", Table.Alignment.Left, minWidth: 13)
            .Add("Port", Table.Alignment.Left, minWidth: 8)
            .Add("RAM", Table.Alignment.Right, minWidth: 9)
            .Add("CPU", Table.Alignment.Right, minWidth: 5)
            .Add("PID", Table.Alignment.Right, minWidth: 5)
            .Add("Up", Table.Alignment.Right, minWidth: 5);

        foreach (var slot in status.Slots)
        {
            var isBr = status.SlotTypes is not null && status.SlotTypes.TryGetValue(slot.Instance, out var t)
                ? t.Equals("BattleRoyale", StringComparison.OrdinalIgnoreCase)
                : (KnownBuilds.HasBattleRoyale(status.Build.Changelist) && slot.Instance > status.StandardServers);
            var typeName = isBr ? "BattleRoyale" : "SaveTheWorld";

            var (badge, statusColor) = slot.State switch
            {
                ServerState.Ready => ("● Ready", Theme.Success),
                ServerState.Registering => ("◐ Registering", Theme.Warning),
                ServerState.Starting => ("▶ Starting", Theme.LightGray),
                ServerState.Stopping => ("■ Stopping", Theme.Muted),
                _ => ("○ Waiting", Theme.Dim),
            };

            var port = slot.Ports.Count > 0 ? $"UDP {slot.Ports[0]}" : Theme.Paint("-", Theme.Dim);
            var pid = slot.ProcessId?.ToString() ?? Theme.Paint("-", Theme.Dim);
            var uptime = slot.Since.HasValue ? Format.Duration(DateTimeOffset.UtcNow - slot.Since.Value) : Theme.Paint("-", Theme.Dim);

            var ramText = Theme.Paint("-", Theme.Dim);
            var cpuText = Theme.Paint("-", Theme.Dim);
            if (slot.ProcessId.HasValue && proc.ByProcessId.TryGetValue(slot.ProcessId.Value, out var snap))
            {
                ramText = Format.Bytes(snap.WorkingSetBytes);
                cpuText = $"{snap.CpuPercent:0.0}%";
            }

            table.Row(
                $"#{slot.Instance}",
                typeName,
                Theme.Paint(badge, statusColor),
                port,
                ramText,
                cpuText,
                pid,
                uptime);
        }

        if (status.Slots.Count == 0)
        {
            table.Row(
                "#1",
                "Server",
                Theme.Paint("○ Waiting", Theme.Dim),
                Theme.Paint("-", Theme.Dim),
                Theme.Paint("-", Theme.Dim),
                Theme.Paint("-", Theme.Dim),
                Theme.Paint("-", Theme.Dim),
                Theme.Paint("-", Theme.Dim));
        }

        lines.AddRange(table.BuildLines());

        // Server assets: the pak's download, while it runs or after it failed (the servers start without it then).
        if (status.ServerAssets is { Visible: true } assets)
        {
            lines.Add(string.Empty);
            lines.Add(Output.Indent + Theme.Emphasis("▌", Theme.White) + " " + Theme.Emphasis("Server Assets", Theme.Text));
            var failed = assets.Phase == ServerAssetsPhase.Failed;
            lines.AddRange(new Panel().Row(failed ? "Failed" : "Status", Theme.Paint(failed ? $"{assets.Describe()} (servers start without it; restart CoreServerHost to retry)" : assets.Describe(), failed ? Theme.Danger : Theme.Text)).BuildLines());
        }

        // 4. Activity Section (budget-aware to prevent exceeding window height)
        const int reservedForBottom = 4; // blank + status bar + blank + footer
        var availableForActivity = ConsoleEx.Height - lines.Count - reservedForBottom;
        if (status.RecentEvents.Count > 0 && (availableForActivity >= 3 || !ConsoleEx.IsInteractive))
        {
            var maxEvents = Math.Clamp(availableForActivity - 2, 1, 4);
            var eventsToShow = status.RecentEvents.TakeLast(maxEvents);

            lines.Add(string.Empty);
            lines.Add(Output.Indent + Theme.Emphasis("▌", Theme.White) + " " + Theme.Emphasis("Recent Activity", Theme.Text));
            var prefix = Output.Indent + Theme.Paint("│", Theme.Line) + "  ";
            foreach (var evt in eventsToShow)
            {
                lines.Add(prefix + FormatActivityEvent(evt));
            }
        }

        // 5. Active Status Highlight Bar
        lines.Add(string.Empty);
        var activeLine = new StyledLine()
            .Add(" ❯ ● ", Theme.Success, bold: true)
            .Add("CoreServerHost  ", Theme.Text, bold: true)
            .Add($"{readyCount}/{status.TotalConfigured} servers ready  ·  {status.Region}  ", Theme.Dim);

        activeLine.Pad(Math.Max(50, ConsoleEx.Width - Output.Indent.Length - 4));
        lines.Add(Output.Indent + activeLine.Build(Theme.Highlight));

        // 6. Footer or Dialog Section
        if (dialog is not null)
        {
            lines.Add(string.Empty);
            var maxKey = dialog.Options.Count > 0 ? dialog.Options.Count.ToString() : "1";
            lines.Add(Output.Indent + Theme.Emphasis("▌", Theme.White) + " " +
                      Theme.Emphasis(dialog.Title, Theme.Text) + "  " +
                      Theme.Paint($"(Use 1-{maxKey}, ↑↓ + Enter, or Esc to cancel)", Theme.Muted));

            for (var i = 0; i < dialog.Options.Count; i++)
            {
                var opt = dialog.Options[i];
                var isSelected = i == dialog.SelectedIndex;
                var prefix = Output.Indent + Theme.Paint("│", Theme.Line) + "  ";
                var keyBadge = $"[{opt.Key}]";
                var desc = opt.Description is not null ? "  " + Theme.Paint(opt.Description, isSelected ? Theme.OffWhite : Theme.Muted) : "";

                if (isSelected)
                {
                    lines.Add(prefix + Theme.Emphasis($"❯ {keyBadge} {opt.Label}", Theme.White) + desc);
                }
                else
                {
                    lines.Add(prefix + Theme.Paint($"  {keyBadge} {opt.Label}", opt.Enabled ? Theme.Dim : Theme.Muted) + desc);
                }
            }
        }
        else
        {
            lines.Add(string.Empty);
            lines.Add(Output.Indent + Hints.FormattedFooter());
        }

        return lines;
    }

    private static string FormatActivityEvent(string evt)
    {
        if (string.IsNullOrWhiteSpace(evt)) return evt;

        var match = ActivityRegex().Match(evt);
        if (!match.Success)
        {
            return Theme.Paint(evt, Theme.Text);
        }

        var timeGroup = match.Groups["time"];
        var badgeGroup = match.Groups["badge"];
        var msgGroup = match.Groups["msg"];

        var sb = new StringBuilder();
        if (timeGroup.Success && timeGroup.Length > 0)
        {
            sb.Append(Theme.Paint(timeGroup.Value, Theme.Muted));
            sb.Append("  ");
        }

        if (badgeGroup.Success && badgeGroup.Length > 0)
        {
            var badge = badgeGroup.Value;
            var badgeColor = badge switch
            {
                "[+]" => Theme.Success,
                "[*]" => Theme.LightGray,
                "[!]" => Theme.Warning,
                "[-]" => Theme.Danger,
                _ => Theme.LightGray
            };
            sb.Append(Theme.Emphasis(badge, badgeColor));
            sb.Append(' ');
        }

        if (msgGroup.Success && msgGroup.Length > 0)
        {
            sb.Append(Theme.Paint(msgGroup.Value, Theme.Text));
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"^(?:(?<time>\d{2}:\d{2}:\d{2})\s+)?(?:(?<badge>\[[\+\*\!\-]\])\s+)?(?<msg>.*)$")]
    private static partial Regex ActivityRegex();
}

