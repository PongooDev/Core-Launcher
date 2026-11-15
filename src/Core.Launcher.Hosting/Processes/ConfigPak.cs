using System.Security.Cryptography;
using System.Text;
using Core.Launcher.Hosting.Builds;
using Core.Launcher.Hosting.Components;

namespace Core.Launcher.Hosting.Processes;

/// <summary>
/// The build's NoRedist config (<see cref="PurchaseFlowConfig"/>) as a pak, for builds that read no loose .ini (Core-Backend
/// #355). From 6.20 at the latest the pak file refuses every loose file ending in .ini but GameUserSettings.ini
/// (FPakPlatformFile::IsNonPakFilenameAllowed, 6.30 0x2401AC0, with the two names Initialize sets at 0x2401310; 6.22's
/// Initialize 0x23A9CB0 and 6.31's 0x240E580 set them too; 6.00 CL-4395664 has neither). So 6.30 read the pak's
/// [EpicPurchaseFlow] Prod, Epic's launcher-website-prod07 page, which no longer loads: the Store's purchase window
/// spun forever and nothing reached the backend. The flow reads Prod once, when the UIManager widget is constructed
/// (UIManager_C::BindDelegates -> UFortMcpContext::RegisterForEpicPurchasingDisplay, 0x118E1E0), on the login screen
/// before the backend's hotfix arrives, so only a file in a pak can set it.
/// <para>
/// A file in a pak is read whatever its extension, and the config hierarchy takes NoRedist\Default&lt;Name&gt;.ini after
/// Default&lt;Name&gt;.ini (6.30 0x17AF1B0), so this pak holds FortniteGame/Config/NoRedist/DefaultGame.ini and
/// DefaultEngine.ini with the loose files' text. It is written like Core's server-assets pak (Core-ServerAssets'
/// PakWriter, which these builds mount): version 3, unencrypted, uncompressed, mount point ../../../, and for a build that
/// signs its paks a .sig with a zeroed master signature and the CRC32 of each 64 KiB chunk.
/// </para>
/// </summary>
public static class ConfigPak
{
    /// <summary>Before the platform: pakchunkCoreConfig-WindowsClient_P.pak.</summary>
    public const string Prefix = "pakchunkCoreConfig-";

    public const string GameIni = "FortniteGame/Config/NoRedist/DefaultGame.ini";

    public const string EngineIni = "FortniteGame/Config/NoRedist/DefaultEngine.ini";

    public const string MountPoint = "../../../";

    private const uint PakMagic = 0x5A6F12E1;

    private const int PakVersion = 3; // PakFile_Version_CompressionEncryption

    private const int SigChunk = 64 * 1024;

    /// <summary>
    /// Whether the build gets the pak: every build after 6.00 CL-4395664, the last one checked that still reads loose .ini
    /// files (6.01 to 6.10 are not checked; there the pak only repeats the loose files). Older builds keep only the loose files.
    /// </summary>
    public static bool Applies(int changelist) => changelist > KnownBuilds.Fortnite600Earlier;

    /// <summary>The pak's path in <paramref name="paksFolder"/>, named for the build's platform (<see cref="ServerAssets.Platform"/>).</summary>
    public static string PakPath(string paksFolder) => Path.Combine(paksFolder, $"{Prefix}{ServerAssets.Platform(paksFolder)}_P.pak");

    /// <summary>
    /// Writes the pak of <paramref name="files"/> (pak path, bytes) to <paramref name="paksFolder"/>, and its .sig when
    /// <paramref name="signed"/>; returns false when both already hold that. The pak goes first and the sig after it, and a
    /// pak whose sig can't follow is deleted, as a signed build stops on a pak whose sig doesn't match it. Throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when it can't (a running game holds the pak open).
    /// </summary>
    public static bool Write(string paksFolder, IReadOnlyList<(string Name, byte[] Data)> files, bool signed)
    {
        var pakPath = PakPath(paksFolder);
        var sigPath = Path.ChangeExtension(pakPath, ".sig");
        var pak = Build(files);
        var sig = signed ? Signature(pak) : null;
        if (Same(pakPath, pak) && (sig is null ? !File.Exists(sigPath) : Same(sigPath, sig)))
        {
            return false;
        }

        Replace(pakPath, pak);
        try
        {
            if (sig is null)
            {
                File.Delete(sigPath);
            }
            else
            {
                Replace(sigPath, sig);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            File.Delete(pakPath);
            throw;
        }

        return true;
    }

    /// <summary>The pak: each file an inline FPakEntry (offset 0) and its bytes, then the index, then the 44-byte footer.</summary>
    public static byte[] Build(IReadOnlyList<(string Name, byte[] Data)> files)
    {
        using var pak = new MemoryStream();
        var entries = new List<(string Name, long Offset, long Size, byte[] Sha1)>();
        foreach (var (name, data) in files)
        {
            var sha1 = SHA1.HashData(data);
            var offset = pak.Position;
            WriteEntry(pak, 0, data.LongLength, sha1);
            pak.Write(data);
            entries.Add((name, offset, data.LongLength, sha1));
        }

        using var index = new MemoryStream();
        WriteFString(index, MountPoint);
        index.Write(BitConverter.GetBytes(entries.Count));
        foreach (var entry in entries)
        {
            WriteFString(index, entry.Name);
            WriteEntry(index, entry.Offset, entry.Size, entry.Sha1);
        }

        var indexBytes = index.ToArray();
        var indexOffset = pak.Position;
        pak.Write(indexBytes);
        pak.Write(BitConverter.GetBytes(PakMagic));
        pak.Write(BitConverter.GetBytes(PakVersion));
        pak.Write(BitConverter.GetBytes(indexOffset));
        pak.Write(BitConverter.GetBytes((long)indexBytes.Length));
        pak.Write(SHA1.HashData(indexBytes));
        return pak.ToArray();
    }

    /// <summary>The .sig: a 64-byte master signature (zeroed), a uint32 chunk count, then each 64 KiB chunk's CRC32.</summary>
    public static byte[] Signature(byte[] pak)
    {
        using var sig = new MemoryStream();
        sig.Write(new byte[64]);
        var chunks = (pak.Length + SigChunk - 1) / SigChunk;
        sig.Write(BitConverter.GetBytes((uint)chunks));
        for (var chunk = 0; chunk < chunks; chunk++)
        {
            var start = chunk * SigChunk;
            sig.Write(BitConverter.GetBytes(Crc32(pak.AsSpan(start, Math.Min(SigChunk, pak.Length - start)))));
        }

        return sig.ToArray();
    }

    /// <summary>zlib's CRC32 (polynomial 0xEDB88320), as the engine checks each chunk with.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private static bool Same(string path, byte[] bytes)
    {
        var file = new FileInfo(path);
        return file.Exists && file.Length == bytes.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);
    }

    private static void Replace(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>FPakEntry, version 3: Offset, Size, UncompressedSize, CompressionMethod, Hash, bEncrypted, CompressionBlockSize.</summary>
    private static void WriteEntry(Stream s, long offset, long size, byte[] sha1)
    {
        Span<byte> b = stackalloc byte[53];
        BitConverter.TryWriteBytes(b[0..8], offset);
        BitConverter.TryWriteBytes(b[8..16], size);
        BitConverter.TryWriteBytes(b[16..24], size);
        BitConverter.TryWriteBytes(b[24..28], 0); // COMPRESS_None
        sha1.CopyTo(b[28..48]);
        b[48] = 0; // not encrypted
        BitConverter.TryWriteBytes(b[49..53], 0u); // no compression blocks
        s.Write(b);
    }

    /// <summary>An FString: ANSI with a positive length when it can be, UTF-16 with a negative length otherwise.</summary>
    private static void WriteFString(Stream s, string text)
    {
        if (text.All(c => c < 128))
        {
            var bytes = Encoding.ASCII.GetBytes(text + "\0");
            s.Write(BitConverter.GetBytes(bytes.Length));
            s.Write(bytes);
        }
        else
        {
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            s.Write(BitConverter.GetBytes(-(bytes.Length / 2)));
            s.Write(bytes);
        }
    }
}
