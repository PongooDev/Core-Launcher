using System.Text;

namespace Core.Server.Cli.Interface;

internal sealed record LogoArt(int Width, IReadOnlyList<string> Rows, string Badge, int Split);

internal static class Logo
{
    private const string Version = "v0.1.0";

    private static readonly LogoArt Art = new(75,
    [
        " ██████   ██████   ██████   ███████     ██   ██   ██████    ██████  ███████",
        "██       ██    ██  ██   ██  ██          ██   ██  ██    ██  ██         ███  ",
        "██       ██    ██  ██████   █████       ███████  ██    ██   █████     ███  ",
        "██       ██    ██  ██   ██  ██          ██   ██  ██    ██       ██    ███  ",
        " ██████   ██████   ██   ██  ███████     ██   ██   ██████   ██████     ███  "
    ], Version, 37);

    public static void Render() => ConsoleEx.WriteBlock(Lines());

    public static List<string> Lines()
    {
        var available = ConsoleEx.Width - Output.Indent.Length - 1;
        if (available < Art.Width || ConsoleEx.Height < 28) return Compact();

        var lines = new List<string> { string.Empty };

        foreach (var row in Art.Rows)
            lines.Add(Output.Indent + (ConsoleEx.SupportsColor ? Gradient(row, Art) : row));

        lines.Add(Output.Indent + Underline(Art));
        return lines;
    }

    private static List<string> Compact() =>
    [
        string.Empty,
        Output.Indent +
        Theme.Emphasis("CORE", Theme.BrandStart) + " " +
        Theme.Emphasis("HOST", Theme.BrandEnd) + "  " +
        Theme.Paint(Version, Theme.Muted)
    ];

    private static string Underline(LogoArt art)
    {
        var rule = Math.Max(4, art.Width - art.Badge.Length - 3);
        if (!ConsoleEx.SupportsColor) return new string('─', rule) + "   " + art.Badge;

        var builder = new StringBuilder();

        for (var column = 0; column < rule; column++)
        {
            builder.Append(Ansi.Foreground(Fade(Theme.Brand((double)column / Math.Max(1, rule - 1)), 0.45d)));
            builder.Append('━');
        }

        builder.Append(Ansi.Reset);
        builder.Append("   ");
        builder.Append(Theme.Paint(art.Badge, Theme.LightGray));
        return builder.ToString();
    }

    private static string Gradient(string line, LogoArt art)
    {
        var builder = new StringBuilder(line.Length * 12);
        var lastColor = default(Color);
        var painting = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == ' ')
            {
                if (painting)
                {
                    builder.Append(Ansi.Reset);
                    painting = false;
                }

                builder.Append(' ');
                continue;
            }

            var color = ColorAt(index, art);
            if (!painting || color != lastColor)
            {
                builder.Append(Ansi.Foreground(color));
                lastColor = color;
                painting = true;
            }

            builder.Append(character);
        }

        if (painting) builder.Append(Ansi.Reset);
        return builder.ToString();
    }

    private static Color ColorAt(int column, LogoArt art)
    {
        if (art.Split < 0) return Theme.Brand((double)column / Math.Max(1, art.Width - 1));
        return column < art.Split
            ? Theme.BrandStart.Blend(Theme.BrandMiddle, (double)column / Math.Max(1, art.Split - 1))
            : Theme.BrandMiddle.Blend(Theme.BrandEnd, (double)(column - art.Split) / Math.Max(1, art.Width - art.Split - 1));
    }

    private static Color Fade(Color color, double strength) =>
        new((byte)(color.R * strength), (byte)(color.G * strength), (byte)(color.B * strength));
}
