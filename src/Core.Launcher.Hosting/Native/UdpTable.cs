using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Core.Launcher.Hosting.Native;

internal static partial class UdpTable
{
    private const int AF_INET = 2;
    private const int UDP_TABLE_OWNER_PID = 1;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedUdpTable(nint pUdpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf, int TableClass, uint Reserved);

    /// <summary>IPv4 UDP ports the process has bound, lowest first.</summary>
    public static IReadOnlyList<int> PortsOwnedBy(int processId)
    {
        var size = 0;
        var result = GetExtendedUdpTable(0, ref size, true, AF_INET, UDP_TABLE_OWNER_PID, 0);
        while (result == ERROR_INSUFFICIENT_BUFFER)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                result = GetExtendedUdpTable(buffer, ref size, true, AF_INET, UDP_TABLE_OWNER_PID, 0);
                if (result == 0)
                {
                    return Parse(buffer, processId);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return [];
    }

    private static List<int> Parse(nint table, int processId)
    {
        // MIB_UDPTABLE_OWNER_PID: DWORD dwNumEntries, then MIB_UDPROW_OWNER_PID { dwLocalAddr, dwLocalPort, dwOwningPid }.
        var count = Marshal.ReadInt32(table);
        var ports = new List<int>();
        for (var index = 0; index < count; ++index)
        {
            var row = table + 4 + index * 12;
            if (Marshal.ReadInt32(row + 8) == processId)
            {
                var port = BinaryPrimitives.ReverseEndianness((ushort)Marshal.ReadInt32(row + 4));
                ports.Add(port);
            }
        }

        ports.Sort();
        return ports;
    }
}
