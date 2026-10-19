using System.Text.Json;
using System.Text.Json.Serialization;

namespace Core.Launcher.Hosting.Settings;

public sealed class SettingsStore
{
    public const int MaxInstances = 16;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = path;
    }

    public string FilePath => _path;

    public LauncherSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new LauncherSettings();
        }

        var settings = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(_path), Options) ?? new LauncherSettings();
        Normalize(settings);
        return settings;
    }

    public void Save(LauncherSettings settings)
    {
        Normalize(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, _path, true);
    }

    public static LauncherSettings Clone(LauncherSettings settings) =>
        JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(settings, Options), Options)!;

    private static void Normalize(LauncherSettings settings)
    {
        settings.Builds ??= [];
        settings.Dlls ??= new DllSettings();
        settings.Backend ??= new BackendSettings();
        settings.Servers ??= new ServerSettings();
        settings.PlayerName = settings.PlayerName?.Trim() ?? "";
        settings.Servers.Instances = Math.Clamp(settings.Servers.Instances, 0, MaxInstances);
        settings.Servers.ReadyTimeoutSeconds = Math.Max(0, settings.Servers.ReadyTimeoutSeconds);
        settings.Backend.Port = Math.Clamp(settings.Backend.Port, 0, 65535);
        foreach (var build in settings.Builds)
        {
            build.ClientDlls ??= [];
            build.ServerDlls ??= [];
        }
    }
}
