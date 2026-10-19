namespace Core.Launcher.Hosting.Dlls;

public static class PeImage
{
    private const ushort MachineAmd64 = 0x8664;
    private const ushort FileDll = 0x2000;

    /// <summary>Returns why the file can't be loaded into an x64 game, or null when it can.</summary>
    public static string? CheckX64(string path, bool dll)
    {
        if (!File.Exists(path))
        {
            return $"{path} doesn't exist";
        }

        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.BaseStream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D)
        {
            return $"{path} isn't a Windows image";
        }

        reader.BaseStream.Position = 0x3C;
        var header = reader.ReadUInt32();
        if (header + 24 > reader.BaseStream.Length)
        {
            return $"{path} isn't a Windows image";
        }

        reader.BaseStream.Position = header;
        if (reader.ReadUInt32() != 0x00004550)
        {
            return $"{path} isn't a Windows image";
        }

        var machine = reader.ReadUInt16();
        reader.BaseStream.Position = header + 22;
        var characteristics = reader.ReadUInt16();
        if (machine != MachineAmd64)
        {
            return $"{path} isn't x64 (machine 0x{machine:X4})";
        }

        if (dll != ((characteristics & FileDll) != 0))
        {
            return dll ? $"{path} isn't a DLL" : $"{path} is a DLL, not an executable";
        }

        return null;
    }
}
