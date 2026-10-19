using System.IO.Compression;
using System.Security.Cryptography;

namespace Core.Launcher.Hosting.Components;

public static class Downloads
{
    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destination"/> through a temporary file, refusing it when the
    /// size or SHA-256 given doesn't match. Progress reports "12.3 / 30.1 MB".
    /// </summary>
    public static async Task FileAsync(HttpClient http, string url, string destination, long? size, string? sha256, IProgress<string>? progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".download";
        try
        {
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? size;
                await using var source = await response.Content.ReadAsStreamAsync(cancellation);
                await using var file = File.Create(temporary);
                var buffer = new byte[81920];
                long received = 0;
                var reported = DateTime.MinValue;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    received += read;
                    if (progress is not null && DateTime.UtcNow - reported > TimeSpan.FromMilliseconds(250))
                    {
                        reported = DateTime.UtcNow;
                        progress.Report(total is { } length ? $"{Megabytes(received)} / {Megabytes(length)} MB" : $"{Megabytes(received)} MB");
                    }
                }
            }

            var actual = new FileInfo(temporary).Length;
            if (size is { } expected && actual != expected)
            {
                throw new InvalidDataException($"{Path.GetFileName(destination)}: downloaded {actual} bytes, expected {expected}");
            }

            if (sha256 is not null && await Sha256Async(temporary, cancellation) is var hash && hash != sha256)
            {
                throw new InvalidDataException($"{Path.GetFileName(destination)}: SHA-256 {hash} doesn't match the expected {sha256}");
            }

            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static async Task<string> Sha256Async(string path, CancellationToken cancellation = default)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellation));
    }

    /// <summary>Extracts a zip into <paramref name="destination"/>, dropping the single top folder GitHub and nodejs.org archives wrap everything in.</summary>
    public static void ExtractWithoutTopFolder(string zip, string destination)
    {
        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')).ToList();
        var top = names.Count > 0 && names[0].Contains('/') ? names[0][..(names[0].IndexOf('/') + 1)] : null;
        if (top is null || !names.All(name => name.StartsWith(top, StringComparison.Ordinal)))
        {
            top = "";
        }

        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.Replace('\\', '/')[top.Length..];
            if (relative.Length == 0)
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{entry.FullName} points outside the archive");
            }

            if (relative.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0");
}
