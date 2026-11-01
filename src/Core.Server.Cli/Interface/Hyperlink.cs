namespace Core.Server.Cli.Interface;

internal static class Hyperlink
{
    /// <summary>
    /// Formats text as an OSC 8 clickable terminal hyperlink. Supported in modern terminals (Windows Terminal, VS Code, etc.).
    /// Falls back to text if terminal colors/escape sequences are not supported.
    /// </summary>
    public static string Format(string url, string? label = null)
    {
        label ??= url;
        if (!ConsoleEx.SupportsColor)
        {
            return label == url ? url : $"{label} ({url})";
        }

        return $"\u001b]8;;{url}\u001b\\{label}\u001b]8;;\u001b\\";
    }
}

