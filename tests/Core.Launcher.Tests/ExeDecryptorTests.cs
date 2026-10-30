using System.Buffers.Binary;
using System.Text;
using Core.Launcher.Hosting.Builds;

namespace Core.Launcher.Tests;

public sealed class ExeDecryptorTests
{
    [Fact]
    public void Returns_NotEncrypted_for_a_non_pe_file()
    {
        using var folder = new TempFolder();
        var exe = folder.Combine("test.exe");
        File.WriteAllBytes(exe, new byte[1024]);
        Assert.Equal(ExeDecryptor.Result.NotEncrypted, ExeDecryptor.Decrypt(exe));
    }

    [Fact]
    public void Returns_NotEncrypted_for_a_pe_with_one_text_section()
    {
        using var folder = new TempFolder();
        var exe = folder.Combine("test.exe");
        File.WriteAllBytes(exe, SyntheticExe(textSections: 1));
        Assert.Equal(ExeDecryptor.Result.NotEncrypted, ExeDecryptor.Decrypt(exe));
    }

    [Fact]
    public void Returns_AlreadyDecrypted_when_text_has_prologues()
    {
        using var folder = new TempFolder();
        var exe = folder.Combine("test.exe");
        File.WriteAllBytes(exe, SyntheticExe(textSections: 2, prologuesInFirst: true));
        Assert.Equal(ExeDecryptor.Result.AlreadyDecrypted, ExeDecryptor.Decrypt(exe));
    }

    private const string Exe191 = @"F:\Builds\1.9.1\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe";

    [Fact]
    [Trait("Category", "Integration")]
    public void Decrypts_the_191_exe_and_detects_it_on_rerun()
    {
        if (!File.Exists(Exe191))
            return;

        using var folder = new TempFolder();
        var copy = folder.Combine("FortniteClient-Win64-Shipping.exe");
        File.Copy(Exe191, copy);

        var messages = new List<string>();
        var first = ExeDecryptor.Decrypt(copy, messages.Add);
        if (first == ExeDecryptor.Result.Decrypted)
        {
            Assert.True(messages.Count >= 4, $"Expected at least 4 progress messages, got {messages.Count}: [{string.Join(", ", messages)}]");
            Assert.False(File.Exists(copy + ".decrypting.tmp"), "Temp file should be cleaned up");
        }

        Assert.Equal(ExeDecryptor.Result.AlreadyDecrypted, ExeDecryptor.Decrypt(copy));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Decrypted_191_has_rand_in_ILT()
    {
        if (!File.Exists(Exe191))
            return;

        using var folder = new TempFolder();
        var copy = folder.Combine("FortniteClient-Win64-Shipping.exe");
        File.Copy(Exe191, copy);

        var result = ExeDecryptor.Decrypt(copy);
        Assert.True(result is ExeDecryptor.Result.Decrypted or ExeDecryptor.Result.AlreadyDecrypted);

        using var fs = new FileStream(copy, FileMode.Open, FileAccess.Read);
        Span<byte> header = stackalloc byte[4096];
        fs.ReadExactly(header);

        int pe = BinaryPrimitives.ReadInt32LittleEndian(header[0x3C..]);
        int coff = pe + 4;
        int numSections = BinaryPrimitives.ReadUInt16LittleEndian(header[(coff + 2)..]);
        int optSize = BinaryPrimitives.ReadUInt16LittleEndian(header[(coff + 16)..]);
        int opt = coff + 20;
        uint importRva = BinaryPrimitives.ReadUInt32LittleEndian(header[(opt + 120)..]);
        Assert.True(importRva != 0, "DataDirectory[1] import RVA is 0");

        // Find .idata section
        int sectOff = opt + optSize;
        uint idataRva = 0;
        long idataFileOff = 0;
        int idataSize = 0;
        for (int i = 0; i < numSections; i++)
        {
            int s = sectOff + i * 40;
            var name = Encoding.ASCII.GetString(header.Slice(s, 8)).TrimEnd('\0');
            if (name == ".idata")
            {
                idataRva = BinaryPrimitives.ReadUInt32LittleEndian(header[(s + 12)..]);
                idataSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[(s + 16)..]);
                idataFileOff = BinaryPrimitives.ReadUInt32LittleEndian(header[(s + 20)..]);
                break;
            }
        }
        Assert.True(idataRva != 0, ".idata not found");
        Assert.True(importRva >= idataRva && importRva < idataRva + (uint)idataSize,
            $"Import directory RVA 0x{importRva:X} not in .idata (0x{idataRva:X}..0x{idataRva + (uint)idataSize:X})");

        var idata = new byte[idataSize];
        fs.Position = idataFileOff;
        fs.ReadExactly(idata);

        bool foundRand = false;
        int dOff = (int)(importRva - idataRva);
        while (dOff + 20 <= idata.Length)
        {
            uint origFirstThunk = BinaryPrimitives.ReadUInt32LittleEndian(idata.AsSpan(dOff));
            uint firstThunk = BinaryPrimitives.ReadUInt32LittleEndian(idata.AsSpan(dOff + 16));
            if (firstThunk == 0) break;

            Assert.True(origFirstThunk != 0, $"OriginalFirstThunk is 0 for descriptor at .idata+0x{dOff:X}");

            if (origFirstThunk >= idataRva && origFirstThunk < idataRva + (uint)idataSize)
            {
                int iltOff = (int)(origFirstThunk - idataRva);
                while (iltOff + 8 <= idata.Length)
                {
                    ulong entry = BinaryPrimitives.ReadUInt64LittleEndian(idata.AsSpan(iltOff));
                    if (entry == 0) break;
                    if ((entry & 0x8000000000000000) == 0)
                    {
                        uint hnRva = (uint)entry;
                        if (hnRva >= idataRva && hnRva + 4 < idataRva + (uint)idataSize)
                        {
                            int nameStart = (int)(hnRva - idataRva) + 2;
                            int nameEnd = nameStart;
                            while (nameEnd < idata.Length && idata[nameEnd] != 0) nameEnd++;
                            var funcName = Encoding.ASCII.GetString(idata, nameStart, nameEnd - nameStart);
                            if (funcName == "rand")
                                foundRand = true;
                        }
                    }
                    iltOff += 8;
                }
            }
            dOff += 20;
        }

        Assert.True(foundRand, "\"rand\" not found in any ILT entry of the decrypted exe");
    }

    private static byte[] SyntheticExe(int textSections, bool prologuesInFirst = false)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // DOS header
        w.Write((ushort)0x5A4D);
        w.Write(new byte[0x3A]);
        w.Write(0x80);
        w.Write(new byte[0x80 - 0x40]);

        // PE signature
        w.Write(0x00004550u);

        // COFF header
        w.Write((ushort)0x8664);
        w.Write((ushort)textSections);
        w.Write(new byte[12]);
        w.Write((ushort)240);
        w.Write((ushort)0x22);

        // Optional header (PE32+, 240 bytes)
        long optStart = ms.Position;
        w.Write((ushort)0x20B);
        w.Write(new byte[14]);
        w.Write(0x1000u);
        w.Write(new byte[4]);
        w.Write(0x0000000140000000L);
        w.Write(new byte[38]);
        w.Write((ushort)0x8160);
        w.Write(new byte[240 - (int)(ms.Position - optStart)]);

        // Section headers
        int dataStart = ((int)ms.Position + textSections * 40 + 0x1FF) & ~0x1FF;
        const int textSize = 4096;

        for (int i = 0; i < textSections; i++)
        {
            w.Write(".text\0\0\0"u8.ToArray());
            w.Write((uint)textSize);
            w.Write((uint)(0x1000 + i * 0x10000));
            w.Write((uint)textSize);
            w.Write((uint)(dataStart + i * textSize));
            w.Write(new byte[12]);
            w.Write(0x60000020u);
        }

        while (ms.Position < dataStart) w.Write((byte)0);

        // .text data
        for (int i = 0; i < textSections; i++)
        {
            if (i == 0 && prologuesInFirst)
            {
                for (int j = 0; j < textSize / 8; j++)
                    w.Write(new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83 });
            }
            else
            {
                w.Write(new byte[textSize]);
            }
        }

        return ms.ToArray();
    }
}
