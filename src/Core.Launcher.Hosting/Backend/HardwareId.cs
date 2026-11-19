using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Core.Launcher.Hosting.Backend;

/// <summary>
/// The machine id the launcher sends with its backend login (Core-Backend #390), for the host's hardware bans: the
/// SHA-256 of a launcher prefix and Windows' MachineGuid (HKLM\SOFTWARE\Microsoft\Cryptography, read only), never the
/// MachineGuid itself. The prefix makes the hash Core's own, so it matches no other program's hash of the same value.
/// The backend keeps only a salted hash of what it is sent. A backend from before #390 ignores the header.
/// </summary>
public static class HardwareId
{
    /// <summary>The header the login carries it in.</summary>
    public const string HeaderName = "X-Core-Hardware-Id";

    /// <summary>What is hashed before the MachineGuid.</summary>
    public const string Prefix = "CoreLauncher-HWID:";

    private const string CryptographyKey = @"SOFTWARE\Microsoft\Cryptography";

    private static readonly Lazy<string?> Machine = new(() => FromMachineGuid(ReadMachineGuid()));

    /// <summary>This PC's id (64 lowercase hex digits), or null when Windows has no MachineGuid to read.</summary>
    public static string? Current => Machine.Value;

    /// <summary>The id for a MachineGuid (trimmed and lowercased first, as Windows writes it in either case), or null for none.</summary>
    public static string? FromMachineGuid(string? machineGuid)
    {
        var value = machineGuid?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Prefix + value)));
    }

    /// <summary>
    /// The MachineGuid from the 64-bit registry view (where Windows keeps it, whatever the process's bitness), opened read
    /// only; null when it can't be read.
    /// </summary>
    private static string? ReadMachineGuid()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(CryptographyKey, writable: false);
            return key?.GetValue("MachineGuid") as string;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
