namespace Core.Server.Cli.Interface;

internal sealed class Panel
{
    private readonly List<Entry> _entries = [];

    public Panel Row(string key, string value, Color? color = null)
    {
        _entries.Add(new Entry(key, value, color, false));
        return this;
    }

    public Panel Gap()
    {
        _entries.Add(new Entry(string.Empty, string.Empty, null, true));
        return this;
    }

    public List<string> BuildLines()
    {
        var result = new List<string>();
        if (_entries.Count == 0) return result;

        var keyWidth = _entries.Where(entry => !entry.IsGap).Select(entry => entry.Key.Length).DefaultIfEmpty(0).Max();
        var valueWidth = Math.Max(10, ConsoleEx.Width - Output.Indent.Length - keyWidth - 7);

        foreach (var entry in _entries)
        {
            if (entry.IsGap)
            {
                result.Add(Output.Indent + Theme.Paint("│", Theme.Line));
                continue;
            }

            var value = entry.Color.HasValue
                ? Theme.Paint(entry.Value, entry.Color.Value)
                : entry.Value;

            result.Add(Output.Indent + Theme.Paint("│", Theme.Line) + "  "
                       + Theme.Paint(entry.Key.PadRight(keyWidth + 3), Theme.Muted)
                       + value);
        }

        return result;
    }

    public void Render()
    {
        foreach (var line in BuildLines())
        {
            ConsoleEx.WriteLine(line);
        }
    }

    private readonly record struct Entry(string Key, string Value, Color? Color, bool IsGap);
}

