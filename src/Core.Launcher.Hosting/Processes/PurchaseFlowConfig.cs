using System.Text;
using Core.Launcher.Hosting.Backend;

namespace Core.Launcher.Hosting.Processes;

/// <summary>
/// The Store's purchase page, in the player's saved Game.ini (Core-Backend #244). The purchase flow reads its URL once,
/// from the game config's [EpicPurchaseFlow] section under the MCP environment's name ("Prod"): the front end's root
/// UIManager does on its first frame (3.1 RegisterForEpicPurchasingDisplay, 0xD496C0), before the backend's DefaultGame.ini
/// hotfix applies, so a V-Bucks pack or the Starter Pack opened Epic's shipped launcher-website page, which is black.
/// The saved Game.ini is read over the build's own config at start-up. Clients only: servers show no Store.
/// </summary>
public static class PurchaseFlowConfig
{
    public const string Section = "EpicPurchaseFlow";

    public const string Key = "Prod";

    /// <summary>The player's saved Game.ini (FPaths::GeneratedConfigDir under Saved).</summary>
    public static string GameIniPath(LauncherPaths paths) => Path.Combine(paths.GameUserDir, "Saved", "Config", "WindowsClient", "Game.ini");

    /// <summary>
    /// As Core-Backend's purchaseFlowUrl (src/services/fortnite/purchase.js): http:///host:port/purchase. The triple slash
    /// is deliberate: the flow takes the page's cookie domain by stripping "http://" or "https://" and cutting at the next
    /// "/", and an empty domain makes the cookie the page's own host; CEF reads the URL as http://host:port/purchase.
    /// </summary>
    public static string Url(BackendEndpoint backend) => $"{backend.Scheme}:///{backend.Url[(backend.Scheme.Length + "://".Length)..]}/purchase";

    /// <summary>
    /// Sets [EpicPurchaseFlow] Prod in <paramref name="path"/>, keeping its other sections and keys, its encoding and line
    /// endings; creates it when it's missing. Returns whether it wrote the file. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> when it can't.
    /// </summary>
    public static bool Write(string path, string url)
    {
        string existing = "";
        Encoding encoding = new UTF8Encoding(false);
        if (File.Exists(path))
        {
            using var reader = new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true);
            existing = reader.ReadToEnd();
            encoding = reader.CurrentEncoding;
        }

        var merged = Merge(existing, url);
        if (merged == existing && File.Exists(path))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, merged, encoding);
        return true;
    }

    /// <summary>Replaces the Prod line under [EpicPurchaseFlow] (dropping any repeats of it), or adds it, or adds the section.</summary>
    public static string Merge(string existing, string url)
    {
        var newline = existing.Contains("\r\n") || !existing.Contains('\n') ? "\r\n" : "\n";
        var lines = existing.Length == 0 ? [] : existing.Replace("\r\n", "\n").Split('\n').ToList();
        var endsWithNewline = existing.EndsWith('\n');
        if (endsWithNewline)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var entry = $"{Key}={url}";
        var start = lines.FindIndex(line => line.Trim().Equals($"[{Section}]", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
            {
                lines.Add("");
            }

            lines.Add($"[{Section}]");
            lines.Add(entry);
        }
        else
        {
            var end = lines.FindIndex(start + 1, line => line.TrimStart().StartsWith('['));
            end = end < 0 ? lines.Count : end;
            var keys = Enumerable.Range(start + 1, end - start - 1).Where(index => IsKey(lines[index])).ToList();
            if (keys.Count > 0)
            {
                lines[keys[0]] = entry;
                foreach (var index in keys.Skip(1).Reverse())
                {
                    lines.RemoveAt(index);
                }
            }
            else
            {
                var last = end - 1;
                while (last > start && lines[last].Trim().Length == 0)
                {
                    last--;
                }

                lines.Insert(last + 1, entry);
            }
        }

        return string.Join(newline, lines) + (endsWithNewline || start < 0 || existing.Length == 0 ? newline : "");
    }

    private static bool IsKey(string line)
    {
        var equals = line.IndexOf('=');
        return equals > 0 && line[..equals].Trim().Equals(Key, StringComparison.OrdinalIgnoreCase);
    }
}
