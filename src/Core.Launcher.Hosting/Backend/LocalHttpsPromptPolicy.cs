using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Hosting.Backend;

/// <summary>
/// When the launcher asks at startup whether Windows should trust this PC's local backend certificate
/// (Core-Launcher #22): only for the player's own backend, while the setting is on, a build is selected, and the
/// backend's current root is untrusted or its earlier root is trusted instead. A friend's backend is never asked:
/// its certificate couldn't vouch for this PC's 127.0.0.1 anyway.
/// </summary>
public static class LocalHttpsPromptPolicy
{
    public static bool ShouldPrompt(LauncherSettings settings, LocalTrustStatus? status) =>
        settings.AskToTrustLocalHttps
        && !settings.Backend.UseRemote
        && settings.FindSelectedBuild() is not null
        && status is { State: LocalTrustState.NotTrusted or LocalTrustState.Outdated };
}
