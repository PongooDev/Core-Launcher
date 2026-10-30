using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Core.Launcher.Hosting.Native;
using Microsoft.Win32.SafeHandles;

namespace Core.Launcher.Hosting.Builds;

/// <summary>
/// Decrypts builds 1.9.1 and up whose shipping exe ships with encrypted sections. A stub in a second .text section
/// decrypts the image at startup; this lets the stub run in a throwaway process, reads every affected section back,
/// and writes them to disk so the exe runs without the stub and Core's patches survive.
/// The packer's anti-dump technique wipes import names after resolving; this reconstructs a PE import directory
/// from the temp process's loaded modules so the OS re-resolves imports on each real launch.
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
        var text = DumpDecryptedText(executable, pe, progress);
        FixEntryPoint(text, pe);

        // Only the first .text is encrypted.  Every other section (.rdata, .data,
        // .idata, .rsrc, …) is ordinary on-disk data, and the on-disk import
        // directory is already valid — its IAT/ILT point at intact hint/name
        // strings, so the Windows loader resolves all imports normally once the
        // stub is bypassed.  We therefore write back ONLY the decrypted .text and
        // leave everything else untouched, which avoids baking in the runtime
        // state (resolved pointers, wiped names, half-initialised globals) that
        // the stub and early game startup leave in the dumped sections.
        progress?.Invoke($"Writing {text.Length / (1024 * 1024)}MB of decrypted .text back to the exe");
        using (var fs = new FileStream(executable, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            fs.Position = pe.TextFileOffset;
            fs.Write(text);

            // Clear IMAGE_DLLCHARACTERISTICS_DYNAMIC_BASE so the exe loads at its
            // preferred image base.  The decrypted .text holds absolute addresses
            // valid only for that base; the stub would normally re-apply
            // relocations after decrypting, but we bypass it, so we must pin the
            // base instead of letting ASLR move the image.
            fs.Position = pe.DllCharacteristicsOffset;
            Span<byte> dllChar = stackalloc byte[2];
            fs.ReadExactly(dllChar);
            ushort chars = BinaryPrimitives.ReadUInt16LittleEndian(dllChar);
            BinaryPrimitives.WriteUInt16LittleEndian(dllChar, (ushort)(chars & ~DynamicBase));
            fs.Position = pe.DllCharacteristicsOffset;
            fs.Write(dllChar);
        }

        return Result.Decrypted;
    }

    private readonly record struct SectionToDump(string Name, long FileOffset, uint Rva, int RawSize);

    private readonly record struct PeInfo(
        long TextFileOffset, uint TextRva, int TextRawSize,
        SectionToDump[] Sections,
        uint EntryPointRva, long ImageBase,
        long DllCharacteristicsOffset, long DataDirectoriesOffset);

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
        long dataDirOffset = optOffset + 112;

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
            var nameSpan = header.Slice(off, 8);
            uint rva = BinaryPrimitives.ReadUInt32LittleEndian(header[(off + 12)..]);
            int rawSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[(off + 16)..]);
            long fileOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[(off + 20)..]);

            string sectionName = Encoding.ASCII.GetString(nameSpan).TrimEnd('\0');

            if (nameSpan.SequenceEqual(textName))
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

            if (nameSpan.SequenceEqual(relocName) || nameSpan.SequenceEqual(pdataName))
                continue;

            if (rawSize > 0)
                sections.Add(new SectionToDump(sectionName, fileOffset, rva, rawSize));
        }

        return textCount >= 2
            ? new PeInfo(firstTextFileOffset, firstTextRva, firstTextRawSize,
                sections.ToArray(), entryPointRva, imageBase, dllCharOffset, dataDirOffset)
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

    private static unsafe byte[] DumpDecryptedText(
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

                    // Poll until the entry wrapper's post-call bytes have been decrypted
                    // by the stub.  The wrapper is `sub rsp,28 / call stub / add rsp,28
                    // / jmp __scrt_common_main_seh`; the stub decrypts the second half
                    // shortly before transferring back.
                    progress?.Invoke("Waiting for the stub to decrypt the entry wrapper");
                    nint entryAddress = (nint)(pe.ImageBase + pe.EntryPointRva);
                    var wrapperProbe = new byte[22];
                    long deadline = Environment.TickCount64 + TimeoutMs;
                    while (true)
                    {
                        Thread.Sleep(PollIntervalMs);

                        if (Kernel32.GetExitCodeProcess(process, out var exitCode) && exitCode != 259)
                            throw new InvalidOperationException(
                                $"Process exited (0x{exitCode:X}) before decryption completed");

                        fixed (byte* pBuf = wrapperProbe)
                        {
                            if (Kernel32.ReadProcessMemory(process, entryAddress, pBuf,
                                    (nuint)wrapperProbe.Length, out _)
                                && wrapperProbe[9] == 0x48 && wrapperProbe[10] == 0x83
                                && wrapperProbe[11] == 0xC4 && wrapperProbe[12] == 0x28
                                && wrapperProbe[13] == 0xE9)
                                break;
                        }

                        if (Environment.TickCount64 >= deadline)
                            throw new TimeoutException(
                                $"The entry wrapper was not decrypted within {TimeoutMs / 1000}s");
                    }
                    ulong textStart = (ulong)pe.ImageBase + pe.TextRva;

                    // The entry wrapper sits near the end of .text, so once the stub
                    // has decrypted it the whole first .text is decrypted.  A short
                    // settle, then suspend so nothing moves while we read.  We only
                    // read .text — everything else stays as the original on-disk data.
                    progress?.Invoke("Letting the decryption settle");
                    Thread.Sleep(300);
                    if (Kernel32.SuspendThread(thread) == uint.MaxValue)
                        throw new Win32Exception(Marshal.GetLastWin32Error());

                    _ = textStart;
                    progress?.Invoke($"Reading {pe.TextRawSize / (1024 * 1024)}MB of decrypted .text");

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

                    return text;
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
