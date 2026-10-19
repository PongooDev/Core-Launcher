using System.Text;

namespace Core.Launcher.Hosting.Processes;

public static class CommandLine
{
    /// <summary>Quotes one argument so CommandLineToArgvW (and the CRT) read it back unchanged.</summary>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return argument;
        }

        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                ++backslashes;
                continue;
            }

            quoted.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            quoted.Append(character);
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>An engine switch with a value, <c>-Name=Value</c>; a value with spaces is quoted after the '=' as the engine's FParse::Value expects.</summary>
    public static string Switch(string name, string value)
    {
        if (value.Contains('"'))
        {
            throw new ArgumentException($"-{name} can't contain a double quote", nameof(value));
        }

        return value.Length == 0 || value.IndexOfAny([' ', '\t']) >= 0 ? $"-{name}=\"{value}\"" : $"-{name}={value}";
    }

    public static string Join(params IEnumerable<string> parts) => string.Join(' ', parts.Select(part => part.Trim()).Where(part => part.Length > 0));
}
