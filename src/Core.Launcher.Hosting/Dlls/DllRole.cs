namespace Core.Launcher.Hosting.Dlls;

public enum DllRole
{
    Server,
    Client,
    Redirect,
}

public static class CoreDlls
{
    /// <summary>The CoreV2 project's output (and release asset) name: Server → Core.dll, Client → CoreClient.dll, Redirect → CoreRedirect.dll.</summary>
    public static string FileName(DllRole role) => role switch
    {
        DllRole.Server => "Core.dll",
        DllRole.Client => "CoreClient.dll",
        DllRole.Redirect => "CoreRedirect.dll",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    /// <summary>The DLL's own log beside the game exe, as CoreV2's Log::Initialize names it: &lt;Name&gt;_log_&lt;pid&gt;.txt.</summary>
    public static string LogFileName(DllRole role, int processId) => $"{role}_log_{processId}.txt";

    public static IReadOnlyList<DllRole> All { get; } = [DllRole.Server, DllRole.Client, DllRole.Redirect];
}
