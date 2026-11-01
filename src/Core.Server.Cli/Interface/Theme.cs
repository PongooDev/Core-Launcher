namespace Core.Server.Cli.Interface;

internal static class Theme
{
    // Modern monochrome / slate aesthetic: Pure White to Distinct Gray
    public static readonly Color White = Color.FromHex(0xFFFFFF);
    public static readonly Color OffWhite = Color.FromHex(0xD4D8E0);
    public static readonly Color LightGray = Color.FromHex(0x94A3B8);      // Deeper, more distinct gray (Slate 400)
    public static readonly Color SlateGray = Color.FromHex(0x64748B);

    public static readonly Color BrandStart = White;       // Pure crisp white (0xFFFFFF)
    public static readonly Color BrandMiddle = OffWhite;   // Clean soft off-white (0xD4D8E0)
    public static readonly Color BrandEnd = LightGray;     // True visible gray (0x94A3B8)

    public static readonly Color Accent = White;
    public static readonly Color AccentSoft = LightGray;
    public static readonly Color CSharp = LightGray;
    public static readonly Color Cyan = LightGray;         // Alias mapped to gray

    public static readonly Color Text = Color.FromHex(0xF8FAFC);     // Slate 50 bright clean text
    public static readonly Color Dim = LightGray;                    // Slate 400 clean secondary text
    public static readonly Color Muted = Color.FromHex(0x555E6D);    // Slate 500 hints and labels
    public static readonly Color Line = Color.FromHex(0x334155);     // Slate 700 subtle dark border lines

    // Functional status badges
    public static readonly Color Success = Color.FromHex(0x4ADE80);  // Green (Ready)
    public static readonly Color Warning = Color.FromHex(0xFBBF24);  // Amber (Registering)
    public static readonly Color Danger = Color.FromHex(0xF87171);   // Red (Errors)

    // Highlight bar background (sleek dark charcoal/slate)
    public static readonly Color Highlight = Color.FromHex(0x1E2430);

    public static Color Brand(double position)
    {
        position = Math.Clamp(position, 0d, 1d);

        return position < 0.5d
            ? BrandStart.Blend(BrandMiddle, position * 2d)
            : BrandMiddle.Blend(BrandEnd, (position - 0.5d) * 2d);
    }

    public static string Paint(string text, Color color) =>
        ConsoleEx.SupportsColor ? $"{Ansi.Foreground(color)}{text}{Ansi.Reset}" : text;

    public static string Emphasis(string text, Color color) =>
        ConsoleEx.SupportsColor ? $"{Ansi.Bold}{Ansi.Foreground(color)}{text}{Ansi.Reset}" : text;
}

