namespace Core.Launcher.Hosting.Logging;

/// <summary>One log the UI shows as a tab (Launcher, Backend, Client, Server 1, ...), optionally mirrored to a file.</summary>
public sealed class LogChannel : IDisposable
{
    private const int MaxLines = 5000;

    private readonly Lock _gate = new();
    private readonly Queue<string> _lines = new();
    private readonly StreamWriter? _file;
    private readonly bool _timestamp;

    public LogChannel(string name, string? filePath = null, bool timestamp = false)
    {
        Name = name;
        _timestamp = timestamp;
        if (filePath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            RotatePrevious(filePath);
            _file = new StreamWriter(new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
        }
    }

    public string Name { get; }

    public event Action<LogChannel, string>? LineWritten;

    public void Write(string message)
    {
        foreach (var part in message.Replace("\r\n", "\n").Split('\n'))
        {
            var line = _timestamp ? $"[{DateTime.Now:HH:mm:ss.fff}] {part}" : part;
            lock (_gate)
            {
                _lines.Enqueue(line);
                if (_lines.Count > MaxLines)
                {
                    _lines.Dequeue();
                }

                _file?.WriteLine(line);
            }

            LineWritten?.Invoke(this, line);
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return [.. _lines];
        }
    }

    private static void RotatePrevious(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            File.Move(filePath, Path.ChangeExtension(filePath, ".previous" + Path.GetExtension(filePath)), true);
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _file?.Dispose();
        }
    }
}
