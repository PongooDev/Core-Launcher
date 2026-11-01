using System.Text;
using System.Text.RegularExpressions;

namespace Core.Server.Cli.Interface;

internal sealed partial class Table
{
    private readonly List<Column> _columns = [];
    private readonly List<string[]> _rows = [];
    private bool _border = true;

    [GeneratedRegex(@"\u001b(\[[0-9;]*[a-zA-Z]|\]8;;.*?\u001b\\)")]
    private static partial Regex AnsiRegex();

    public static int VisibleLength(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return text.Contains('\u001b') ? AnsiRegex().Replace(text, string.Empty).Length : text.Length;
    }

    public Table Border(bool enable = true)
    {
        _border = enable;
        return this;
    }

    public Table Add(string header, Alignment alignment = Alignment.Left, int minWidth = 0, int maxWidth = 0, Color? color = null)
    {
        _columns.Add(new Column(header, alignment, minWidth, maxWidth, color));
        return this;
    }

    public Table Row(params string[] cells)
    {
        _rows.Add(cells);
        return this;
    }

    public int RowCount => _rows.Count;

    public List<string> BuildLines()
    {
        var result = new List<string>();
        if (_columns.Count == 0) return result;

        var widths = new int[_columns.Count];
        for (var i = 0; i < _columns.Count; i++)
        {
            widths[i] = Math.Max(_columns[i].Header.Length, _columns[i].MinWidth);
        }

        foreach (var row in _rows)
        {
            for (var i = 0; i < _columns.Count && i < row.Length; i++)
            {
                widths[i] = Math.Max(widths[i], VisibleLength(row[i]));
            }
        }

        for (var i = 0; i < _columns.Count; i++)
        {
            if (_columns[i].MaxWidth > 0)
                widths[i] = Math.Min(widths[i], _columns[i].MaxWidth);
        }

        Fit(widths);

        var prefix = _border ? Output.Indent + Theme.Paint("│", Theme.Line) + "  " : Output.Indent;

        var header = new StringBuilder(prefix);
        for (var i = 0; i < _columns.Count; i++)
        {
            if (i > 0) header.Append("  ");
            header.Append(Theme.Paint(Pad(_columns[i].Header, widths[i], _columns[i].Alignment), Theme.Muted));
        }

        result.Add(header.ToString());
        result.Add(prefix + Output.GradientRule(widths.Sum() + (_columns.Count - 1) * 2));

        foreach (var row in _rows)
        {
            var line = new StringBuilder(prefix);
            for (var i = 0; i < _columns.Count; i++)
            {
                if (i > 0) line.Append("  ");
                var value = i < row.Length ? row[i] : string.Empty;
                var padded = Pad(value, widths[i], _columns[i].Alignment);

                if (padded.Contains('\u001b'))
                {
                    line.Append(padded);
                }
                else
                {
                    line.Append(Theme.Paint(padded, _columns[i].Color ?? Theme.Text));
                }
            }

            result.Add(line.ToString());
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

    private void Fit(int[] widths)
    {
        var prefixLen = _border ? Output.Indent.Length + 3 : Output.Indent.Length;
        var available = ConsoleEx.Width - prefixLen - (_columns.Count - 1) * 2 - 1;
        var total = widths.Sum();
        if (total <= available) return;

        var order = Enumerable.Range(0, widths.Length).OrderByDescending(i => widths[i]).ToArray();
        foreach (var index in order)
        {
            if (total <= available) break;
            var floor = Math.Min(widths[index], Math.Max(6, _columns[index].Header.Length));
            var reducible = widths[index] - floor;
            if (reducible <= 0) continue;
            var cut = Math.Min(reducible, total - available);
            widths[index] -= cut;
            total -= cut;
        }
    }

    private static string Pad(string value, int width, Alignment alignment)
    {
        var visibleLen = VisibleLength(value);
        if (visibleLen >= width)
        {
            return value;
        }

        var padding = new string(' ', width - visibleLen);
        return alignment switch
        {
            Alignment.Right => padding + value,
            Alignment.Center =>
                padding[..(padding.Length / 2)] + value + padding[(padding.Length / 2)..],
            _ => value + padding
        };
    }

    private readonly record struct Column(string Header, Alignment Alignment, int MinWidth, int MaxWidth, Color? Color);

    public enum Alignment
    {
        Left,
        Right,
        Center,
    }
}

