using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Core.Launcher.Hosting.Native;
using Microsoft.Win32.SafeHandles;

namespace Core.Launcher.Hosting.Builds;

/// <summary>
/// Decrypts builds 1.9.1 and up whose shipping exe ships with encrypted sections. A stub in a second .text section
/// decrypts the image at startup; this lets the stub run in a throwaway process, reads every affected section back,
/// and writes them to disk so the exe runs without the stub and Core's patches survive.
/// </summary>
public static class ExeDecryptor
{
    public enum Result
    {
        Decrypted,
        AlreadyDecrypted,
        /// <summary>The exe has only one .text section: no stub, not encrypted.</summary>
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

    public static Result Decrypt(string executable, Action<string>? progress = null)
    {
        if (ParsePe(executable) is not { } pe)
            return Result.NotEncrypted;

        int sampleLength = Math.Min(SampleSize, pe.TextRawSize);
        int sampleOffset = Math.Min(0x100000, pe.TextRawSize - sampleLength);
        if (sampleOffset < 0) sampleOffset = 0;

        var sample = new byte[sampleLength];
        using (var fs = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            fs.Position = pe.TextFileOffset + sampleOffset;
            fs.ReadExactly(sample);
        }

        if (CountPrologues(sample) >= PrologueThreshold)
            return Result.AlreadyDecrypted;

        progress?.Invoke("Encrypted .text section detected, decrypting");
        var (text, sectionData) = DumpSections(executable, pe, progress);
        FixEntryPoint(text, pe);

        long totalSize = text.Length;
        foreach (var data in sectionData)
            totalSize += data.Length;
        progress?.Invoke($"Writing {totalSize / (1024 * 1024)}MB of decrypted sections back to the exe");
        using (var fs = new FileStream(executable, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            fs.Position = pe.TextFileOffset;
            fs.Write(text);
            for (int i = 0; i < pe.Sections.Length; i++)
            {
                fs.Position = pe.Sections[i].FileOffset;
                fs.Write(sectionData[i]);
            }
        }

        return Result.Decrypted;
    }

    private readonly record struct SectionToDump(long FileOffset, uint Rva, int RawSize);

    private readonly record struct PeInfo(
        long TextFileOffset, uint TextRva, int TextRawSize,
        SectionToDump[] Sections,
        uint EntryPointRva, long ImageBase, long DllCharacteristicsOffset);

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
        if (optOffset + 72 > read) return null;
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[optOffset..]) != 0x20B) return null;

        uint entryPointRva = BinaryPrimitives.ReadUInt32LittleEndian(header[(optOffset + 16)..]);
        long imageBase = BinaryPrimitives.ReadInt64LittleEndian(header[(optOffset + 24)..]);
        long dllCharOffset = optOffset + 70;

        int sectionsOffset = optOffset + sizeOfOptionalHeader;
        int textCount = 0;
        uint firstTextRva = 0;
        int firstTextRawSize = 0;
        long firstTextFileOffset = 0;
        var sections = new List<SectionToDump>();
        ReadOnlySpan<byte> textName = ".text\0\0\0"u8;
        ReadOnlySpan<byte> relocName = ".reloc\0\0"u8;
        ReadOnlySpan<byte> pdataName = ".pdata\0\0"u8;

        for (int i = 0; i < numberOfSections; i++)
        {
            int off = sectionsOffset + i * 40;
            if (off + 40 > read) break;
            var name = header.Slice(off, 8);
            uint rva = BinaryPrimitives.ReadUInt32LittleEndian(header[(off + 12)..]);
            int rawSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[(off + 16)..]);
            long fileOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[(off + 20)..]);

            if (name.SequenceEqual(textName))
            {
                textCount++;
                if (textCount == 1)
                {
                    firstTextRva = rva;
                    firstTextRawSize = rawSize;
                    firstTextFileOffset = fileOffset;
                    continue;
                }
            }

            if (name.SequenceEqual(relocName) || name.SequenceEqual(pdataName))
                continue;

            if (rawSize > 0)
                sections.Add(new SectionToDump(fileOffset, rva, rawSize));
        }

        return textCount >= 2
            ? new PeInfo(firstTextFileOffset, firstTextRva, firstTextRawSize,
                sections.ToArray(), entryPointRva, imageBase, dllCharOffset)
            : null;
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

    private static unsafe (byte[] Text, byte[][] SectionData) DumpSections(string executable, PeInfo pe, Action<string>? progress)
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
            var cmdLine = $"\"{tmpPath}\"".ToCharArray();

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

                    progress?.Invoke("Waiting for the exe's stub to decrypt the code section");
                    nint entryAddress = (nint)(pe.ImageBase + pe.EntryPointRva);
                    var crtPollBuffer = new byte[0x2000];
                    long deadline = Environment.TickCount64 + TimeoutMs;

                    while (true)
                    {
                        Thread.Sleep(PollIntervalMs);

                        if (Kernel32.GetExitCodeProcess(process, out var exitCode) && exitCode != 259)
                            throw new InvalidOperationException(
                                $"Process exited (0x{exitCode:X}) before decryption completed");

                        fixed (byte* pBuf = crtPollBuffer)
                        {
                            if (Kernel32.ReadProcessMemory(process, entryAddress, pBuf,
                                    (nuint)crtPollBuffer.Length, out _)
                                && crtPollBuffer.AsSpan().IndexOf(CrtPrologue) >= 0)
                                break;
                        }

                        if (Environment.TickCount64 >= deadline)
                            throw new TimeoutException(
                                $"The exe's code section was not decrypted within {TimeoutMs / 1000}s");
                    }

                    long totalSize = pe.TextRawSize;
                    foreach (var section in pe.Sections)
                        totalSize += section.RawSize;
                    progress?.Invoke($"Reading {totalSize / (1024 * 1024)}MB from the decrypted process");

                    var text = new byte[pe.TextRawSize];
                    nint textAddress = (nint)(pe.ImageBase + pe.TextRva);
                    fixed (byte* pBuf = text)
                    {
                        if (!Kernel32.ReadProcessMemory(process, textAddress, pBuf,
                                (nuint)text.Length, out var bytesRead))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        if (bytesRead != (nuint)text.Length)
                            throw new InvalidOperationException(
                                $"ReadProcessMemory returned {bytesRead} of {text.Length} bytes");
                    }

                    var sectionData = new byte[pe.Sections.Length][];
                    for (int i = 0; i < pe.Sections.Length; i++)
                    {
                        var section = pe.Sections[i];
                        sectionData[i] = new byte[section.RawSize];
                        nint addr = (nint)(pe.ImageBase + section.Rva);
                        fixed (byte* pBuf = sectionData[i])
                        {
                            if (!Kernel32.ReadProcessMemory(process, addr, pBuf,
                                    (nuint)section.RawSize, out var bytesRead))
                                throw new Win32Exception(Marshal.GetLastWin32Error());
                            if (bytesRead != (nuint)section.RawSize)
                                throw new InvalidOperationException(
                                    $"ReadProcessMemory returned {bytesRead} of {section.RawSize} section bytes");
                        }
                    }

                    return (text, sectionData);
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

    private static void FixEntryPoint(byte[] decryptedText, PeInfo pe)
    {
        int entryOffset = (int)(pe.EntryPointRva - pe.TextRva);
        if (entryOffset < 0 || entryOffset + 9 > decryptedText.Length)
            throw new InvalidOperationException("Entry point is outside the .text section");

        if (!decryptedText.AsSpan(entryOffset, EntryPrefix.Length).SequenceEqual(EntryPrefix))
            throw new InvalidOperationException(
                "Entry point does not match the expected pattern (48 83 EC 28 E8)");

        int searchEnd = Math.Min(decryptedText.Length, entryOffset + 0x10000);
        int idx = decryptedText.AsSpan(entryOffset, searchEnd - entryOffset).IndexOf(CrtPrologue);
        if (idx < 0)
            throw new InvalidOperationException(
                "Could not find __scrt_common_main_seh near the entry point");

        int crtOffset = entryOffset + idx;
        int displacement = crtOffset - (entryOffset + 9);
        BinaryPrimitives.WriteInt32LittleEndian(decryptedText.AsSpan(entryOffset + 5), displacement);
    }
}
