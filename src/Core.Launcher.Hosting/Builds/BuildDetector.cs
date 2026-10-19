using System.Text;
using System.Text.Json;

namespace Core.Launcher.Hosting.Builds;

/// <param name="Source">Where the version came from: Build.version, cloudcontent.json, BuildInfo.ini, the exe, or the folder name.</param>
public sealed record DetectedBuild(string Folder, BuildVersion Version, string Source);

public static class BuildDetector
{
    public const string ClientExecutable = @"FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe";

    private const int MaxBuildStringChars = 160;
    private static readonly byte[] BuildStringMarker = Encoding.Unicode.GetBytes("++Fortnite+");

    /// <summary>
    /// Finds the build root (the folder with FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe or
    /// Engine\Build\Build.version) from the folder the user picked: that folder, its Fortnite subfolder, a folder just
    /// inside it, or a folder above it. Shared builds often lack Build.version, so the version also comes from
    /// Cloud\cloudcontent.json, Cloud\BuildInfo.ini, the build string in the exe, or a "CL-&lt;n&gt;" folder name.
    /// </summary>
    public static DetectedBuild? Detect(string picked)
    {
        if (string.IsNullOrWhiteSpace(picked) || !Directory.Exists(picked))
        {
            return null;
        }

        foreach (var root in Candidates(Path.GetFullPath(picked)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var executable = Path.Combine(root, ClientExecutable);
            var hasExecutable = File.Exists(executable);
            if (!hasExecutable && !File.Exists(Path.Combine(root, BuildVersion.RelativePath)))
            {
                continue;
            }

            return ReadVersion(root, hasExecutable ? executable : null) ?? new DetectedBuild(root, BuildVersion.Unknown, "nothing");
        }

        return null;
    }

    private static IEnumerable<string> Candidates(string folder)
    {
        yield return folder;
        yield return Path.Combine(folder, "Fortnite");

        IEnumerable<string> children;
        try
        {
            children = Directory.GetDirectories(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            children = [];
        }

        foreach (var child in children)
        {
            yield return child;
        }

        var parent = Directory.GetParent(folder);
        for (var depth = 0; parent is not null && depth < 5; ++depth, parent = parent.Parent)
        {
            yield return parent.FullName;
        }
    }

    private static DetectedBuild? ReadVersion(string root, string? executable)
    {
        if (BuildVersion.Read(root) is { Changelist: > 0 } fromFile)
        {
            return new DetectedBuild(root, fromFile, "Build.version");
        }

        if (FromCloudContent(Path.Combine(root, "Cloud", "cloudcontent.json")) is { } fromCloud)
        {
            return new DetectedBuild(root, fromCloud, "cloudcontent.json");
        }

        if (FromBuildInfo(Path.Combine(root, "Cloud", "BuildInfo.ini")) is { } fromInfo)
        {
            return new DetectedBuild(root, fromInfo, "BuildInfo.ini");
        }

        if (executable is not null && FindBuildString(executable) is { } text && BuildVersion.FromBuildString(text) is { } fromExe)
        {
            return new DetectedBuild(root, fromExe, Path.GetFileName(executable));
        }

        for (var folder = new DirectoryInfo(root); folder is not null; folder = folder.Parent)
        {
            if (BuildVersion.FromFolderName(folder.Name) is { } fromName)
            {
                return new DetectedBuild(root, fromName, "the folder name");
            }
        }

        return null;
    }

    private static BuildVersion? FromCloudContent(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("BuildVersion", out var value) && value.GetString() is { } text ? BuildVersion.FromBuildString(text) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static BuildVersion? FromBuildInfo(string path)
    {
        try
        {
            return File.ReadLines(path)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("Build=", StringComparison.OrdinalIgnoreCase))
                .Select(line => BuildVersion.FromBuildString(line[6..]))
                .FirstOrDefault(version => version is not null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The engine's build string compiled into the exe as UTF-16, e.g. "++Fortnite+Release-26.20-CL-28096793".</summary>
    public static string? FindBuildString(string executable)
    {
        using var file = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var buffer = new byte[4 << 20];
        var kept = 0;
        int read;
        while ((read = file.Read(buffer, kept, buffer.Length - kept)) > 0)
        {
            var length = kept + read;
            var data = buffer.AsSpan(0, length);
            for (var offset = 0; offset < length;)
            {
                var index = data[offset..].IndexOf(BuildStringMarker);
                if (index < 0)
                {
                    break;
                }

                var start = offset + index;
                if (ReadUtf16(data[start..]) is not { } text)
                {
                    break;
                }

                if (BuildVersion.FromBuildString(text) is not null)
                {
                    return text;
                }

                offset = start + BuildStringMarker.Length;
            }

            // Keep enough of the end for a string that runs past this chunk.
            kept = Math.Min(MaxBuildStringChars * 2, length);
            buffer.AsSpan(length - kept, kept).CopyTo(buffer);
        }

        return null;
    }

    /// <summary>Printable UTF-16 text up to its terminator, or null when the data ends before one.</summary>
    private static string? ReadUtf16(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder();
        for (var index = 0; index + 1 < data.Length && text.Length <= MaxBuildStringChars; index += 2)
        {
            var character = (char)(data[index] | data[index + 1] << 8);
            if (character < 0x20 || character > 0x7E)
            {
                return text.ToString();
            }

            text.Append(character);
        }

        return text.Length > MaxBuildStringChars ? text.ToString() : null;
    }
}
