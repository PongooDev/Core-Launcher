using System.Security.Cryptography;
using System.Text;
using Core.Launcher.Hosting.Builds;
using Core.Launcher.Hosting.Components;
using Core.Launcher.Hosting.Processes;

namespace Core.Launcher.Tests;

// Core-Backend #355: from 6.20 at the latest the game reads no loose .ini, so the NoRedist config also goes in a pak.
public sealed class ConfigPakTests
{
    private static readonly byte[] GameIni = Encoding.UTF8.GetBytes("[EpicPurchaseFlow]\r\nProd=\"http:///127.0.0.1:5595/purchase\"\r\n!ProdAltDomains=ClearArray\r\n");

    private static readonly byte[] EngineIni = Encoding.UTF8.GetBytes("[OnlineSubsystemMcp.OnlineIdentityMcp]\r\nProtocol=http\r\nRedirectUrl=127.0.0.1:5595\r\n");

    private static List<(string Name, byte[] Data)> Files() => [(ConfigPak.GameIni, GameIni), (ConfigPak.EngineIni, EngineIni)];

    [Fact]
    public void Only_builds_after_6_00_get_it()
    {
        Assert.False(ConfigPak.Applies(0));
        Assert.False(ConfigPak.Applies(4008490)); // 3.5.2
        Assert.False(ConfigPak.Applies(KnownBuilds.Fortnite600Earlier));
        Assert.True(ConfigPak.Applies(KnownBuilds.Fortnite600));
        Assert.True(ConfigPak.Applies(4497486)); // 6.20
        Assert.True(ConfigPak.Applies(4573096)); // 6.30
    }

    [Fact]
    public void Is_named_for_the_builds_platform_and_left_out_of_the_platform_lookup()
    {
        using var folder = new TempFolder();
        var paks = Directory.CreateDirectory(folder.Combine("Paks")).FullName;

        // Core's own paks never name the platform.
        File.WriteAllText(Path.Combine(paks, "pakchunkCoreConfig-Other_P.pak"), "");
        Assert.Equal("WindowsClient", ServerAssets.Platform(paks));

        File.WriteAllText(Path.Combine(paks, "pakchunk0-WindowsNoEditor.pak"), "");
        Assert.Equal(Path.Combine(paks, "pakchunkCoreConfig-WindowsNoEditor_P.pak"), ConfigPak.PakPath(paks));
        Assert.Equal("pakchunkCoreServerAssets-WindowsNoEditor_P.pak", ServerAssets.PakFileName(paks));
    }

    [Fact]
    public void Holds_the_files_under_the_mount_point_as_a_version_3_pak()
    {
        var pak = ConfigPak.Build(Files());

        // Footer: magic, version, index offset and size, index SHA-1.
        var footer = pak.AsSpan(pak.Length - 44);
        Assert.Equal(0x5A6F12E1u, BitConverter.ToUInt32(footer[0..4]));
        Assert.Equal(3, BitConverter.ToInt32(footer[4..8]));
        var indexOffset = BitConverter.ToInt64(footer[8..16]);
        var indexSize = BitConverter.ToInt64(footer[16..24]);
        Assert.Equal(pak.Length - 44, indexOffset + indexSize);
        var index = pak.AsSpan((int)indexOffset, (int)indexSize);
        Assert.True(SHA1.HashData(index).AsSpan().SequenceEqual(footer[24..44]));

        var at = 0;
        Assert.Equal("../../../", ReadFString(index, ref at));
        Assert.Equal(2, BitConverter.ToInt32(index[at..(at + 4)]));
        at += 4;
        foreach (var (name, data) in Files())
        {
            Assert.Equal(name, ReadFString(index, ref at));
            var offset = BitConverter.ToInt64(index[at..(at + 8)]);
            var size = BitConverter.ToInt64(index[(at + 8)..(at + 16)]);
            Assert.Equal(data.Length, size);
            Assert.Equal(size, BitConverter.ToInt64(index[(at + 16)..(at + 24)]));
            Assert.Equal(0, BitConverter.ToInt32(index[(at + 24)..(at + 28)]));
            Assert.True(SHA1.HashData(data).AsSpan().SequenceEqual(index[(at + 28)..(at + 48)]));
            Assert.Equal(0, index[at + 48]);
            at += 53;

            // Each file's bytes follow its 53-byte inline entry.
            Assert.True(pak.AsSpan((int)offset + 53, data.Length).SequenceEqual(data));
        }

        Assert.Equal(index.Length, at);
    }

    [Fact]
    public void Signs_each_64k_chunk_with_zlibs_crc32()
    {
        Assert.Equal(0xCBF43926u, ConfigPak.Crc32("123456789"u8));

        var pak = new byte[64 * 1024 + 10];
        new Random(355).NextBytes(pak);
        var sig = ConfigPak.Signature(pak);

        Assert.Equal(64 + 4 + 2 * 4, sig.Length);
        Assert.True(sig.AsSpan(0, 64).ToArray().All(b => b == 0));
        Assert.Equal(2u, BitConverter.ToUInt32(sig, 64));
        Assert.Equal(ConfigPak.Crc32(pak.AsSpan(0, 64 * 1024)), BitConverter.ToUInt32(sig, 68));
        Assert.Equal(ConfigPak.Crc32(pak.AsSpan(64 * 1024)), BitConverter.ToUInt32(sig, 72));
    }

    [Fact]
    public void Writes_the_pak_and_sig_once_and_again_when_the_text_changes()
    {
        using var folder = new TempFolder();
        var paks = Directory.CreateDirectory(folder.Combine("Paks")).FullName;
        var pak = ConfigPak.PakPath(paks);
        var sig = Path.ChangeExtension(pak, ".sig");

        Assert.True(ConfigPak.Write(paks, Files(), signed: true));
        Assert.Equal(ConfigPak.Build(Files()), File.ReadAllBytes(pak));
        Assert.Equal(ConfigPak.Signature(File.ReadAllBytes(pak)), File.ReadAllBytes(sig));
        Assert.False(ConfigPak.Write(paks, Files(), signed: true));

        List<(string Name, byte[] Data)> changed = [(ConfigPak.GameIni, Encoding.UTF8.GetBytes("[EpicPurchaseFlow]\r\nProd=\"http:///10.0.0.2:5595/purchase\"\r\n"))];
        Assert.True(ConfigPak.Write(paks, changed, signed: true));
        Assert.Equal(ConfigPak.Build(changed), File.ReadAllBytes(pak));
        Assert.Equal(ConfigPak.Signature(File.ReadAllBytes(pak)), File.ReadAllBytes(sig));
        Assert.Empty(Directory.EnumerateFiles(paks, "*.tmp"));
    }

    [Fact]
    public void A_build_that_signs_no_paks_gets_no_sig()
    {
        using var folder = new TempFolder();
        var paks = Directory.CreateDirectory(folder.Combine("Paks")).FullName;
        Assert.False(ServerAssets.SignsPaks(paks));
        File.WriteAllText(Path.Combine(paks, "pakchunk0-WindowsClient.sig"), "");
        Assert.True(ServerAssets.SignsPaks(paks));

        var sig = Path.ChangeExtension(ConfigPak.PakPath(paks), ".sig");
        Assert.True(ConfigPak.Write(paks, Files(), signed: true));
        Assert.True(File.Exists(sig));
        Assert.True(ConfigPak.Write(paks, Files(), signed: false));
        Assert.False(File.Exists(sig));
        Assert.True(File.Exists(ConfigPak.PakPath(paks)));
    }

    private static string ReadFString(ReadOnlySpan<byte> data, ref int at)
    {
        var length = BitConverter.ToInt32(data[at..(at + 4)]);
        at += 4;
        var text = Encoding.ASCII.GetString(data.Slice(at, length - 1));
        at += length;
        return text;
    }
}
