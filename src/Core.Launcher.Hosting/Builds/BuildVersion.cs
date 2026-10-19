using System.Text.Json;
using System.Text.RegularExpressions;

namespace Core.Launcher.Hosting.Builds;

/// <summary>A build's version: Engine\Build\Build.version, or what the launcher could read elsewhere (then the engine version is 0.0.0).</summary>
public sealed partial record BuildVersion(int MajorVersion, int MinorVersion, int PatchVersion, int Changelist, string BranchName)
{
    public const string RelativePath = @"Engine\Build\Build.version";

    public static BuildVersion Unknown { get; } = new(0, 0, 0, 0, "");

    public string EngineVersion => $"{MajorVersion}.{MinorVersion}.{PatchVersion}";

    /// <summary>"UE 4.16.0", or null when only the branch and changelist are known.</summary>
    public string? Engine => MajorVersion > 0 ? $"UE {EngineVersion}" : null;

    /// <summary>The branch's version for people: "++Fortnite+Release-14.60" → "14.60", "++Fortnite+Release-Cert" → "Cert".</summary>
    public string ShortName
    {
        get
        {
            var branch = BranchName;
            var release = branch.LastIndexOf("Release-", StringComparison.OrdinalIgnoreCase);
            return release >= 0 ? branch[(release + 8)..] : branch.TrimStart('+').Replace("Fortnite+", "", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static BuildVersion? Read(string buildFolder)
    {
        var path = Path.Combine(buildFolder, RelativePath);
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static BuildVersion Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new BuildVersion(
            Int(root, "MajorVersion"),
            Int(root, "MinorVersion"),
            Int(root, "PatchVersion"),
            Int(root, "Changelist"),
            root.TryGetProperty("BranchName", out var branch) ? branch.GetString() ?? "" : "");
    }

    /// <summary>"++Fortnite+Release-14.60-CL-14786821" (also with a "Ship-" prefix or "-Windows" suffix) → branch and changelist.</summary>
    public static BuildVersion? FromBuildString(string text)
    {
        var match = BuildString().Match(text);
        return match.Success && int.TryParse(match.Groups["cl"].Value, out var changelist) && changelist > 0
            ? new BuildVersion(0, 0, 0, changelist, match.Groups["branch"].Value)
            : null;
    }

    /// <summary>A shared build's folder name, "14.60-CL-14786821" or "++Fortnite+Release-14.60-CL-14786821".</summary>
    public static BuildVersion? FromFolderName(string name)
    {
        if (FromBuildString(name) is { } full)
        {
            return full;
        }

        var match = FolderName().Match(name);
        return match.Success && int.TryParse(match.Groups["cl"].Value, out var changelist) && changelist > 0
            ? new BuildVersion(0, 0, 0, changelist, match.Groups["version"].Success ? $"++Fortnite+Release-{match.Groups["version"].Value}" : "")
            : null;
    }

    private static int Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    [GeneratedRegex(@"(?<branch>\+\+Fortnite\+[A-Za-z0-9._+-]+?)-CL-(?<cl>\d+)(?![0-9])")]
    private static partial Regex BuildString();

    [GeneratedRegex(@"(?:(?<version>\d+\.\d+(?:\.\d+)?)[ _-]+)?CL-?(?<cl>\d{6,9})(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex FolderName();
}
