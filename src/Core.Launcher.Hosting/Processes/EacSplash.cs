using System.Text.Json;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Hosting.Processes;

/// <summary>
/// The launch window EasyAntiCheat showed on real Fortnite from 3.2: the build's own SplashScreen.png and a progress bar,
/// before the game. Core doesn't run EasyAntiCheat; this only finds the image the build ships, so the launcher can show
/// it while the game starts (Core-Launcher #12). A build without the image gets no window.
/// </summary>
public static class EacSplash
{
    /// <summary>Under the exe's folder: EasyAntiCheat\Launcher\SplashScreen.png.</summary>
    public static readonly string ImageFile = Path.Combine("EasyAntiCheat", "Launcher", "SplashScreen.png");

    private static readonly string SettingsFile = Path.Combine("EasyAntiCheat", "Launcher", "Settings.json");

    /// <summary>
    /// The build's splash image when <see cref="LauncherSettings.ShowEacSplash"/> is on and the build ships one (and
    /// its own Launcher\Settings.json doesn't hide it); null otherwise.
    /// </summary>
    public static string? Find(LauncherSettings settings, BuildSettings build) => settings.ShowEacSplash ? FindImage(build) : null;

    /// <summary>The build's splash image whatever the setting says, null when it has none or hides it.</summary>
    public static string? FindImage(BuildSettings build)
    {
        var folder = Path.GetDirectoryName(build.ExecutablePath);
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        var image = Path.Combine(folder, ImageFile);
        return File.Exists(image) && !HidesSplash(Path.Combine(folder, SettingsFile)) ? image : null;
    }

    /// <summary>Whether the Launcher\Settings.json beside the image has "hide_splash_screen" set to 1.</summary>
    private static bool HidesSplash(string settingsFile)
    {
        try
        {
            if (!File.Exists(settingsFile))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(settingsFile));
            return document.RootElement.TryGetProperty("hide_splash_screen", out var hide)
                && hide.ValueKind == JsonValueKind.String && hide.GetString() == "1";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}
