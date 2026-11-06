using System.Globalization;

namespace Core.Launcher.Hosting.Components;

public enum ServerAssetsPhase
{
    /// <summary>No check has run.</summary>
    Idle,

    /// <summary>Reading the manifest and the installed pak.</summary>
    Checking,

    /// <summary>Downloading the pak (or its sig).</summary>
    Downloading,

    /// <summary>Checking the download's SHA-256.</summary>
    Verifying,

    /// <summary>Moving the verified files into the Paks folder.</summary>
    Installing,

    /// <summary>The check finished: nothing to show.</summary>
    Done,

    /// <summary>The download or install failed; <see cref="ServerAssetsProgress.Error"/> says why and <see cref="ServerAssets.Retry"/> tries again.</summary>
    Failed,
}

/// <summary>Where a build's server assets check is: its phase and, while downloading or verifying, how far along it is.</summary>
/// <param name="Phase">What it is doing.</param>
/// <param name="Version">The build's version in the manifest ("4.0"), blank until the manifest is read.</param>
/// <param name="File">The file being downloaded.</param>
/// <param name="BytesDone">Bytes downloaded (Downloading) or hashed (Verifying).</param>
/// <param name="BytesTotal">The file's size, 0 when unknown.</param>
/// <param name="BytesPerSecond">The recent download speed, 0 until there is a measure.</param>
/// <param name="Remaining">The time left at that speed, null until there is a measure.</param>
/// <param name="Error">Why it failed (Failed only).</param>
public sealed record ServerAssetsProgress(
    ServerAssetsPhase Phase,
    string Version = "",
    string File = "",
    long BytesDone = 0,
    long BytesTotal = 0,
    double BytesPerSecond = 0,
    TimeSpan? Remaining = null,
    string Error = "")
{
    public static readonly ServerAssetsProgress None = new(ServerAssetsPhase.Idle);

    /// <summary>A check is under way (not idle, done or failed).</summary>
    public bool Active => Phase is ServerAssetsPhase.Checking or ServerAssetsPhase.Downloading or ServerAssetsPhase.Verifying or ServerAssetsPhase.Installing;

    /// <summary>Something to show: under way, or failed.</summary>
    public bool Visible => Active || Phase == ServerAssetsPhase.Failed;

    /// <summary>0 to 100; 0 when the total is unknown.</summary>
    public double Percent => BytesTotal > 0 ? Math.Clamp(BytesDone * 100.0 / BytesTotal, 0, 100) : 0;

    /// <summary>The bar can show how far along it is (otherwise it just moves).</summary>
    public bool HasFraction => Phase is ServerAssetsPhase.Downloading or ServerAssetsPhase.Verifying && BytesTotal > 0;

    /// <summary>"Downloading server assets for 4.0 – 412 MB / 922 MB (45%) · 8.1 MB/s · 1 min left"; blank when there is nothing to show.</summary>
    public string Describe()
    {
        var target = Version.Length > 0 ? $" for {Version}" : "";
        switch (Phase)
        {
            case ServerAssetsPhase.Checking:
                return "Checking server assets";
            case ServerAssetsPhase.Downloading:
                var line = $"Downloading server assets{target}";
                if (BytesTotal <= 0)
                {
                    return $"{line} – {Size(BytesDone, BytesDone)}";
                }

                line += $" – {Size(BytesDone, BytesTotal)} / {Size(BytesTotal, BytesTotal)} ({Percent:0}%)";
                if (BytesPerSecond > 0)
                {
                    line += $" · {Size((long)BytesPerSecond, (long)BytesPerSecond)}/s";
                    if (Remaining is { } left)
                    {
                        line += $" · {Left(left)} left";
                    }
                }

                return line;
            case ServerAssetsPhase.Verifying:
                return BytesTotal > 0 ? $"Verifying server assets{target} – {Percent:0}%" : $"Verifying server assets{target}";
            case ServerAssetsPhase.Installing:
                return $"Installing server assets{target}";
            case ServerAssetsPhase.Failed:
                return $"Server assets{target} failed: {Error}";
            default:
                return "";
        }
    }

    /// <summary>"412 MB" or "1.07 GB", in the unit <paramref name="scale"/> calls for (so 412 MB / 1.07 GB doesn't happen: both use the total's).</summary>
    private static string Size(long bytes, long scale)
    {
        const double MB = 1048576.0;
        const double GB = 1073741824.0;
        var inGigabytes = scale >= GB;
        var text = inGigabytes
            ? (bytes / GB).ToString("0.00", CultureInfo.InvariantCulture)
            : (bytes / MB).ToString(bytes < 100 * MB ? "0.0" : "0", CultureInfo.InvariantCulture);
        return $"{text} {(inGigabytes ? "GB" : "MB")}";
    }

    private static string Left(TimeSpan time)
    {
        if (time.TotalSeconds < 60)
        {
            return $"{Math.Max(1, (int)Math.Ceiling(time.TotalSeconds))} s";
        }

        if (time.TotalMinutes < 60)
        {
            return $"{(int)Math.Round(time.TotalMinutes)} min";
        }

        return $"{(int)time.TotalHours} h {time.Minutes} min";
    }
}
