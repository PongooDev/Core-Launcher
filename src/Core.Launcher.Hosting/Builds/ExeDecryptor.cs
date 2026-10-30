using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Core.Launcher.Hosting.Native;
using Microsoft.Win32.SafeHandles;

namespace Core.Launcher.Hosting.Builds;

/// <summary>
/// Decrypts builds 1.9.1 and up, whose shipping exe is packed. A stub in a second .text section decrypts the image at
/// startup and only then jumps to the real entry point, so it would overwrite the patches Core applies before the
/// entry point runs. This runs the stub in a throwaway process, snapshots the decrypted image, writes it back to disk
/// and points the entry point past the stub, so the real launch never runs the stub at all.
/// </summary>
public static class ExeDecryptor
{
    public enum Result
    {
        Decrypted,
        AlreadyDecrypted,
        /// <summary>The exe has only one .text section: no stub, not packed.</summary>
        NotEncrypted,
    }

    private static readonly byte[] ProloguePattern = [0x48, 0x89, 0x5C, 0x24];

    private static readonly byte[] CrtPrologue =
        [0x48, 0x89, 0x5C, 0x24, 0x20, 0x55, 0x48, 0x8B, 0xEC, 0x48, 0x83, 0xEC, 0x20];

    private static readonly byte[] EntryPrefix = [0x48, 0x83, 0xEC, 0x28, 0xE8];

    private const int PrologueThreshold = 8;
    private const int SampleSize = 65536;
    private const int PollIntervalMs = 200;
    private const int TimeoutMs = 30_000;
    private const ushort DynamicBase = 0x0040;
    private const uint SectionExecute = 0x20000000;
    private const uint SectionWrite = 0x80000000;

    /// <summary>
    /// How far apart two changed bytes may be and still count as one blob. The code the packer appends is dense, so
    /// a gap this size never falls inside one; a smaller value would split a blob at its longest run of zeroes.
    /// </summary>
    private const int BlobGap = 64;

    private const string TextSection = ".text";

    /// <summary>
    /// How much of a trailing run must have changed for it to be the packer's appended code rather than runtime
    /// state. Its code comes out 80-99% different from the encrypted bytes it replaces, while the stub and the
    /// loader writing pointers into globals changes only the few bytes of each that are not already right.
    /// </summary>
    private const double BlobDensity = 0.5;

    public static Result Decrypt(string executable, Action<string>? progress = null)
    {
        if (ParsePe(executable) is not { } pe)
            return Result.NotEncrypted;

        if (LooksDecrypted(executable, pe.Sections[pe.FirstTextIndex]))
            return Result.AlreadyDecrypted;

        progress?.Invoke("Packed exe detected, decrypting");
        var live = DumpDecryptedImage(executable, pe, progress);

        progress?.Invoke("Writing the decrypted image back to the exe");
        long written = 0;
        var code = new List<(Section Section, byte[] Bytes)>();
        using (var fs = new FileStream(executable, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            for (int i = 0; i < pe.Sections.Length; i++)
            {
                var section = pe.Sections[i];
                var merged = new byte[section.RawSize];
                fs.Position = section.FileOffset;
                fs.ReadExactly(merged);

                if (i == pe.FirstTextIndex)
                    FixEntryPoint(live[i], pe, section);

                int changed = MergeDecrypted(merged, live[i], section, pe);
                if (section.Name == TextSection)
                    code.Add((section, merged));
                if (changed == 0)
                    continue;

                fs.Position = section.FileOffset;
                fs.Write(merged);
                written += changed;

                // The stub hands over with the whole image PAGE_EXECUTE_READWRITE: it runs threads out of the
                // code it appended, and that code patches a few bytes of .text back into itself. Standing in for
                // the stub means granting the same access, or the loader maps these sections read-only and the
                // first such execute or write faults.
                MarkRunnable(fs, section);
            }

            written += UnfixAddendSlots(fs, pe, code, progress);

            // Clear IMAGE_DLLCHARACTERISTICS_DYNAMIC_BASE so the exe loads at its preferred image base. The
            // decrypted code holds absolute addresses valid only for that base; the stub would normally re-apply
            // relocations after decrypting, but we bypass it, so we must pin the base instead.
            fs.Position = pe.DllCharacteristicsOffset;
            Span<byte> dllChar = stackalloc byte[2];
            fs.ReadExactly(dllChar);
            ushort chars = BinaryPrimitives.ReadUInt16LittleEndian(dllChar);
            BinaryPrimitives.WriteUInt16LittleEndian(dllChar, (ushort)(chars & ~DynamicBase));
            fs.Position = pe.DllCharacteristicsOffset;
            fs.Write(dllChar);
        }

        progress?.Invoke($"Decrypted {written / (1024 * 1024)}MB");
        return Result.Decrypted;
    }

    private readonly record struct Section(
        string Name, long FileOffset, uint Rva, int RawSize, long HeaderOffset, uint Characteristics)
    {
        public bool Writable => (Characteristics & SectionWrite) != 0;
    }

    private readonly record struct PeInfo(
        Section[] Sections, int FirstTextIndex,
        uint EntryPointRva, long ImageBase, uint SizeOfImage,
        long DllCharacteristicsOffset, uint IatRva, uint IatSize,
        uint ExceptionRva, uint ExceptionSize)
    {
        public long FileOffsetOf(uint rva)
        {
            foreach (var section in Sections)
                if (rva >= section.Rva && rva < section.Rva + section.RawSize)
                    return section.FileOffset + (rva - section.Rva);
            return -1;
        }
    }

    private static PeInfo? ParsePe(string executable)
    {
        Span<byte> header = stackalloc byte[4096];
        int read;
        using (var fs = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            read = fs.Read(header);
        }

        if (read < 0x40) return null;
        header = header[..read];

        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(header[0x3C..]);
        if (peOffset < 0 || peOffset + 4 > read) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[peOffset..]) != 0x00004550) return null;

        int coffOffset = peOffset + 4;
        if (coffOffset + 20 > read) return null;
        int numberOfSections = BinaryPrimitives.ReadUInt16LittleEndian(header[(coffOffset + 2)..]);
        int sizeOfOptionalHeader = BinaryPrimitives.ReadUInt16LittleEndian(header[(coffOffset + 16)..]);

        int optOffset = coffOffset + 20;
        if (optOffset + 112 > read) return null;
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[optOffset..]) != 0x20B) return null;

        uint entryPointRva = BinaryPrimitives.ReadUInt32LittleEndian(header[(optOffset + 16)..]);
        long imageBase = BinaryPrimitives.ReadInt64LittleEndian(header[(optOffset + 24)..]);
        uint sizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(header[(optOffset + 56)..]);
        long dllCharOffset = optOffset + 70;

        // DataDirectory[3] is the exception directory: the RUNTIME_FUNCTION table, which says
        // exactly where every real function starts and ends.
        int exceptionDirOffset = optOffset + 112 + 3 * 8;
        uint exceptionRva = BinaryPrimitives.ReadUInt32LittleEndian(header[exceptionDirOffset..]);
        uint exceptionSize = BinaryPrimitives.ReadUInt32LittleEndian(header[(exceptionDirOffset + 4)..]);

        // DataDirectory[12] is the import address table: the one part of the image the Windows loader rewrites on
        // every launch, so it is the one part we must not take from the throwaway process.
        int iatDirOffset = optOffset + 112 + 12 * 8;
        if (iatDirOffset + 8 > read) return null;
        uint iatRva = BinaryPrimitives.ReadUInt32LittleEndian(header[iatDirOffset..]);
        uint iatSize = BinaryPrimitives.ReadUInt32LittleEndian(header[(iatDirOffset + 4)..]);

        int sectionsOffset = optOffset + sizeOfOptionalHeader;
        var sections = new List<Section>();
        int textCount = 0, firstTextIndex = -1;
        ReadOnlySpan<byte> textName = ".text\0\0\0"u8;

        for (int i = 0; i < numberOfSections; i++)
        {
            int off = sectionsOffset + i * 40;
            if (off + 40 > read) break;

            var nameSpan = header.Slice(off, 8);
            int rawSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[(off + 16)..]);
            if (rawSize <= 0) continue;

            if (nameSpan.SequenceEqual(textName) && ++textCount == 1)
                firstTextIndex = sections.Count;

            sections.Add(new Section(
                Encoding.ASCII.GetString(nameSpan).TrimEnd('\0'),
                BinaryPrimitives.ReadUInt32LittleEndian(header[(off + 20)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(header[(off + 12)..]),
                rawSize,
                off,
                BinaryPrimitives.ReadUInt32LittleEndian(header[(off + 36)..])));
        }

        return textCount >= 2
            ? new PeInfo(sections.ToArray(), firstTextIndex, entryPointRva, imageBase, sizeOfImage,
                dllCharOffset, iatRva, iatSize, exceptionRva, exceptionSize)
            : null;
    }

    private static bool LooksDecrypted(string executable, Section firstText)
    {
        int length = Math.Min(SampleSize, firstText.RawSize);
        int offset = Math.Max(0, Math.Min(0x100000, firstText.RawSize - length));

        var sample = new byte[length];
        using (var fs = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            fs.Position = firstText.FileOffset + offset;
            fs.ReadExactly(sample);
        }

        return CountPrologues(sample) >= PrologueThreshold;
    }

    private static int CountPrologues(ReadOnlySpan<byte> data)
    {
        int count = 0;
        var remaining = data;
        while (true)
        {
            int idx = remaining.IndexOf(ProloguePattern);
            if (idx < 0) break;
            count++;
            remaining = remaining[(idx + 1)..];
        }
        return count;
    }

    /// <summary>
    /// Overlays what the stub decrypted onto the section's on-disk bytes and returns how many of them changed. Two
    /// kinds of difference are deliberately left behind: the import address table, which the loader fills in afresh
    /// on every launch, and — in a writable section — everything before the blob the packer appended at the very
    /// end, because a writable section also holds globals the stub and the loader have already written to.
    /// </summary>
    private static int MergeDecrypted(byte[] onDisk, byte[] decrypted, Section section, PeInfo pe)
    {
        int from = 0, to = onDisk.Length;
        if (section.Writable)
        {
            if (TrailingBlob(onDisk, decrypted) is not { } blob) return 0;
            (from, to) = blob;
        }

        long iatFrom = (long)pe.IatRva - section.Rva;
        long iatTo = iatFrom + pe.IatSize;

        int changed = 0;
        for (int i = from; i < to; i++)
        {
            if (onDisk[i] == decrypted[i]) continue;
            if (i >= iatFrom && i < iatTo) continue;
            onDisk[i] = decrypted[i];
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// Finds the dense run of changed bytes reaching the end of a section — the code the packer appended past the
    /// section's real content, which every section in a packed build carries — or null if the section ends in
    /// nothing but unchanged bytes or scattered runtime writes.
    /// </summary>
    private static (int From, int To)? TrailingBlob(byte[] onDisk, byte[] decrypted)
    {
        int to = onDisk.Length;
        while (to > 0 && onDisk[to - 1] == decrypted[to - 1]) to--;
        if (to == 0) return null;

        int from = to, gap = 0;
        while (from > 0 && gap <= BlobGap)
        {
            from--;
            gap = onDisk[from] == decrypted[from] ? gap + 1 : 0;
        }

        int changed = 0;
        for (int i = from; i < to; i++)
            if (onDisk[i] != decrypted[i])
                changed++;

        return changed >= (to - from) * BlobDensity ? (from, to) : null;
    }

    /// <summary>
    /// Puts back the addend slots the stub converted from RVAs to absolute addresses.
    /// <para>
    /// The packer keeps a few qwords in the gaps between functions and adds the image base to them
    /// at use time: <c>add rax, cs:qword_7F1A18</c> then an indirect jump. Its stub also converts
    /// those slots to absolute as it decrypts, so a snapshot taken at the handoff has both halves
    /// applied; the image base then goes on a second time at runtime and the jump lands one image
    /// base too high, in unmapped memory. Subtracting the base restores what the use site expects.
    /// </para>
    /// <para>
    /// Slots are found by how they are used, not by what they hold: a slot that merely contains an
    /// image address may be the packer's own copy of the image base, which has to stay absolute.
    /// On 1.9.1 exactly one slot in the image is the target of such an add.
    /// </para>
    /// </summary>
    private static long UnfixAddendSlots(FileStream fs, PeInfo pe,
        List<(Section Section, byte[] Bytes)> code, Action<string>? progress)
    {
        var functions = ReadFunctionRanges(fs, pe);
        if (functions.Length == 0) return 0;

        Span<byte> qword = stackalloc byte[8];
        long fixedUp = 0;
        foreach (var (section, bytes) in code)
        {
            foreach (uint target in AddendTargets(bytes, section.Rva, pe, functions))
            {
                long offset = pe.FileOffsetOf(target);
                if (offset < 0) continue;

                fs.Position = offset;
                fs.ReadExactly(qword);
                ulong value = BinaryPrimitives.ReadUInt64LittleEndian(qword);
                if (value <= (ulong)pe.ImageBase || value >= (ulong)pe.ImageBase + pe.SizeOfImage)
                    continue;

                BinaryPrimitives.WriteUInt64LittleEndian(qword, value - (ulong)pe.ImageBase);
                fs.Position = offset;
                fs.Write(qword);
                fixedUp += 8;
                progress?.Invoke($"Restored the addend at RVA 0x{target:X} to 0x{value - (ulong)pe.ImageBase:X}");
            }
        }
        return fixedUp;
    }

    /// <summary>Every <c>add r64, [rip+disp32]</c> target that sits outside all real functions.</summary>
    private static IEnumerable<uint> AddendTargets(byte[] bytes, uint sectionRva, PeInfo pe,
        (uint Begin, uint End)[] functions)
    {
        for (int i = 0; i < bytes.Length - 7; i++)
        {
            // REX.W (48, or 4C for r8-r15) + 03 /r with mod=00 rm=101 is add r64, [rip+disp32].
            if ((bytes[i] != 0x48 && bytes[i] != 0x4C) || bytes[i + 1] != 0x03) continue;
            if ((bytes[i + 2] & 0xC7) != 0x05) continue;

            int displacement = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i + 3));
            long target = sectionRva + i + 7L + displacement;
            if (target <= 0 || target >= pe.SizeOfImage || target % 8 != 0) continue;
            if (InFunction(functions, (uint)target)) continue;

            yield return (uint)target;
        }
    }

    private static bool InFunction((uint Begin, uint End)[] functions, uint rva)
    {
        int low = 0, high = functions.Length - 1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (rva < functions[mid].Begin) high = mid - 1;
            else if (rva >= functions[mid].End) low = mid + 1;
            else return true;
        }
        return false;
    }

    private static (uint Begin, uint End)[] ReadFunctionRanges(FileStream fs, PeInfo pe)
    {
        long offset = pe.FileOffsetOf(pe.ExceptionRva);
        if (offset < 0 || pe.ExceptionSize == 0) return [];

        var table = new byte[pe.ExceptionSize];
        fs.Position = offset;
        fs.ReadExactly(table);

        var functions = new List<(uint, uint)>(table.Length / 12);
        for (int i = 0; i + 12 <= table.Length; i += 12)
        {
            uint begin = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i));
            uint end = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i + 4));
            if (end > begin) functions.Add((begin, end));
        }

        var ranges = functions.ToArray();
        Array.Sort(ranges, (a, b) => a.Item1.CompareTo(b.Item1));
        return ranges;
    }

    private static void MarkRunnable(FileStream fs, Section section)
    {
        uint wanted = section.Characteristics | SectionExecute | SectionWrite;
        if (wanted == section.Characteristics) return;

        Span<byte> dword = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(dword, wanted);
        fs.Position = section.HeaderOffset + 36;
        fs.Write(dword);
    }

    private static unsafe Dictionary<int, byte[]> DumpDecryptedImage(
        string executable, PeInfo pe, Action<string>? progress)
    {
        var tmpPath = executable + ".decrypting.tmp";
        try
        {
            progress?.Invoke("Copying exe to a temporary file");
            File.Copy(executable, tmpPath, overwrite: true);

            using (var fs = new FileStream(tmpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                fs.Position = pe.DllCharacteristicsOffset;
                Span<byte> buf = stackalloc byte[2];
                fs.ReadExactly(buf);
                ushort chars = BinaryPrimitives.ReadUInt16LittleEndian(buf);
                BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)(chars & ~DynamicBase));
                fs.Position = pe.DllCharacteristicsOffset;
                fs.Write(buf);
            }

            var si = new Kernel32.STARTUPINFOW { cb = sizeof(Kernel32.STARTUPINFOW) };
            // -epicportal stops the game relaunching itself through the Epic Games Launcher (which it does when it
            // thinks it was started outside the portal), and -nullrhi/-nosplash/-nosound keep it from opening a
            // window or playing audio during the moment it runs to decrypt itself. It is killed straight after.
            var cmdLine = $"\"{tmpPath}\" -epicportal -nullrhi -nosplash -nosound".ToCharArray();

            fixed (char* pCmd = cmdLine)
            {
                if (!Kernel32.CreateProcess(tmpPath, pCmd, 0, 0, false, Kernel32.CREATE_SUSPENDED, 0,
                        Path.GetDirectoryName(tmpPath)!, in si, out var pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                using var process = new SafeProcessHandle(pi.hProcess, true);
                using var thread = new SafeKernelHandle(pi.hThread);

                try
                {
                    if (Kernel32.ResumeThread(thread) == uint.MaxValue)
                        throw new Win32Exception(Marshal.GetLastWin32Error());

                    WaitForTheStub(process, pe, progress);

                    // The stub decrypts the entry wrapper last, so once it is readable the image is done. A short
                    // settle, then suspend so nothing moves while we read it.
                    progress?.Invoke("Letting the decryption settle");
                    Thread.Sleep(300);
                    if (Kernel32.SuspendThread(thread) == uint.MaxValue)
                        throw new Win32Exception(Marshal.GetLastWin32Error());

                    progress?.Invoke("Reading the decrypted image");
                    var live = new Dictionary<int, byte[]>();
                    for (int i = 0; i < pe.Sections.Length; i++)
                    {
                        var section = pe.Sections[i];
                        var buf = new byte[section.RawSize];
                        fixed (byte* pBuf = buf)
                        {
                            if (!Kernel32.ReadProcessMemory(process, (nint)(pe.ImageBase + section.Rva), pBuf,
                                    (nuint)section.RawSize, out var readN))
                                throw new Win32Exception(Marshal.GetLastWin32Error());
                            if (readN != (nuint)section.RawSize)
                                throw new InvalidOperationException(
                                    $"ReadProcessMemory returned {readN} of {section.RawSize} {section.Name} bytes");
                        }
                        live[i] = buf;
                    }

                    return live;
                }
                finally
                {
                    Kernel32.TerminateProcess(process, 1);
                    Kernel32.WaitForSingleObject(process, 5000);
                }
            }
        }
        finally
        {
            try { File.Delete(tmpPath); } catch { }
        }
    }

    /// <summary>
    /// Waits until the stub has decrypted the entry wrapper. The wrapper is
    /// <c>sub rsp,28 / call stub / add rsp,28 / jmp __scrt_common_main_seh</c>, and the stub only decrypts the half
    /// after the call shortly before it transfers back, which makes those bytes a reliable "image is ready" signal.
    /// </summary>
    private static unsafe void WaitForTheStub(SafeProcessHandle process, PeInfo pe, Action<string>? progress)
    {
        progress?.Invoke("Waiting for the stub to decrypt the image");
        nint entryAddress = (nint)(pe.ImageBase + pe.EntryPointRva);
        var probe = new byte[22];
        long deadline = Environment.TickCount64 + TimeoutMs;

        while (true)
        {
            Thread.Sleep(PollIntervalMs);

            if (Kernel32.GetExitCodeProcess(process, out var exitCode) && exitCode != 259)
                throw new InvalidOperationException($"Process exited (0x{exitCode:X}) before decryption completed");

            fixed (byte* pBuf = probe)
            {
                if (Kernel32.ReadProcessMemory(process, entryAddress, pBuf, (nuint)probe.Length, out _)
                    && probe[9] == 0x48 && probe[10] == 0x83 && probe[11] == 0xC4 && probe[12] == 0x28
                    && probe[13] == 0xE9)
                    return;
            }

            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException($"The entry wrapper was not decrypted within {TimeoutMs / 1000}s");
        }
    }

    /// <summary>
    /// Repoints the entry wrapper's call from the stub to __scrt_common_main_seh, so the real launch starts the CRT
    /// directly and the stub never runs.
    /// </summary>
    private static void FixEntryPoint(byte[] decryptedText, PeInfo pe, Section text)
    {
        int entryOffset = (int)(pe.EntryPointRva - text.Rva);
        if (entryOffset < 0 || entryOffset + 9 > decryptedText.Length)
            throw new InvalidOperationException("Entry point is outside the .text section");

        if (!decryptedText.AsSpan(entryOffset, EntryPrefix.Length).SequenceEqual(EntryPrefix))
            throw new InvalidOperationException("Entry point does not match the expected pattern (48 83 EC 28 E8)");

        int searchEnd = Math.Min(decryptedText.Length, entryOffset + 0x10000);
        int idx = decryptedText.AsSpan(entryOffset, searchEnd - entryOffset).IndexOf(CrtPrologue);
        if (idx < 0)
            throw new InvalidOperationException("Could not find __scrt_common_main_seh near the entry point");

        int crtOffset = entryOffset + idx;
        BinaryPrimitives.WriteInt32LittleEndian(decryptedText.AsSpan(entryOffset + 5), crtOffset - (entryOffset + 9));
    }
}
