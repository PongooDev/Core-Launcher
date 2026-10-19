using System.Text;
using Core.Launcher.Hosting.Logging;

namespace Core.Launcher.Tests;

public sealed class LogTailTests
{
    [Fact]
    public void ReadsLinesAsTheyAreAppendedAndSkipsTheBom()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("Server_log_1.txt");
        var lines = new List<string>();
        var tail = new LogTail(path, lines.Add);

        tail.Poll();
        Assert.Empty(lines);

        // CoreV2's log is opened "w, ccs=UTF-8": a BOM, then whole lines, flushed one by one.
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write(Encoding.UTF8.GetPreamble());
        Write(writer, "[10:00:00.000] [Info] first\n[10:00:00.001] [Info] sec");
        tail.Poll();
        Assert.Equal(["[10:00:00.000] [Info] first"], lines);

        Write(writer, "ond\r\n[10:00:01.000] [Info] Server ready: World\n");
        tail.Poll();
        Assert.Equal(["[10:00:00.000] [Info] first", "[10:00:00.001] [Info] second", "[10:00:01.000] [Info] Server ready: World"], lines);
    }

    [Fact]
    public void KeepsMultibyteCharactersSplitAcrossReads()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("log.txt");
        var lines = new List<string>();
        var tail = new LogTail(path, lines.Add);
        var bytes = Encoding.UTF8.GetBytes("café ✓\n");

        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write(bytes, 0, 4);
        writer.Flush();
        tail.Poll();
        writer.Write(bytes, 4, bytes.Length - 4);
        writer.Flush();
        tail.Poll();

        Assert.Equal(["café ✓"], lines);
    }

    [Fact]
    public void FinalPollFlushesAnUnterminatedLine()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("log.txt");
        File.WriteAllText(path, "last words");
        var lines = new List<string>();
        var tail = new LogTail(path, lines.Add);

        tail.Poll();
        Assert.Empty(lines);
        tail.Poll(final: true);
        Assert.Equal(["last words"], lines);
    }

    [Fact]
    public void ReportsAWriterThatAllowsNoReaders()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("log.txt");
        var tail = new LogTail(path, _ => { });

        using (new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            tail.Poll();
            Assert.True(tail.Locked);
        }

        tail.Poll();
        Assert.False(tail.Locked);
    }

    private static void Write(FileStream stream, string text)
    {
        stream.Write(Encoding.UTF8.GetBytes(text));
        stream.Flush();
    }
}
