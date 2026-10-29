using System.Text.Json;
using System.Text.Json.Serialization;
using Core.Launcher.Hosting.Builds;

namespace Core.Launcher.Hosting.Settings;

public sealed class SettingsStore
{
    public const int MaxInstances = 16;

    /// <summary>
    /// 1: -FORCELOGFLUSH is removed from every saved build's arguments. Cert's defaults had it (from run_server.py), and
    /// it can deadlock the game before its first log line: every line's Flush() holds FAsyncWriter's buffer lock and
    /// waits for the writer thread, which can be waiting for that lock in its periodic flush (CoreV2 session,
    /// 2026-10-19: about 2 runs in 3 stalled with it, 4 in 4 started without it). Core's own logs flush themselves.
    /// 2: every build records its changelist, and Cert servers get -McpRegion=NA (<see cref="KnownBuilds.CertServerRegion"/>).
    /// 3: Core knows 1.2 (<see cref="KnownBuilds.Fortnite12"/>, support in progress), so a 1.2 build added while it was
    /// unsupported becomes the known one (<see cref="KnownBuilds.MakeKnown"/>: it keeps its path and options and loses
    /// CoreDefaultsAdded). Builds still named what an earlier launcher made up ("Cert CL-3532353",
    /// "Fortnite Cert CL-3541083") take the known build's name (<see cref="KnownBuilds.CurrentName"/>); names set by
    /// hand stay.
    /// </summary>
    public const int CurrentVersion = 3;

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
        if (settings.Version < CurrentVersion)
        {
            Migrate(settings, settings.Version);
            Save(settings);
        }

        Normalize(settings);
        return settings;
    }

    private static void Migrate(LauncherSettings settings, int from)
    {
        foreach (var build in settings.Builds ?? [])
        {
            build.ClientArguments ??= "";
            build.ServerArguments ??= "";
            if (from < 1)
            {
                build.ClientArguments = WithoutSwitch(build.ClientArguments, "-FORCELOGFLUSH");
                build.ServerArguments = WithoutSwitch(build.ServerArguments, "-FORCELOGFLUSH");
            }

            if (from < 2)
            {
                if (build.Changelist == 0)
                {
                    build.Changelist = BuildVersion.Read(build.Path ?? "")?.Changelist
                        ?? (build.Name?.Contains($"CL-{KnownBuilds.Cert}", StringComparison.OrdinalIgnoreCase) == true ? KnownBuilds.Cert : 0);
                }

                if (build.Changelist == KnownBuilds.Cert && !build.ServerArguments.Contains("-McpRegion=", StringComparison.OrdinalIgnoreCase))
                {
                    build.ServerArguments = (build.ServerArguments + " " + KnownBuilds.CertServerRegion).Trim();
                }
            }

            if (from < 3 && build.Changelist == KnownBuilds.Fortnite12)
            {
                build.ClientDlls ??= [];
                build.ServerDlls ??= [];
                KnownBuilds.MakeKnown(build);
            }
        }

        if (from < 3 && settings.Builds is { } builds)
        {
            RenameGeneratedNames(settings, builds);
        }
    }

    /// <summary>Gives builds still named what an earlier launcher made up the known build's name, keeping names unique and the selection.</summary>
    private static void RenameGeneratedNames(LauncherSettings settings, List<BuildSettings> builds)
    {
        var selected = settings.FindSelectedBuild();
        foreach (var build in builds)
        {
            if (KnownBuilds.CurrentName(build.Name ?? "", build.Changelist) is not { } name)
            {
                continue;
            }

            var known = KnownBuilds.NameOf(build.Changelist)!;
            for (var copy = 2; builds.Any(other => other != build && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase)); ++copy)
            {
                name = $"{known} ({copy})";
            }

            build.Name = name;
            if (build == selected)
            {
                settings.SelectedBuild = name;
            }
        }
    }

    private static string WithoutSwitch(string arguments, string name) =>
        string.Join(' ', arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(argument => !argument.Equals(name, StringComparison.OrdinalIgnoreCase)));

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
        settings.Version = CurrentVersion;
        settings.Builds ??= [];
        settings.Dlls ??= new DllSettings();
        settings.Backend ??= new BackendSettings();
        settings.Servers ??= new ServerSettings();
        settings.PlayerName = settings.PlayerName?.Trim() ?? "";
        settings.Password = settings.Password?.Trim() ?? "";
        settings.Servers.Instances = Math.Clamp(settings.Servers.Instances, 0, MaxInstances);
        settings.Servers.BattleRoyaleInstances = Math.Clamp(settings.Servers.BattleRoyaleInstances, 0, MaxInstances);
        settings.Servers.ReadyTimeoutSeconds = Math.Max(0, settings.Servers.ReadyTimeoutSeconds);
        // Settings saved before the region existed have none: NA, what their servers advertised.
        settings.Servers.Region = string.IsNullOrWhiteSpace(settings.Servers.Region) ? "NA" : settings.Servers.Region.Trim().ToUpperInvariant();
        settings.Backend.Port = Math.Clamp(settings.Backend.Port, 0, 65535);
        settings.Backend.Remote ??= new RemoteBackend();
        settings.Backend.SavedRemotes ??= [];
        settings.Backend.SavedRemotes.RemoveAll(remote => remote is null);
        foreach (var remote in settings.Backend.SavedRemotes.Append(settings.Backend.Remote))
        {
            remote.Name = remote.Name?.Trim() ?? "";
            remote.Address = remote.Address?.Trim() ?? "";
        }
        foreach (var build in settings.Builds)
        {
            build.ClientDlls ??= [];
            build.ServerDlls ??= [];
        }
    }
}
