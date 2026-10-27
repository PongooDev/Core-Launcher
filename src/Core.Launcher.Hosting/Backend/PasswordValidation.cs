namespace Core.Launcher.Hosting.Backend;

/// <summary>
/// Password and username validation rules enforcing parity with Core-Backend's authentication policies:
/// 8-128 character passwords, no quotes, and trimmed whitespace.
/// </summary>
public static class PasswordValidation
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;

    /// <summary>
    /// Validates password input. If not required and empty, returns null.
    /// Otherwise enforces 8 to 128 characters and disallows double quotes.
    /// </summary>
    public static string? Validate(string? password, bool required = false)
    {
        var trimmed = (password ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return required ? "Password is required" : null;
        }

        if (trimmed.Length < MinPasswordLength)
        {
            return $"Password must be at least {MinPasswordLength} characters long";
        }

        if (trimmed.Length > MaxPasswordLength)
        {
            return $"Password must not exceed {MaxPasswordLength} characters";
        }

        if (trimmed.Contains('"'))
        {
            return "A password can't contain quotes";
        }

        return null;
    }

    /// <summary>
    /// Validates username input: must not be empty and must not contain quotes.
    /// </summary>
    public static string? ValidateUsername(string? username)
    {
        var trimmed = (username ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return "Enter a player name";
        }

        if (trimmed.Contains('"'))
        {
            return "A player name can't contain quotes";
        }

        return null;
    }
}
