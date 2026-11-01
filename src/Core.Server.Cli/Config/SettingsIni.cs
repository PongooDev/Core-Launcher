using System.Text;

namespace Core.Server.Cli.Config;

/// <summary>
/// Lightweight, zero-dependency parser, serializer, and updater for settings.ini.
/// Automatically backfills missing settings and keeps comments intact.
/// </summary>
public class SettingsIni
{
    public const string DefaultFileName = "settings.ini";
    public const string LegacyFileName = "setup.ini";

    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string section, string key) =>
        _sections.TryGetValue(section, out var keys) && keys.TryGetValue(key, out var value) ? value : null;

    public void Set(string section, string key, string value)
    {
        if (!_sections.TryGetValue(section, out var keys))
        {
            keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _sections[section] = keys;
        }

        keys[key] = value;
    }

    /// <summary>
    /// Resolves the INI path in the given directory. If settings.ini doesn't exist but legacy setup.ini exists,
    /// it automatically migrates setup.ini -> settings.ini.
    /// </summary>
    public static string ResolveIniPath(string baseDirectory)
    {
        var settingsPath = Path.Combine(baseDirectory, DefaultFileName);
        var legacyPath = Path.Combine(baseDirectory, LegacyFileName);

        if (!File.Exists(settingsPath) && File.Exists(legacyPath))
        {
            try
            {
                File.Move(legacyPath, settingsPath);
            }
            catch (IOException)
            {
                try { File.Copy(legacyPath, settingsPath, true); } catch { }
            }
        }

        return settingsPath;
    }

    public static SettingsIni Parse(string text)
    {
        var ini = new SettingsIni();
        var currentSection = "";

        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                currentSection = trimmed[1..^1].Trim();
                continue;
            }

            var equals = trimmed.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            var key = trimmed[..equals].Trim();
            var value = trimmed[(equals + 1)..].Trim();
            ini.Set(currentSection, key, value);
        }

        return ini;
    }

    public static SettingsIni Load(string path)
    {
        if (!File.Exists(path))
        {
            var ini = new SettingsIni();
            ini.PopulateDefaults();
            return ini;
        }

        var loaded = Parse(File.ReadAllText(path));
        // Backfill any missing keys from new updates:
        loaded.PopulateDefaults();
        return loaded;
    }

    public void PopulateDefaults()
    {
        SetDefault("Server", "BuildPath", "");
        SetDefault("Server", "BackendUrl", "http://127.0.0.1:5595");
        SetDefault("Server", "Region", "NA");
        SetDefault("Server", "StandardServers", "2");
        SetDefault("Server", "BattleRoyaleServers", "2");
        SetDefault("Server", "ServerLogin", "CoreServer{n}");
        SetDefault("Server", "PcName", "");
        SetDefault("Server", "ReadyTimeout", "180");
        SetDefault("Server", "AllowUnsupportedBuilds", "false");
        SetDefault("Paths", "DllsFolder", "");
        SetDefault("Paths", "DataFolder", "");
    }

    private void SetDefault(string section, string key, string defaultValue)
    {
        if (Get(section, key) is null)
        {
            Set(section, key, defaultValue);
        }
    }

    public static string CreateDefaultTemplate() =>
        """
        ; ==============================================================================
        ; CoreServerHost Configuration
        ; Dedicated Fortnite game server hosting configuration.
        ; Automatically updated after every update / start.
        ; ==============================================================================

        [Server]
        ; Path to your Fortnite build folder (must contain Engine and FortniteGame)
        BuildPath=

        ; The Core-Backend endpoint to register servers with
        ; Local backend: http://127.0.0.1:5595 | Friend/Remote: http://192.168.1.100:5595
        BackendUrl=http://127.0.0.1:5595

        ; Matchmaking Region advertised by servers: NA, EU, OCE, BR, ASIA
        Region=NA

        ; Number of standard / Save the World server instances to keep running
        StandardServers=2

        ; Number of Battle Royale server instances to keep running (Fortnite 1.7.2+)
        BattleRoyaleServers=2

        ; Server login name pattern; {n} is replaced with the server instance number
        ; Default: CoreServer{n} (e.g. CoreServer1, CoreServer2). You can change this to any pattern like MyHost_{n}
        ServerLogin=CoreServer{n}

        ; Custom PC name when hosting for a remote backend (empty uses this PC's name)
        PcName=

        ; Seconds before restarting a hung server (0 = wait forever)
        ReadyTimeout=180

        ; Allow builds not officially verified with Core V2 (true / false)
        AllowUnsupportedBuilds=false

        [Paths]
        ; Custom Core DLLs folder (empty uses cached Core V2 release DLLs)
        DllsFolder=

        ; Custom data folder for logs and downloads (empty uses %LOCALAPPDATA%\Core-Launcher)
        DataFolder=
        """;

    public void Save(string path)
    {
        var builder = new StringBuilder();
        builder.AppendLine("; ==============================================================================");
        builder.AppendLine("; CoreServerHost Configuration");
        builder.AppendLine("; Dedicated Fortnite game server hosting configuration.");
        builder.AppendLine("; Automatically updated after every update / start.");
        builder.AppendLine("; ==============================================================================");
        builder.AppendLine();

        builder.AppendLine("[Server]");
        builder.AppendLine("; Path to your Fortnite build folder (must contain Engine and FortniteGame)");
        builder.AppendLine($"BuildPath={Get("Server", "BuildPath") ?? ""}");
        builder.AppendLine();

        builder.AppendLine("; The Core-Backend endpoint to register servers with");
        builder.AppendLine("; Local backend: http://127.0.0.1:5595 | Friend/Remote: http://192.168.1.100:5595");
        builder.AppendLine($"BackendUrl={Get("Server", "BackendUrl") ?? "http://127.0.0.1:5595"}");
        builder.AppendLine();

        builder.AppendLine("; Matchmaking Region advertised by servers: NA, EU, OCE, BR, ASIA");
        builder.AppendLine($"Region={Get("Server", "Region") ?? "NA"}");
        builder.AppendLine();

        builder.AppendLine("; Number of standard / Save the World server instances to keep running");
        builder.AppendLine($"StandardServers={Get("Server", "StandardServers") ?? "2"}");
        builder.AppendLine();

        builder.AppendLine("; Number of Battle Royale server instances to keep running (Fortnite 1.7.2+)");
        builder.AppendLine($"BattleRoyaleServers={Get("Server", "BattleRoyaleServers") ?? "2"}");
        builder.AppendLine();

        builder.AppendLine("; Server login name pattern; {n} is replaced with the server instance number");
        builder.AppendLine("; Default: CoreServer{n} (e.g. CoreServer1, CoreServer2). You can change this to any pattern like MyHost_{n}");
        builder.AppendLine($"ServerLogin={Get("Server", "ServerLogin") ?? "CoreServer{n}"}");
        builder.AppendLine();

        builder.AppendLine("; Custom PC name when hosting for a remote backend (empty uses this PC's name)");
        builder.AppendLine($"PcName={Get("Server", "PcName") ?? ""}");
        builder.AppendLine();

        builder.AppendLine("; Seconds before restarting a hung server (0 = wait forever)");
        builder.AppendLine($"ReadyTimeout={Get("Server", "ReadyTimeout") ?? "180"}");
        builder.AppendLine();

        builder.AppendLine("; Allow builds not officially verified with Core V2 (true / false)");
        builder.AppendLine($"AllowUnsupportedBuilds={Get("Server", "AllowUnsupportedBuilds") ?? "false"}");
        builder.AppendLine();

        builder.AppendLine("[Paths]");
        builder.AppendLine("; Custom Core DLLs folder (empty uses cached Core V2 release DLLs)");
        builder.AppendLine($"DllsFolder={Get("Paths", "DllsFolder") ?? ""}");
        builder.AppendLine();

        builder.AppendLine("; Custom data folder for logs and downloads (empty uses %LOCALAPPDATA%\\Core-Launcher)");
        builder.AppendLine($"DataFolder={Get("Paths", "DataFolder") ?? ""}");
        builder.AppendLine();

        // Any custom user-defined sections or extra keys:
        foreach (var (section, keys) in _sections)
        {
            if (section.Equals("Server", StringComparison.OrdinalIgnoreCase) ||
                section.Equals("Paths", StringComparison.OrdinalIgnoreCase))
            {
                var extraKeys = keys.Where(k => !IsStandardKey(section, k.Key)).ToList();
                if (extraKeys.Count > 0)
                {
                    builder.AppendLine($"[{section}]");
                    foreach (var (key, value) in extraKeys)
                    {
                        builder.AppendLine($"{key}={value}");
                    }
                    builder.AppendLine();
                }
                continue;
            }

            builder.AppendLine($"[{section}]");
            foreach (var (key, value) in keys)
            {
                builder.AppendLine($"{key}={value}");
            }
            builder.AppendLine();
        }

        File.WriteAllText(path, builder.ToString());
    }

    private static bool IsStandardKey(string section, string key)
    {
        if (section.Equals("Server", StringComparison.OrdinalIgnoreCase))
        {
            return key.Equals("BuildPath", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("BackendUrl", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("Region", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("StandardServers", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("BattleRoyaleServers", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("ServerLogin", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("Login", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("PcName", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("ReadyTimeout", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("AllowUnsupportedBuilds", StringComparison.OrdinalIgnoreCase);
        }

        if (section.Equals("Paths", StringComparison.OrdinalIgnoreCase))
        {
            return key.Equals("DllsFolder", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("DataFolder", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}

/// <summary>
/// Alias for backwards compatibility.
/// </summary>
public sealed class SetupIni : SettingsIni
{
}

