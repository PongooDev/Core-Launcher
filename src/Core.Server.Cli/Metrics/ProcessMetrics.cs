using System.Diagnostics;

namespace Core.Server.Cli.Metrics;

public sealed record ProcessSnapshot(int ProcessId, long WorkingSetBytes, double CpuPercent);

public sealed record TotalProcessSnapshot(long TotalWorkingSetBytes, double TotalCpuPercent, IReadOnlyDictionary<int, ProcessSnapshot> ByProcessId);

public sealed class ProcessMetrics
{
    private readonly Dictionary<int, (TimeSpan LastCpu, DateTimeOffset LastTime, double LastPercent)> _history = [];

    public TotalProcessSnapshot Sample(IEnumerable<int> processIds)
    {
        var result = new Dictionary<int, ProcessSnapshot>();
        var totalBytes = 0L;
        var totalCpu = 0.0;
        var activeIds = new HashSet<int>(processIds);

        // Clean up exited processes
        var toRemove = _history.Keys.Where(id => !activeIds.Contains(id)).ToList();
        foreach (var id in toRemove)
        {
            _history.Remove(id);
        }

        var now = DateTimeOffset.UtcNow;
        var processorCount = Environment.ProcessorCount;

        foreach (var pid in activeIds)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    continue;
                }

                var workingSet = process.WorkingSet64;
                var totalProcessorTime = process.TotalProcessorTime;
                var cpuPercent = 0.0;

                if (_history.TryGetValue(pid, out var previous))
                {
                    var elapsedMs = (now - previous.LastTime).TotalMilliseconds;
                    var cpuDeltaMs = (totalProcessorTime - previous.LastCpu).TotalMilliseconds;

                    if (elapsedMs > 200)
                    {
                        cpuPercent = Math.Clamp(cpuDeltaMs / (elapsedMs * processorCount) * 100.0, 0.0, 100.0);
                        _history[pid] = (totalProcessorTime, now, cpuPercent);
                    }
                    else
                    {
                        cpuPercent = previous.LastPercent;
                    }
                }
                else
                {
                    _history[pid] = (totalProcessorTime, now, 0.0);
                }

                result[pid] = new ProcessSnapshot(pid, workingSet, cpuPercent);
                totalBytes += workingSet;
                totalCpu += cpuPercent;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                // Process terminated between query and reading properties
                _history.Remove(pid);
            }
        }

        return new TotalProcessSnapshot(totalBytes, totalCpu, result);
    }
}

