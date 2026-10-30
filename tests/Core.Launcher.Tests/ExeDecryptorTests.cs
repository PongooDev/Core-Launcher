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



    /// <summary>
    /// The packer appends a blob of code past the real content of every section, so every section has to come back
    /// decrypted and executable. The two exceptions are what the loader owns: the import address table, which it
    /// refills on each launch, and the globals in .data that the stub has already written runtime pointers into.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void Decrypted_191_restores_every_appended_blob_but_not_loader_state()
    {
        var bak = Exe191 + ".bak";
        if (!File.Exists(bak)) return;
        using var folder = new TempFolder();
        var copy = folder.Combine("FortniteClient-Win64-Shipping.exe");
        File.Copy(bak, copy);
        Assert.Equal(ExeDecryptor.Result.Decrypted, ExeDecryptor.Decrypt(copy));

        var before = File.ReadAllBytes(bak);
        var after = File.ReadAllBytes(copy);
        int pe = BinaryPrimitives.ReadInt32LittleEndian(after.AsSpan(0x3C));
        int numSections = BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(pe + 4 + 2));
        int optSize = BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(pe + 4 + 16));
        int opt = pe + 4 + 20;
        int sectOff = opt + optSize;

        uint iatRva = BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(opt + 112 + 12 * 8));
        uint iatSize = BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(opt + 112 + 12 * 8 + 4));
        Assert.True(iatSize > 0, "no IAT data directory to protect");

        const int TailWindow = 0x200;
        int textSeen = 0, blobsSeen = 0;
        for (int i = 0; i < numSections; i++)
        {
            int s = sectOff + i * 40;
            var name = Encoding.ASCII.GetString(after, s, 8).TrimEnd('\0');
            uint rva = BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(s + 12));
            int sz = (int)BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(s + 16));
            int fo = (int)BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(s + 20));
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(s + 36));
            if (sz == 0) continue;

            long diff = 0, tailDiff = 0;
            for (int k = 0; k < sz; k++)
            {
                if (before[fo + k] == after[fo + k]) continue;
                diff++;
                if (k >= sz - TailWindow) tailDiff++;

                // Nothing inside the IAT may be baked in: those slots hold the addresses the
                // throwaway process resolved, which are meaningless to the next launch.
                uint byteRva = rva + (uint)k;
                Assert.False(byteRva >= iatRva && byteRva < iatRva + iatSize,
                    $"{name} rewrote IAT byte at rva 0x{byteRva:X}");
            }

            // The stub hands over with the image PAGE_EXECUTE_READWRITE; without the same access the
            // appended code faults the first time it is executed or patches itself.
            Assert.True((flags & 0x20000000) != 0, $"{name} is not executable (0x{flags:X8})");
            Assert.True((flags & 0x80000000) != 0, $"{name} is not writable (0x{flags:X8})");

            if (name == ".text")
            {
                // Both .text sections are encrypted wholesale: the first holds the game, the second
                // the stub's own code. Only the second ships partly in plaintext, so it alone has no
                // appended blob — the packer has no reason to hide code in the section it decrypts from.
                textSeen++;
                Assert.True(diff > sz / 10, $".text #{textSeen} barely changed ({diff}/{sz}) — not decrypted?");
                continue;
            }

            // Every other section carries a blob of code the packer appended past its real content.
            Assert.True(tailDiff > 0, $"{name} has no decrypted blob at its tail");
            blobsSeen++;

            // .rdata also holds whole blocks of moved code, so it is the only one that may
            // differ away from the end.
            if (name == ".rdata")
                Assert.True(diff > tailDiff, ".rdata has only its tail blob — the moved code is missing");
            else
                Assert.Equal(tailDiff, diff);
        }

        Assert.Equal(2, textSeen);
        Assert.Equal(numSections - 2, blobsSeen);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Decrypted_191_keeps_the_original_import_directory_and_pins_the_base()
    {
        var bak = Exe191 + ".bak";
        if (!File.Exists(bak)) return;
        using var folder = new TempFolder();
        var copy = folder.Combine("FortniteClient-Win64-Shipping.exe");
        File.Copy(bak, copy);
        Assert.Equal(ExeDecryptor.Result.Decrypted, ExeDecryptor.Decrypt(copy));

        var b = File.ReadAllBytes(copy);
        int pe = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(0x3C));
        int opt = pe + 4 + 20;

        // DynamicBase (0x40) must be cleared so the exe loads at its preferred base.
        ushort dllChar = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(opt + 70));
        Assert.True((dllChar & 0x40) == 0, $"DynamicBase still set (0x{dllChar:X4})");

        // The import directory must be left pointing at the original, intact .idata.
        uint importRva = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(opt + 112 + 8));
        uint origImportRva = BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(bak).AsSpan(opt + 112 + 8));
        Assert.Equal(origImportRva, importRva);

        // The entry point keeps the `sub rsp,28 ; call rel32` wrapper FixEntryPoint retargets.
        uint entryRva = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(opt + 16));
        int entryFo = (int)entryRva - 0x1000 + 0x600;
        var entry = b.AsSpan(entryFo, 5);
        Assert.True(entry[0] == 0x48 && entry[1] == 0x83 && entry[2] == 0xEC && entry[3] == 0x28 && entry[4] == 0xE8,
            "Entry wrapper prefix not found");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Decrypted_191_import_names_are_all_intact()
    {
        var bak = Exe191 + ".bak";
        if (!File.Exists(bak)) return;
        File.Copy(bak, Exe191, true);
        using var folder = new TempFolder();
        var copy = folder.Combine("FortniteClient-Win64-Shipping.exe");
        File.Copy(Exe191, copy);
        ExeDecryptor.Decrypt(copy);

        var bytes = File.ReadAllBytes(copy);
        int pe = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C));
        int numSections = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 4 + 2));
        int optSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 4 + 16));
        int opt = pe + 4 + 20;
        uint importRva = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(opt + 120));

        var sects = new List<(string Name, uint Rva, int Size, int FileOff)>();
        int sectOff = opt + optSize;
        for (int i = 0; i < numSections; i++)
        {
            int s = sectOff + i * 40;
            sects.Add((Encoding.ASCII.GetString(bytes, s, 8).TrimEnd('\0'),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s + 12)),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s + 16)),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s + 20))));
        }

        int RvaToOff(uint rva)
        {
            foreach (var s in sects)
                if (rva >= s.Rva && rva < s.Rva + (uint)s.Size)
                    return s.FileOff + (int)(rva - s.Rva);
            return -1;
        }

        // Walk descriptors exactly like the loader: for every by-name ILT entry,
        // the hint/name must be a readable, printable, NUL-terminated string.
        int descOff = RvaToOff(importRva);
        int checkedNames = 0;
        var bad = new List<string>();
        while (true)
        {
            uint oft = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(descOff));
            uint ft = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(descOff + 16));
            if (oft == 0 && ft == 0) break;
            int iltOff = RvaToOff(oft);
            for (int e = 0; ; e++)
            {
                ulong ilt = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(iltOff + e * 8));
                if (ilt == 0) break;
                if ((ilt & 0x8000000000000000) != 0) continue; // ordinal
                int hnOff = RvaToOff((uint)ilt);
                Assert.True(hnOff >= 0, $"hint/name RVA 0x{(uint)ilt:X} not in any section");
                int ns = hnOff + 2;
                int ne = ns;
                while (ne < bytes.Length && bytes[ne] != 0) ne++;
                string name = Encoding.ASCII.GetString(bytes, ns, ne - ns);
                checkedNames++;
                bool ok = name.Length is > 0 and < 512 && name.All(c => c is >= (char)0x20 and <= (char)0x7E);
                if (!ok) bad.Add($"0x{(uint)ilt:X}='{name}'");
            }
            descOff += 20;
        }

        Assert.True(bad.Count == 0, $"{bad.Count}/{checkedNames} corrupted names: {string.Join(", ", bad.Take(10))}");
    }

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
        AssertImportByName(copy, "rand");
        AssertImportByName(copy, "GetUserNameW");
    }

    private static void AssertImportByName(string exe, string function)
    {
        var found = ReadImportFunctions(exe);
        Assert.Contains(function, found);
    }

    private static HashSet<string> ReadImportFunctions(string exe)
    {

        using var fs = new FileStream(exe, FileMode.Open, FileAccess.Read);
        Span<byte> header = stackalloc byte[4096];
        fs.ReadExactly(header);

        int pe = BinaryPrimitives.ReadInt32LittleEndian(header[0x3C..]);
        int coff = pe + 4;
        int numSections = BinaryPrimitives.ReadUInt16LittleEndian(header[(coff + 2)..]);
        int optSize = BinaryPrimitives.ReadUInt16LittleEndian(header[(coff + 16)..]);
        int opt = coff + 20;
        uint importRva = BinaryPrimitives.ReadUInt32LittleEndian(header[(opt + 120)..]);
        Assert.True(importRva != 0, "DataDirectory[1] import RVA is 0");

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

        var found = new HashSet<string>();
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
                            if (funcName.Length > 0)
                                found.Add(funcName);
                        }
                    }
                    iltOff += 8;
                }
            }
            dOff += 20;
        }

        return found;
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
