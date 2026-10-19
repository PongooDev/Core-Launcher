using System.Text;

namespace Core.Launcher.Hosting.Logging;

/// <summary>Follows a log file another process is writing, line by line, from its first byte.</summary>
public sealed class LogTail
{
    private const int SharingViolation = unchecked((int)0x80070020);

    private readonly string _path;
    private readonly Action<string> _line;
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly StringBuilder _partial = new();
    private readonly byte[] _buffer = new byte[64 * 1024];
    private readonly char[] _chars = new char[64 * 1024 + 1];
    private long _position;
    private bool _bomChecked;

    public LogTail(string path, Action<string> line)
    {
        _path = path;
        _line = line;
    }

    public string FilePath => _path;

    /// <summary>True once the file was there but couldn't be opened because its writer allows no readers.</summary>
    public bool Locked { get; private set; }

    public bool Exists => File.Exists(_path);

    /// <summary>Reads whatever was appended since the last call. <paramref name="final"/> also flushes an unterminated last line.</summary>
    public void Poll(bool final = false)
    {
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Locked = false;
            if (stream.Length < _position)
            {
                _position = 0;
                _bomChecked = false;
                _partial.Clear();
                _decoder.Reset();
            }

            stream.Position = _position;
            int read;
            while ((read = stream.Read(_buffer, 0, _buffer.Length)) > 0)
            {
                var offset = 0;
                if (!_bomChecked)
                {
                    _bomChecked = true;
                    if (read >= 3 && _buffer[0] == 0xEF && _buffer[1] == 0xBB && _buffer[2] == 0xBF)
                    {
                        offset = 3;
                    }
                }

                _position += read;
                var count = _decoder.GetChars(_buffer, offset, read - offset, _chars, 0);
                Split(count);
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException exception) when (exception.HResult == SharingViolation)
        {
            Locked = true;
        }

        if (final && _partial.Length > 0)
        {
            _line(_partial.ToString());
            _partial.Clear();
        }
    }

    private void Split(int count)
    {
        var start = 0;
        for (var index = 0; index < count; ++index)
        {
            if (_chars[index] != '\n')
            {
                continue;
            }

            _partial.Append(_chars, start, index - start);
            if (_partial.Length > 0 && _partial[^1] == '\r')
            {
                _partial.Length--;
            }

            _line(_partial.ToString());
            _partial.Clear();
            start = index + 1;
        }

        _partial.Append(_chars, start, count - start);
    }
}
