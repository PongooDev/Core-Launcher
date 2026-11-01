using System.Runtime.InteropServices;

namespace Core.Server.Cli.Metrics;

public sealed record SystemSnapshot(double CpuPercent, long UsedRamBytes, long TotalRamBytes)
{
    public double RamPercent => TotalRamBytes > 0 ? (double)UsedRamBytes / TotalRamBytes * 100d : 0d;
}

public sealed partial class SystemMetrics
{
    private long _lastIdleTime;
    private long _lastKernelTime;
    private long _lastUserTime;
    private DateTimeOffset _lastSample = DateTimeOffset.MinValue;
    private double _lastCpu;

    public SystemSnapshot Sample()
    {
        var ram = SampleRam();
        var cpu = SampleCpu();
        return new SystemSnapshot(cpu, ram.UsedBytes, ram.TotalBytes);
    }

    private (long UsedBytes, long TotalBytes) SampleRam()
    {
        if (!OperatingSystem.IsWindows())
        {
            var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return (Environment.WorkingSet, total > 0 ? total : 16L * 1024 * 1024 * 1024);
        }

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            var total = (long)mem.ullTotalPhys;
            var avail = (long)mem.ullAvailPhys;
            return (Math.Max(0, total - avail), total);
        }

        return (Environment.WorkingSet, 16L * 1024 * 1024 * 1024);
    }

    private double SampleCpu()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0d;
        }

        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return _lastCpu;
        }

        var now = DateTimeOffset.UtcNow;
        if (_lastSample == DateTimeOffset.MinValue)
        {
            _lastIdleTime = idleTime;
            _lastKernelTime = kernelTime;
            _lastUserTime = userTime;
            _lastSample = now;
            return 0d;
        }

        var idleDelta = idleTime - _lastIdleTime;
        var kernelDelta = kernelTime - _lastKernelTime;
        var userDelta = userTime - _lastUserTime;
        var totalDelta = kernelDelta + userDelta;

        _lastIdleTime = idleTime;
        _lastKernelTime = kernelTime;
        _lastUserTime = userTime;
        _lastSample = now;

        if (totalDelta <= 0)
        {
            return _lastCpu;
        }

        var cpu = Math.Clamp((1.0 - ((double)idleDelta / totalDelta)) * 100.0, 0.0, 100.0);
        _lastCpu = cpu;
        return cpu;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);
}

