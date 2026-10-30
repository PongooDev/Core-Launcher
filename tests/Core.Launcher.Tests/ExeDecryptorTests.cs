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
            // The section the decryptor appends for the rebuilt import directory is its own, not the
            // packer's: it has no blob to restore and holds nothing that runs.
            if (fo >= before.Length) continue;

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
        Assert.Equal(numSections - 3, blobsSeen);
    }

    /// <summary>
    /// The packer adds the image base to a few qwords in the gaps between functions, and its stub
    /// also converts them to absolute while decrypting. Both halves end up applied in a snapshot, so
    /// the decryptor has to subtract the base back off, or the indirect jump lands one image base
    /// high in unmapped memory. On 1.9.1 exactly one slot in the image is used this way.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void Decrypted_191_leaves_no_addend_slot_holding_an_absolute_address()
    {
        var bak = Exe191 + ".bak";
        if (!File.Exists(bak)) return;
        using var folder = new TempFolder();
        var copy = folder.Combine("FortniteClient-Win64-Shipping.exe");
        File.Copy(bak, copy);
        Assert.Equal(ExeDecryptor.Result.Decrypted, ExeDecryptor.Decrypt(copy));

        var b = File.ReadAllBytes(copy);
        int pe = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(0x3C));
        int numSections = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(pe + 4 + 2));
        int optSize = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(pe + 4 + 16));
        int opt = pe + 4 + 20;
        ulong imageBase = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(opt + 24));
        uint sizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(opt + 56));
        uint pdataRva = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(opt + 112 + 3 * 8));
        uint pdataSize = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(opt + 112 + 3 * 8 + 4));

        var sections = new List<(string Name, uint Rva, int Size, int FileOff)>();
        for (int i = 0; i < numSections; i++)
        {
            int s = opt + optSize + i * 40;
            sections.Add((Encoding.ASCII.GetString(b, s, 8).TrimEnd('\0'),
                BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(s + 12)),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(s + 16)),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(s + 20))));
        }

        int Offset(uint rva)
        {
            foreach (var s in sections)
                if (rva >= s.Rva && rva < s.Rva + (uint)s.Size)
                    return s.FileOff + (int)(rva - s.Rva);
            return -1;
        }

        var functions = new List<(uint Begin, uint End)>();
        for (int i = Offset(pdataRva); i + 12 <= Offset(pdataRva) + pdataSize; i += 12)
        {
            uint begin = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i));
            uint end = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i + 4));
            if (end > begin) functions.Add((begin, end));
        }
        functions.Sort((x, y) => x.Begin.CompareTo(y.Begin));
        bool InFunction(uint rva) => functions.BinarySearch((rva, rva),
            Comparer<(uint Begin, uint End)>.Create((x, y) => x.Begin <= y.Begin && y.Begin < x.End ? 0
                : x.Begin.CompareTo(y.Begin))) >= 0;

        int found = 0;
        foreach (var s in sections.Where(s => s.Name == ".text"))
        {
            for (int i = 0; i < s.Size - 7; i++)
            {
                int at = s.FileOff + i;
                if ((b[at] != 0x48 && b[at] != 0x4C) || b[at + 1] != 0x03) continue;
                if ((b[at + 2] & 0xC7) != 0x05) continue;

                int disp = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at + 3));
                long target = s.Rva + i + 7L + disp;
                if (target <= 0 || target >= sizeOfImage || target % 8 != 0) continue;
                if (InFunction((uint)target)) continue;

                int off = Offset((uint)target);
                if (off < 0) continue;
                ulong value = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(off));
                if (value <= imageBase || value >= imageBase + sizeOfImage) continue;

                Assert.Fail($"addend slot at rva 0x{target:X} still holds the absolute 0x{value:X}; "
                            + $"adding the image base again gives 0x{value + imageBase:X}");
                found++;
            }
        }

        // The scan must still be finding the construct: if it silently matches nothing the exe
        // would pass this test while the crash came back.
        var packed = File.ReadAllBytes(bak);
        Assert.NotEqual(
            BinaryPrimitives.ReadUInt64LittleEndian(packed.AsSpan(Offset(0x7F1A18))),
            BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(Offset(0x7F1A18))));
        Assert.Equal(0x12B4030UL, BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(Offset(0x7F1A18))));
        _ = found;
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

        // The import directory moves into the appended section, because the rebuilt descriptors for the
        // stub's private table are added to it -- but the original ones have to come along, or the exe
        // loses the imports it already declared.
        uint importRva = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(opt + 112 + 8));
        uint origImportRva = BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(bak).AsSpan(opt + 112 + 8));
        Assert.NotEqual(origImportRva, importRva);

        var (dlls, _, thunks) = ReadImports(copy);
        foreach (var original in new[] { "KERNEL32.dll", "USER32.dll", "WS2_32.dll", "ole32.dll" })
            Assert.Contains(original, dlls);

        // And the rebuilt descriptors must write into the stub's table, not somewhere of their own.
        Assert.Contains(thunks, rva => rva is >= 0x400E000 and < 0x400F930);

        // A bound import directory would describe the descriptor list we just replaced.
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(opt + 112 + 11 * 8)));

        // The entry point keeps the `sub rsp,28 ; call rel32` wrapper FixEntryPoint retargets.
        uint entryRva = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(opt + 16));
        int entryFo = (int)entryRva - 0x1000 + 0x600;
        var entry = b.AsSpan(entryFo, 5);
        Assert.True(entry[0] == 0x48 && entry[1] == 0x83 && entry[2] == 0xEC && entry[3] == 0x28 && entry[4] == 0xE8,
            "Entry wrapper prefix not found");
    }

    /// <summary>
    /// The packer takes the game's imports out of the import directory and has its stub resolve them into
    /// a private table, so a bypassed launch would run on whatever addresses the decryption process
    /// happened to see. Those are only right for that process -- on 1.9.1, 84 of the 762 came out stale --
    /// so the decryptor rebuilds the table as a real import directory for the loader to fill each launch.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void Decrypted_191_rebuilds_the_stubs_private_import_table()
    {
        var bak = Exe191 + ".bak";
        if (!File.Exists(bak)) return;
        using var folder = new TempFolder();
        var copy = folder.Combine("FortniteClient-Win64-Shipping.exe");
        File.Copy(bak, copy);
        Assert.Equal(ExeDecryptor.Result.Decrypted, ExeDecryptor.Decrypt(copy));

        var (dlls, functions, thunks) = ReadImports(copy);

        // Imports the game needs that the packed exe does not declare: they only existed in the
        // stub's table, and nothing would resolve them if the rebuild had not run.
        foreach (var hidden in new[] { "rand", "GetUserNameW", "memcpy", "RegCloseKey" })
            Assert.Contains(hidden, functions);
        foreach (var module in new[] { "ucrtbase.dll", "MSVCP140.dll", "VCRUNTIME140.dll" })
            Assert.Contains(module, dlls);

        // Every slot of the table must be covered, so the loader leaves none holding a captured address.
        var covered = thunks.Where(rva => rva is >= 0x400E000 and < 0x400F930).ToHashSet();
        Assert.True(covered.Count >= 700, $"only {covered.Count} of the table's slots are imported");

        // No slot may keep the address the decryption process resolved: those point into modules that
        // move, so a baked one is a call into whatever is mapped there next launch.
        var b = File.ReadAllBytes(copy);
        int pe = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(0x3C));
        int opt = pe + 4 + 20;
        int optSize = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(pe + 4 + 16));
        int numSections = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(pe + 4 + 2));
        for (int i = 0; i < numSections; i++)
        {
            int s = opt + optSize + i * 40;
            uint rva = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(s + 12));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(s + 16));
            int fo = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(s + 20));
            if (rva > 0x400E000 || rva + (uint)size <= 0x400E000) continue;

            for (uint at = 0x400E000; at < 0x400F930; at += 8)
            {
                ulong value = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(fo + (int)(at - rva)));
                Assert.False(value > 0x7FF000000000,
                    $"the table at rva 0x{at:X} still holds the captured address 0x{value:X}");
            }
        }
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

    /// <summary>
    /// Walks the import directory the way the loader does, wherever it lives. The decryptor moves it
    /// into the section it appends, so nothing here may assume .idata.
    /// </summary>
    private static HashSet<string> ReadImportFunctions(string exe) => ReadImports(exe).Functions;

    private static (HashSet<string> Dlls, HashSet<string> Functions, List<uint> FirstThunks) ReadImports(string exe)
    {
        var b = File.ReadAllBytes(exe);
        int pe = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(0x3C));
        int coff = pe + 4;
        int numSections = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(coff + 2));
        int optSize = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(coff + 16));
        int opt = coff + 20;
        uint importRva = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(opt + 120));
        Assert.True(importRva != 0, "DataDirectory[1] import RVA is 0");

        var sections = new List<(uint Rva, int Size, int FileOff)>();
        for (int i = 0; i < numSections; i++)
        {
            int s = opt + optSize + i * 40;
            sections.Add((BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(s + 12)),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(s + 16)),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(s + 20))));
        }

        int Offset(uint rva)
        {
            foreach (var s in sections)
                if (rva >= s.Rva && rva < s.Rva + (uint)s.Size)
                    return s.FileOff + (int)(rva - s.Rva);
            return -1;
        }

        string? Text(uint rva)
        {
            int at = Offset(rva);
            if (at < 0) return null;
            int end = Array.IndexOf(b, (byte)0, at);
            return end > at ? Encoding.ASCII.GetString(b, at, end - at) : null;
        }

        var dlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var functions = new HashSet<string>();
        var firstThunks = new List<uint>();
        for (int d = Offset(importRva); ; d += 20)
        {
            uint originalFirstThunk = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(d));
            uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(d + 12));
            uint firstThunk = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(d + 16));
            if (originalFirstThunk == 0 && firstThunk == 0 && nameRva == 0) break;

            Assert.True(originalFirstThunk != 0, $"OriginalFirstThunk is 0 for the descriptor at 0x{d:X}");
            if (Text(nameRva) is { } dll) dlls.Add(dll);

            for (int lookup = Offset(originalFirstThunk); ; lookup += 8)
            {
                ulong entry = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(lookup));
                if (entry == 0) break;
                if ((entry & 0x8000000000000000) == 0 && Text((uint)entry + 2) is { } function)
                    functions.Add(function);
                firstThunks.Add(firstThunk + (uint)(lookup - Offset(originalFirstThunk)));
            }
        }

        return (dlls, functions, firstThunks);
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
