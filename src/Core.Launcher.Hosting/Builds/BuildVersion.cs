using System.Text.Json;

namespace Core.Launcher.Hosting.Builds;

/// <summary>A build's Engine\Build\Build.version.</summary>
public sealed record BuildVersion(int MajorVersion, int MinorVersion, int PatchVersion, int Changelist, string BranchName)
{
    public const string RelativePath = @"Engine\Build\Build.version";

    public string EngineVersion => $"{MajorVersion}.{MinorVersion}.{PatchVersion}";

    public static BuildVersion? Read(string buildFolder)
    {
        var path = Path.Combine(buildFolder, RelativePath);
        return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
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

    private static int Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
}
