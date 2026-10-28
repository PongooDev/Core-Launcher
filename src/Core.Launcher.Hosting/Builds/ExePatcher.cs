using System.Text;

namespace Core.Launcher.Hosting.Builds;

/// <summary>
/// Lets a server's switches through the shipping exe's command line whitelist. FCommandLine::Set keeps only the switches
/// in one space-separated string the exe holds ("... -epicapp -epicenv -invitesession -invitefrom -party_joininfo_token
/// -replay -purgatorymallocproxy ..."), so -nullrhi never reached the engine: a 1.8 server made a D3D11 device and
/// crashed building the loading screen. This swaps four client-only switches for server ones in place, as Reboot
/// Launcher does; the string keeps its length. Core's server makes the swap in memory when it starts, so a build this
/// can't write to (in use, read-only) still works; in memory it also swaps Reboot's -useolditemcards for
/// -saveddirsuffix, which servers need (<see cref="Processes.ServerConfig"/>).
/// </summary>
public static class ExePatcher
{
    public static readonly byte[] ClientSwitches = Encoding.Unicode.GetBytes("-invitesession -invitefrom -party_joininfo_token -replay");

    public static readonly byte[] ServerSwitches = Encoding.Unicode.GetBytes("-log -nosplash -nosound -nullrhi -useolditemcards       ");

    public enum Result
    {
        Patched,
        AlreadyPatched,
        /// <summary>The exe has neither string: a build without the whitelist, or one patched some other way.</summary>
        NotFound,
    }

    /// <summary>Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the exe can't be written.</summary>
    public static Result PatchWhitelist(string executable)
    {
        var bytes = File.ReadAllBytes(executable);
        var offset = bytes.AsSpan().IndexOf(ClientSwitches);
        if (offset < 0)
        {
            return bytes.AsSpan().IndexOf(ServerSwitches) >= 0 ? Result.AlreadyPatched : Result.NotFound;
        }

        using var file = new FileStream(executable, FileMode.Open, FileAccess.Write, FileShare.Read);
        file.Position = offset;
        file.Write(ServerSwitches);
        return Result.Patched;
    }
}
