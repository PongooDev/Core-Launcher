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
    private const ulong OrdinalFlag64 = 0x8000000000000000UL;

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
        var (text, sectionData, imports) = DumpSections(executable, pe, progress);
        FixEntryPoint(text, pe);

        long totalSize = text.Length;
        foreach (var data in sectionData)
            totalSize += data.Length;
        progress?.Invoke($"Writing {totalSize / (1024 * 1024)}MB of decrypted sections back to the exe");
        using (var fs = new FileStream(executable, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            fs.Position = pe.TextFileOffset;
            fs.Write(text);
            for (int i = 0; i < pe.Sections.Length; i++)
            {
                fs.Position = pe.Sections[i].FileOffset;
                fs.Write(sectionData[i]);
            }

            if (imports is { } imp)
            {
                int idataIdx = Array.FindIndex(pe.Sections, s => s.Name == ".idata");
                int rdataIdx = Array.FindIndex(pe.Sections, s => s.Name == ".rdata");

                Span<byte> dirEntry = stackalloc byte[8];

                fs.Position = pe.DataDirectoriesOffset + 1 * 8;
                BinaryPrimitives.WriteUInt32LittleEndian(dirEntry, pe.Sections[idataIdx].Rva);
                BinaryPrimitives.WriteUInt32LittleEndian(dirEntry[4..], (uint)imp.ImportDirectorySize);
                fs.Write(dirEntry);

                fs.Position = pe.DataDirectoriesOffset + 12 * 8;
                BinaryPrimitives.WriteUInt32LittleEndian(dirEntry, pe.Sections[rdataIdx].Rva);
                BinaryPrimitives.WriteUInt32LittleEndian(dirEntry[4..], (uint)imp.IatSize);
                fs.Write(dirEntry);
            }
        }

        return Result.Decrypted;
    }

    private readonly record struct SectionToDump(string Name, long FileOffset, uint Rva, int RawSize);

    private readonly record struct PeInfo(
        long TextFileOffset, uint TextRva, int TextRawSize,
        SectionToDump[] Sections,
        uint EntryPointRva, long ImageBase,
        long DllCharacteristicsOffset, long DataDirectoriesOffset);

    private record struct ImportInfo(int IatSize, int ImportDirectorySize);

    private record struct LoadedModule(string Name, ulong Base, uint Size);

    private record struct ImportEntry(ushort Ordinal, string? ByName);

    private record struct IatGroup(int FirstOffset, int EntryCount, string DllName, ImportEntry[] Entries);

    private record struct ForwardInfo(ushort Ordinal, string ExportName, string TargetDll, string TargetFunc);

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

    private static unsafe (byte[] Text, byte[][] SectionData, ImportInfo? Imports) DumpSections(
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

                    ImportInfo? imports = null;
                    int rdataIdx = Array.FindIndex(pe.Sections, s => s.Name == ".rdata");
                    int idataIdx = Array.FindIndex(pe.Sections, s => s.Name == ".idata");
                    if (rdataIdx >= 0 && idataIdx >= 0)
                    {
                        imports = ReconstructImports(process, pe, sectionData, rdataIdx, idataIdx, progress);
                    }

                    return (text, sectionData, imports);
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

    private static unsafe ImportInfo ReconstructImports(
        SafeProcessHandle process, PeInfo pe, byte[][] sectionData,
        int rdataIdx, int idataIdx, Action<string>? progress)
    {
        var rdata = sectionData[rdataIdx];
        var idata = sectionData[idataIdx];

        progress?.Invoke("Enumerating loaded modules for import reconstruction");
        var modules = EnumerateModules(process, pe.ImageBase);

        progress?.Invoke("Reading export tables from loaded modules");
        var ordinalsByDll = new Dictionary<string, Dictionary<ulong, ushort>>(StringComparer.OrdinalIgnoreCase);
        var namesByDll = new Dictionary<string, Dictionary<string, ulong>>(StringComparer.OrdinalIgnoreCase);
        var reverseNames = new Dictionary<string, Dictionary<ulong, string>>(StringComparer.OrdinalIgnoreCase);
        var addrByOrdinal = new Dictionary<string, Dictionary<ushort, ulong>>(StringComparer.OrdinalIgnoreCase);
        var forwardsByDll = new Dictionary<string, List<ForwardInfo>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in modules)
        {
            var ordMap = new Dictionary<ulong, ushort>();
            var nameMap = new Dictionary<string, ulong>(StringComparer.Ordinal);
            var revMap = new Dictionary<ulong, string>();
            var ordAddrMap = new Dictionary<ushort, ulong>();
            var fwdList = new List<ForwardInfo>();
            ordinalsByDll[mod.Name] = ordMap;
            namesByDll[mod.Name] = nameMap;
            reverseNames[mod.Name] = revMap;
            addrByOrdinal[mod.Name] = ordAddrMap;
            forwardsByDll[mod.Name] = fwdList;
            ReadModuleExports(process, mod, ordMap, nameMap, revMap, ordAddrMap, fwdList);
        }

        var modulesByBaseName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in modules)
            modulesByBaseName.TryAdd(Path.GetFileNameWithoutExtension(mod.Name), mod.Name);

        foreach (var (dllName, fwdList) in forwardsByDll)
        {
            foreach (var fwd in fwdList)
            {
                ulong? resolved = null;

                if (fwd.TargetFunc.StartsWith('#') &&
                    ushort.TryParse(fwd.TargetFunc.AsSpan(1), out var targetOrd))
                {
                    string targetFull = modulesByBaseName.TryGetValue(fwd.TargetDll, out var full) ? full : fwd.TargetDll;
                    if (addrByOrdinal.TryGetValue(targetFull, out var ordAddr) &&
                        ordAddr.TryGetValue(targetOrd, out var addr))
                        resolved = addr;
                    else
                    {
                        foreach (var oa in addrByOrdinal.Values)
                            if (oa.TryGetValue(targetOrd, out addr)) { resolved = addr; break; }
                    }
                }
                else
                {
                    string targetFull = modulesByBaseName.TryGetValue(fwd.TargetDll, out var full) ? full : fwd.TargetDll;
                    if (namesByDll.TryGetValue(targetFull, out var targetNames) &&
                        targetNames.TryGetValue(fwd.TargetFunc, out var addr))
                        resolved = addr;
                    else
                    {
                        foreach (var names in namesByDll.Values)
                            if (names.TryGetValue(fwd.TargetFunc, out addr)) { resolved = addr; break; }
                    }
                }

                if (resolved is { } a)
                    ordinalsByDll[dllName].TryAdd(a, fwd.Ordinal);
            }
        }

        progress?.Invoke("Matching resolved IAT entries to module exports");
        var groups = ParseResolvedIat(process, rdata, ordinalsByDll, reverseNames, forwardsByDll, modules);

        int totalEntries = 0;
        foreach (var g in groups)
            totalEntries += g.Entries.Length;
        progress?.Invoke($"Reconstructing imports: {groups.Count} DLLs, {totalEntries} functions");

        return WriteImportData(rdata, idata, groups,
            pe.Sections[rdataIdx].Rva, pe.Sections[idataIdx].Rva);
    }

    private static unsafe List<LoadedModule> EnumerateModules(SafeProcessHandle process, long exeImageBase)
    {
        const int maxModules = 1024;
        nint* handles = stackalloc nint[maxModules];

        if (!Kernel32.EnumProcessModulesEx(process, handles, (uint)(maxModules * nint.Size), out var needed, 3))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        int count = Math.Min((int)(needed / (uint)nint.Size), maxModules);
        var modules = new List<LoadedModule>(count);
        char* nameBuf = stackalloc char[260];

        for (int i = 0; i < count; i++)
        {
            if ((ulong)handles[i] == (ulong)exeImageBase)
                continue;

            uint nameLen = Kernel32.GetModuleFileNameEx(process, handles[i], nameBuf, 260);
            if (nameLen == 0) continue;

            if (!Kernel32.GetModuleInformation(process, handles[i],
                    out var info, (uint)sizeof(Kernel32.MODULEINFO)))
                continue;

            modules.Add(new LoadedModule(
                Path.GetFileName(new string(nameBuf, 0, (int)nameLen)),
                (ulong)info.lpBaseOfDll,
                info.SizeOfImage));
        }

        return modules;
    }

    private static unsafe void ReadModuleExports(
        SafeProcessHandle process, LoadedModule module,
        Dictionary<ulong, ushort> ordinalMap,
        Dictionary<string, ulong> nameMap,
        Dictionary<ulong, string> reverseNameMap,
        Dictionary<ushort, ulong> ordAddrMap,
        List<ForwardInfo> forwards)
    {
        var header = new byte[4096];
        fixed (byte* p = header)
            if (!Kernel32.ReadProcessMemory(process, (nint)module.Base, p, 4096, out _))
                return;

        int peOff = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x3C));
        if (peOff < 0 || peOff + 120 > header.Length) return;
        int optOff = peOff + 4 + 20;
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(optOff));
        int ddOffset = magic == 0x20B ? 112 : 96;
        uint exportRva = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(optOff + ddOffset));
        uint exportSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(optOff + ddOffset + 4));
        if (exportRva == 0 || exportSize == 0) return;

        var block = new byte[exportSize];
        fixed (byte* p = block)
            if (!Kernel32.ReadProcessMemory(process, (nint)(module.Base + exportRva), p, (nuint)exportSize, out _))
                return;

        if (exportSize < 40) return;
        uint numFunctions = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(20));
        uint numNames = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(24));
        uint ordinalBase = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(16));
        uint addrTableRva = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(28));
        uint nameTableRva = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(32));
        uint ordTableRva = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(36));

        int addrOff = (int)(addrTableRva - exportRva);
        int nameOff = (int)(nameTableRva - exportRva);
        int ordOff = (int)(ordTableRva - exportRva);

        if (addrOff < 0 || (long)addrOff + numFunctions * 4 > exportSize) return;
        if (numNames > 0 && (nameOff < 0 || (long)nameOff + numNames * 4 > exportSize)) return;
        if (numNames > 0 && (ordOff < 0 || (long)ordOff + numNames * 2 > exportSize)) return;

        for (uint i = 0; i < numFunctions; i++)
        {
            uint funcRva = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(addrOff + (int)(i * 4)));
            if (funcRva == 0) continue;
            if (funcRva >= exportRva && funcRva < exportRva + exportSize) continue;
            ulong addr = module.Base + funcRva;
            ushort ord = (ushort)(ordinalBase + i);
            ordinalMap.TryAdd(addr, ord);
            ordAddrMap.TryAdd(ord, addr);
        }

        for (uint j = 0; j < numNames; j++)
        {
            uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(nameOff + (int)(j * 4)));
            ushort ordIdx = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(ordOff + (int)(j * 2)));
            if (ordIdx >= numFunctions) continue;
            uint funcRva = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(addrOff + ordIdx * 4));

            int nameBlockOff = (int)(nameRva - exportRva);
            string? name = ReadAsciiFromBlock(block, nameBlockOff);
            if (name == null && nameRva != 0)
            {
                name = ReadAsciiFromProcess(process, module.Base + nameRva);
            }
            if (name == null) continue;

            ushort ordinal = (ushort)(ordinalBase + ordIdx);

            if (funcRva >= exportRva && funcRva < exportRva + exportSize)
            {
                var fwd = ReadAsciiFromBlock(block, (int)(funcRva - exportRva));
                if (fwd != null)
                {
                    int dot = fwd.IndexOf('.');
                    if (dot > 0)
                        forwards.Add(new ForwardInfo(ordinal, name, fwd[..dot], fwd[(dot + 1)..]));
                }
            }
            else
            {
                ulong addr = module.Base + funcRva;
                nameMap.TryAdd(name, addr);
                reverseNameMap.TryAdd(addr, name);
            }
        }
    }

    private static unsafe string? ReadAsciiFromProcess(SafeProcessHandle process, ulong address)
    {
        var buf = new byte[256];
        fixed (byte* p = buf)
            if (!Kernel32.ReadProcessMemory(process, (nint)address, p, 256, out _))
                return null;
        int end = Array.IndexOf(buf, (byte)0);
        if (end <= 0) return null;
        return Encoding.ASCII.GetString(buf, 0, end);
    }

    private static string? ReadAsciiFromBlock(byte[] block, int offset)
    {
        if (offset < 0 || offset >= block.Length) return null;
        int end = Array.IndexOf(block, (byte)0, offset);
        if (end < 0) end = block.Length;
        if (end == offset) return null;
        return Encoding.ASCII.GetString(block, offset, end - offset);
    }

    private static unsafe List<IatGroup> ParseResolvedIat(
        SafeProcessHandle process, byte[] rdata,
        Dictionary<string, Dictionary<ulong, ushort>> ordinalsByDll,
        Dictionary<string, Dictionary<ulong, string>> reverseNames,
        Dictionary<string, List<ForwardInfo>> forwardsByDll,
        List<LoadedModule> modules)
    {
        var groups = new List<IatGroup>();
        int offset = 0;

        while (offset + 8 <= rdata.Length)
        {
            ulong entry = BinaryPrimitives.ReadUInt64LittleEndian(rdata.AsSpan(offset));

            if (entry == 0)
            {
                offset += 8;
                if (offset + 8 > rdata.Length) break;
                ulong next = BinaryPrimitives.ReadUInt64LittleEndian(rdata.AsSpan(offset));
                if (next == 0 || FindModule(next, modules) == null) break;
                continue;
            }

            if (FindModule(entry, modules) == null) break;

            int groupStart = offset;
            var addresses = new List<ulong>();
            while (offset + 8 <= rdata.Length)
            {
                entry = BinaryPrimitives.ReadUInt64LittleEndian(rdata.AsSpan(offset));
                if (entry == 0) break;
                if (FindModule(entry, modules) == null) break;
                addresses.Add(entry);
                offset += 8;
            }

            if (addresses.Count == 0) break;
            offset += 8;

            string? bestDll = null;
            int bestCount = 0;
            foreach (var (dll, ordMap) in ordinalsByDll)
            {
                int count = 0;
                foreach (var addr in addresses)
                    if (ordMap.ContainsKey(addr)) count++;
                if (count > bestCount) { bestCount = count; bestDll = dll; }
            }

            if (bestDll == null)
                throw new InvalidOperationException(
                    $"IAT group at .rdata+0x{groupStart:X} ({addresses.Count} entries) could not be matched to any loaded module");

            var ordMap2 = ordinalsByDll[bestDll];
            var fwdList = forwardsByDll.GetValueOrDefault(bestDll);
            var accountedAddresses = new HashSet<ulong>();
            foreach (var a in addresses)
                if (ordMap2.ContainsKey(a))
                    accountedAddresses.Add(a);
            var imports = new ImportEntry[addresses.Count];
            for (int i = 0; i < addresses.Count; i++)
            {
                ulong addr = addresses[i];
                if (ordMap2.TryGetValue(addr, out var ordinal))
                {
                    imports[i] = new ImportEntry(ordinal, null);
                    continue;
                }

                string? byName = ResolveByNameFallback(addr, bestDll, fwdList, reverseNames);
                if (byName != null)
                {
                    imports[i] = new ImportEntry(0, byName);
                    continue;
                }

                ulong unshimmed = FollowTrampoline(process, addr);
                if (unshimmed != addr)
                {
                    if (ordMap2.TryGetValue(unshimmed, out ordinal))
                    {
                        imports[i] = new ImportEntry(ordinal, null);
                        continue;
                    }

                    byName = ResolveByNameFallback(unshimmed, bestDll, fwdList, reverseNames);
                    if (byName != null)
                    {
                        imports[i] = new ImportEntry(0, byName);
                        continue;
                    }
                }

                var groupMod = modules.Find(m =>
                    string.Equals(m.Name, bestDll, StringComparison.OrdinalIgnoreCase));
                if (groupMod.Size > 0)
                {
                    ulong? orig = ScanShimForOriginal(process, addr, groupMod.Base, groupMod.Size, ordMap2);
                    if (orig == null)
                        orig = FindHookedExport(process, addr, ordMap2);
                    if (orig is { } origAddr)
                    {
                        if (ordMap2.TryGetValue(origAddr, out ordinal))
                        {
                            imports[i] = new ImportEntry(ordinal, null);
                            continue;
                        }

                        byName = ResolveByNameFallback(origAddr, bestDll, fwdList, reverseNames);
                        if (byName != null)
                        {
                            imports[i] = new ImportEntry(0, byName);
                            continue;
                        }
                    }

                    ulong? shimOrig = ScanShimModuleForOriginal(
                        process, addr, groupMod.Base, groupMod.Size,
                        ordMap2, accountedAddresses, modules);
                    if (shimOrig is { } origAddr2)
                    {
                        if (ordMap2.TryGetValue(origAddr2, out ordinal))
                        {
                            imports[i] = new ImportEntry(ordinal, null);
                            continue;
                        }

                        byName = ResolveByNameFallback(origAddr2, bestDll, fwdList, reverseNames);
                        if (byName != null)
                        {
                            imports[i] = new ImportEntry(0, byName);
                            continue;
                        }
                    }
                }

                throw new InvalidOperationException(
                    $"IAT entry at .rdata+0x{groupStart + i * 8:X} (0x{addr:X}) not found in {bestDll} exports");
            }

            groups.Add(new IatGroup(groupStart, addresses.Count, bestDll, imports));
        }

        if (groups.Count == 0)
            throw new InvalidOperationException("No IAT groups found in .rdata");

        return groups;
    }

    private static string? ResolveByNameFallback(
        ulong address, string groupDll, List<ForwardInfo>? forwards,
        Dictionary<string, Dictionary<ulong, string>> reverseNames)
    {
        if (forwards == null) return null;

        string? targetFuncName = null;
        foreach (var (_, revMap) in reverseNames)
        {
            if (revMap.TryGetValue(address, out targetFuncName))
                break;
        }

        if (targetFuncName == null) return null;

        foreach (var fwd in forwards)
        {
            if (string.Equals(fwd.TargetFunc, targetFuncName, StringComparison.Ordinal))
                return fwd.ExportName;
        }

        return null;
    }

    private static unsafe ulong? FindHookedExport(
        SafeProcessHandle process, ulong shimAddr,
        Dictionary<ulong, ushort> ordinalMap)
    {
        byte* code = stackalloc byte[16];

        foreach (var (exportAddr, _) in ordinalMap)
        {
            if (!Kernel32.ReadProcessMemory(process, (nint)exportAddr, code, 16, out _))
                continue;

            if (code[0] == 0xE9)
            {
                int disp = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(code + 1, 4));
                ulong target = exportAddr + 5 + (ulong)(long)disp;
                if (target == shimAddr)
                    return exportAddr;
            }

            if (code[0] == 0xFF && code[1] == 0x25)
            {
                int disp = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(code + 2, 4));
                ulong ptrAddr = exportAddr + 6 + (ulong)(long)disp;
                ulong target;
                if (Kernel32.ReadProcessMemory(process, (nint)ptrAddr, &target, 8, out _)
                    && target == shimAddr)
                    return exportAddr;
            }

            if (code[0] == 0x48 && code[1] == 0xB8 && code[10] == 0xFF && code[11] == 0xE0)
            {
                ulong target = BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(code + 2, 8));
                if (target == shimAddr)
                    return exportAddr;
            }
        }

        return null;
    }

    private static unsafe ulong? ScanShimForOriginal(
        SafeProcessHandle process, ulong shimAddr, ulong targetBase, uint targetSize,
        Dictionary<ulong, ushort> targetOrdinals)
    {
        var code = new byte[4096];
        fixed (byte* p = code)
            if (!Kernel32.ReadProcessMemory(process, (nint)shimAddr, p, (nuint)code.Length, out _))
                return null;

        ulong targetEnd = targetBase + targetSize;

        for (int i = 0; i < code.Length - 5; i++)
        {
            if (code[i] == 0xE8 || code[i] == 0xE9)
            {
                int disp = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(i + 1));
                ulong target = shimAddr + (uint)i + 5 + (ulong)(long)disp;
                if (target >= targetBase && target < targetEnd && targetOrdinals.ContainsKey(target))
                    return target;
            }

            if (i + 6 <= code.Length && code[i] == 0xFF && (code[i + 1] == 0x15 || code[i + 1] == 0x25))
            {
                int disp = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(i + 2));
                ulong ptrAddr = shimAddr + (uint)i + 6 + (ulong)(long)disp;
                ulong target;
                if (Kernel32.ReadProcessMemory(process, (nint)ptrAddr, &target, 8, out _)
                    && target >= targetBase && target < targetEnd && targetOrdinals.ContainsKey(target))
                    return target;
            }
        }

        for (int i = 0; i <= code.Length - 8; i++)
        {
            ulong val = BinaryPrimitives.ReadUInt64LittleEndian(code.AsSpan(i));
            if (val >= targetBase && val < targetEnd && targetOrdinals.ContainsKey(val))
                return val;
        }

        return null;
    }

    private static unsafe ulong? ScanShimModuleForOriginal(
        SafeProcessHandle process, ulong shimAddr,
        ulong groupBase, uint groupSize,
        Dictionary<ulong, ushort> groupOrdinals,
        HashSet<ulong> accountedAddresses,
        List<LoadedModule> modules)
    {
        ulong groupEnd = groupBase + groupSize;

        var code = new byte[16384];
        fixed (byte* p = code)
            if (!Kernel32.ReadProcessMemory(process, (nint)shimAddr, p,
                    (nuint)code.Length, out _))
                return null;

        for (int i = 0; i < code.Length - 7; i++)
        {
            if ((code[i] == 0x48 || code[i] == 0x4C) && code[i + 1] == 0x8B &&
                (code[i + 2] & 0xC7) == 0x05)
            {
                int disp = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(i + 3));
                ulong dataAddr = shimAddr + (ulong)(uint)(i + 7) + (ulong)(long)disp;

                ulong val;
                if (!Kernel32.ReadProcessMemory(process, (nint)dataAddr, &val, 8, out _))
                    continue;

                if (val >= groupBase && val < groupEnd
                    && groupOrdinals.ContainsKey(val)
                    && !accountedAddresses.Contains(val))
                    return val;

                if (val > 0x10000 && val < 0x7FFFFFFFFFFF)
                {
                    var buf = new byte[512];
                    fixed (byte* bp = buf)
                    {
                        if (Kernel32.ReadProcessMemory(process, (nint)val, bp,
                                (nuint)buf.Length, out var read))
                        {
                            for (int j = 0; j <= (int)read - 8; j += 8)
                            {
                                ulong inner = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(j));
                                if (inner >= groupBase && inner < groupEnd
                                    && groupOrdinals.ContainsKey(inner)
                                    && !accountedAddresses.Contains(inner))
                                    return inner;
                            }
                        }
                    }
                }
            }
        }

        LoadedModule shimModule = default;
        foreach (var mod in modules)
        {
            if (shimAddr >= mod.Base && shimAddr < mod.Base + mod.Size)
            { shimModule = mod; break; }
        }
        if (shimModule.Size == 0) return null;

        var image = new byte[shimModule.Size];
        fixed (byte* p = image)
            if (!Kernel32.ReadProcessMemory(process, (nint)shimModule.Base, p,
                    (nuint)shimModule.Size, out _))
                return null;

        long shimRel = (long)(shimAddr - shimModule.Base);
        ulong? bestMatch = null;
        long bestDistance = long.MaxValue;

        for (int i = 0; i <= image.Length - 8; i++)
        {
            ulong val = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(i));
            if (val < groupBase || val >= groupEnd) continue;
            if (!groupOrdinals.ContainsKey(val)) continue;
            if (accountedAddresses.Contains(val)) continue;

            long dist = Math.Abs((long)i - shimRel);
            if (dist < bestDistance)
            {
                bestDistance = dist;
                bestMatch = val;
            }
        }

        return bestMatch;
    }

    private static unsafe ulong FollowTrampoline(SafeProcessHandle process, ulong address)
    {
        ulong current = address;
        for (int depth = 0; depth < 5; depth++)
        {
            byte* code = stackalloc byte[16];
            if (!Kernel32.ReadProcessMemory(process, (nint)current, code, 16, out _))
                break;

            if (code[0] == 0xFF && code[1] == 0x25)
            {
                int disp = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(code + 2, 4));
                ulong ptrAddr = current + 6 + (ulong)(long)disp;
                ulong target;
                if (!Kernel32.ReadProcessMemory(process, (nint)ptrAddr, &target, 8, out _))
                    break;
                current = target;
                continue;
            }

            if (code[0] == 0xE9)
            {
                int disp = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(code + 1, 4));
                current = current + 5 + (ulong)(long)disp;
                continue;
            }

            if (code[0] == 0x48 && code[1] == 0xB8 && code[10] == 0xFF && code[11] == 0xE0)
            {
                current = BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(code + 2, 8));
                continue;
            }

            break;
        }

        return current;
    }

    private static string? FindModule(ulong address, List<LoadedModule> modules)
    {
        foreach (var mod in modules)
            if (address >= mod.Base && address < mod.Base + mod.Size) return mod.Name;
        return null;
    }

    private static ImportInfo WriteImportData(
        byte[] rdata, byte[] idata, List<IatGroup> groups,
        uint rdataRva, uint idataRva)
    {
        int lastGroup = groups.Count - 1;
        int iatSize = groups[lastGroup].FirstOffset + groups[lastGroup].EntryCount * 8 + 8;

        Array.Clear(idata);

        int descriptorCount = groups.Count + 1;
        int descriptorBytes = descriptorCount * 20;
        int stringsPos = descriptorBytes;

        var dllPositions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int currentPos = stringsPos;
        foreach (var group in groups)
        {
            if (!dllPositions.ContainsKey(group.DllName))
            {
                dllPositions[group.DllName] = currentPos;
                currentPos += Encoding.ASCII.GetByteCount(group.DllName) + 1;
                if (currentPos % 2 != 0) currentPos++;
            }
        }

        int hintNamePos = currentPos;
        var hintNameOffsets = new Dictionary<(int groupIdx, int entryIdx), int>();
        for (int g = 0; g < groups.Count; g++)
        {
            for (int i = 0; i < groups[g].Entries.Length; i++)
            {
                var entry = groups[g].Entries[i];
                if (entry.ByName != null)
                {
                    if (hintNamePos % 2 != 0) hintNamePos++;
                    hintNameOffsets[(g, i)] = hintNamePos;
                    hintNamePos += 2 + Encoding.ASCII.GetByteCount(entry.ByName) + 1;
                }
            }
        }

        for (int g = 0; g < groups.Count; g++)
        {
            for (int i = 0; i < groups[g].Entries.Length; i++)
            {
                var entry = groups[g].Entries[i];
                if (entry.ByName != null)
                {
                    int hnOff = hintNameOffsets[(g, i)];
                    Encoding.ASCII.GetBytes(entry.ByName, idata.AsSpan(hnOff + 2));
                    uint hnRva = idataRva + (uint)hnOff;
                    BinaryPrimitives.WriteUInt64LittleEndian(
                        rdata.AsSpan(groups[g].FirstOffset + i * 8), hnRva);
                }
                else
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(
                        rdata.AsSpan(groups[g].FirstOffset + i * 8),
                        OrdinalFlag64 | entry.Ordinal);
                }
            }
        }

        for (int i = 0; i < groups.Count; i++)
        {
            int d = i * 20;
            BinaryPrimitives.WriteInt32LittleEndian(idata.AsSpan(d + 8), -1);
            BinaryPrimitives.WriteUInt32LittleEndian(idata.AsSpan(d + 12),
                idataRva + (uint)dllPositions[groups[i].DllName]);
            BinaryPrimitives.WriteUInt32LittleEndian(idata.AsSpan(d + 16),
                rdataRva + (uint)groups[i].FirstOffset);
        }

        foreach (var (name, pos) in dllPositions)
            Encoding.ASCII.GetBytes(name, idata.AsSpan(pos));

        int importDirSize = hintNamePos > currentPos ? hintNamePos : descriptorBytes;
        return new ImportInfo(iatSize, importDirSize);
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
