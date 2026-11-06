using System.IO.Compression;
using System.Security.Cryptography;

namespace Core.Launcher.Hosting.Components;

/// <summary>How <see cref="Downloads.VerifiedAsync"/> copes with a long download on a bad line.</summary>
public sealed class DownloadOptions
{
    /// <summary>Fail (<see cref="DownloadStalledException"/>) when no byte arrives for this long; null waits forever.</summary>
    public TimeSpan? StallTimeout { get; init; }

    /// <summary>Continue an unfinished temporary file with a Range request instead of starting over.</summary>
    public bool Resume { get; init; }

    /// <summary>How many times to try (a retry resumes when <see cref="Resume"/> is on).</summary>
    public int Attempts { get; init; } = 1;

    /// <summary>The wait before the next try, given the attempt that just failed; 2 s times the attempt by default.</summary>
    public Func<int, TimeSpan>? RetryDelay { get; init; }

    /// <summary>(bytes of the file now on disk, the file's total size if known): called as bytes arrive, first with what is kept from an earlier try.</summary>
    public Action<long, long?>? Received { get; init; }

    /// <summary>(bytes hashed, file size): called while the finished download is checked.</summary>
    public Action<long, long>? Hashed { get; init; }

    /// <summary>(the attempt that failed, why): called before a retry.</summary>
    public Action<int, Exception>? Retrying { get; init; }
}

/// <summary>A download that received no bytes for its stall timeout.</summary>
public sealed class DownloadStalledException(TimeSpan timeout)
    : IOException($"no data received for {(timeout.TotalSeconds < 120 ? $"{timeout.TotalSeconds:0} s" : $"{timeout.TotalMinutes:0} min")}");

public static class Downloads
{
    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destination"/> through a temporary file, refusing it when the
    /// size or SHA-256 given doesn't match. Progress reports "12.3 / 30.1 MB".
    /// </summary>
    public static async Task FileAsync(HttpClient http, string url, string destination, long? size, string? sha256, IProgress<string>? progress, CancellationToken cancellation)
    {
        var temporary = destination + ".download";
        try
        {
            await VerifiedAsync(http, url, temporary, Path.GetFileName(destination), size, sha256, progress, cancellation);
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

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="temporary"/> and checks it, throwing
    /// <see cref="InvalidDataException"/> (naming <paramref name="name"/>) when the size or SHA-256 given doesn't match.
    /// The file is left where it is either way: the caller moves it into place or deletes it. With
    /// <paramref name="options"/>, a stalled connection fails (<see cref="DownloadStalledException"/>), a failed one is
    /// tried again, and an unfinished <paramref name="temporary"/> is resumed with an HTTP Range request.
    /// </summary>
    public static async Task VerifiedAsync(HttpClient http, string url, string temporary, string name, long? size, string? sha256, IProgress<string>? progress, CancellationToken cancellation, DownloadOptions? options = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(temporary))!);
        var attempts = Math.Max(1, options?.Attempts ?? 1);
        for (var attempt = 1; ; ++attempt)
        {
            try
            {
                var resumed = await TransferAsync(http, url, temporary, size, progress, options, cancellation);
                try
                {
                    await CheckAsync(temporary, name, size, sha256, options, cancellation);
                    return;
                }
                catch (InvalidDataException) when (resumed > 0)
                {
                    // The partial file this resumed from was bad: start over once, from nothing.
                    File.Delete(temporary);
                    --attempt;
                }
            }
            catch (Exception exception) when (attempt < attempts && !cancellation.IsCancellationRequested && IsTransient(exception))
            {
                options?.Retrying?.Invoke(attempt, exception);
                await Task.Delay(options?.RetryDelay?.Invoke(attempt) ?? TimeSpan.FromSeconds(2 * attempt), cancellation);
            }
        }
    }

    private static bool IsTransient(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } status } => (int)status >= 500 || status is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests,
        HttpRequestException => true,
        IOException => true,
        _ => false,
    };

    /// <summary>Downloads into <paramref name="temporary"/>; returns how many bytes of it were already there and kept (0 for a fresh download).</summary>
    private static async Task<long> TransferAsync(HttpClient http, string url, string temporary, long? size, IProgress<string>? progress, DownloadOptions? options, CancellationToken cancellation)
    {
        long existing = 0;
        if (options?.Resume == true && File.Exists(temporary))
        {
            existing = new FileInfo(temporary).Length;
            if (size is { } expected && existing > expected)
            {
                existing = 0;
            }
        }

        if (existing > 0 && existing == size)
        {
            options?.Received?.Invoke(existing, size);
            return existing;
        }

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        try
        {
            stall.CancelAfter(options?.StallTimeout ?? Timeout.InfiniteTimeSpan);
            var response = await SendAsync(http, url, existing, stall.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                response.Dispose();
                existing = 0;
                response = await SendAsync(http, url, 0, stall.Token);
            }

            using (response)
            {
                response.EnsureSuccessStatusCode();
                var append = existing > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From == existing;
                if (!append)
                {
                    existing = 0;
                }

                var length = response.Content.Headers.ContentLength;
                var total = length is { } remaining ? existing + remaining : size;
                await using var source = await response.Content.ReadAsStreamAsync(stall.Token);
                await using var file = new FileStream(temporary, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
                var buffer = new byte[262144];
                var received = existing;
                options?.Received?.Invoke(received, total);
                var reported = DateTime.MinValue;
                while (true)
                {
                    stall.CancelAfter(options?.StallTimeout ?? Timeout.InfiniteTimeSpan);
                    var read = await source.ReadAsync(buffer, stall.Token);
                    if (read <= 0)
                    {
                        break;
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), stall.Token);
                    received += read;
                    options?.Received?.Invoke(received, total);
                    if (progress is not null && DateTime.UtcNow - reported > TimeSpan.FromMilliseconds(250))
                    {
                        reported = DateTime.UtcNow;
                        progress.Report(total is { } all ? $"{Megabytes(received)} / {Megabytes(all)} MB" : $"{Megabytes(received)} MB");
                    }
                }

                stall.CancelAfter(Timeout.InfiniteTimeSpan);
                if (length is not null && received != total)
                {
                    throw new IOException($"the connection closed after {received} of {total} bytes");
                }
            }

            return existing;
        }
        catch (OperationCanceledException) when (stall.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            throw new DownloadStalledException(options?.StallTimeout ?? Timeout.InfiniteTimeSpan);
        }
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient http, string url, long from, CancellationToken cancellation)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (from > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, null);
        }

        return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
    }

    private static async Task CheckAsync(string temporary, string name, long? size, string? sha256, DownloadOptions? options, CancellationToken cancellation)
    {
        var actual = new FileInfo(temporary).Length;
        if (size is { } expected && actual != expected)
        {
            throw new InvalidDataException($"{name}: downloaded {actual} bytes, expected {expected}");
        }

        if (sha256 is not null && await Sha256Async(temporary, cancellation, options?.Hashed) is var hash && hash != sha256)
        {
            throw new InvalidDataException($"{name}: SHA-256 {hash} doesn't match the expected {sha256}");
        }
    }

    /// <summary>The file's SHA-256 in lower-case hex; <paramref name="hashed"/> is told (bytes hashed, file size) as it goes.</summary>
    public static async Task<string> Sha256Async(string path, CancellationToken cancellation = default, Action<long, long>? hashed = null)
    {
        await using var file = File.OpenRead(path);
        if (hashed is null)
        {
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellation));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1048576];
        long done = 0;
        hashed(0, file.Length);
        int read;
        while ((read = await file.ReadAsync(buffer, cancellation)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            done += read;
            hashed(done, file.Length);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
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
