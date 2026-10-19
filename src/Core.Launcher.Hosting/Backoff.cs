namespace Core.Launcher.Hosting;

public static class Backoff
{
    private static readonly TimeSpan First = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Longest = TimeSpan.FromSeconds(60);

    /// <summary>5 s after the first failure in a row, doubling, at most 60 s.</summary>
    public static TimeSpan Delay(int failures)
    {
        if (failures <= 0)
        {
            return TimeSpan.Zero;
        }

        var seconds = First.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 10));
        return TimeSpan.FromSeconds(Math.Min(seconds, Longest.TotalSeconds));
    }
}
