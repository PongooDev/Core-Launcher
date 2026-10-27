using System.Text;
using Core.Launcher.Hosting.Builds;

namespace Core.Launcher.Tests;

public sealed class ExePatcherTests
{
    private static byte[] Exe(string whitelist) =>
        [.. new byte[64], .. Encoding.Unicode.GetBytes($"-epicapp -epicenv {whitelist} -purgatorymallocproxy"), .. new byte[64]];

    [Fact]
    public void Swaps_the_client_switches_for_the_server_ones_in_place_once()
    {
        using var folder = new TempFolder();
        var exe = folder.Combine("FortniteClient-Win64-Shipping.exe");
        File.WriteAllBytes(exe, Exe("-invitesession -invitefrom -party_joininfo_token -replay"));

        Assert.Equal(ExePatcher.Result.Patched, ExePatcher.PatchWhitelist(exe));
        Assert.Equal(Exe("-log -nosplash -nosound -nullrhi -useolditemcards       "), File.ReadAllBytes(exe));
        Assert.Equal(ExePatcher.Result.AlreadyPatched, ExePatcher.PatchWhitelist(exe));
    }

    [Fact]
    public void Leaves_an_exe_without_the_whitelist_alone()
    {
        using var folder = new TempFolder();
        var exe = folder.Combine("FortniteClient-Win64-Shipping.exe");
        var bytes = Exe("-somethingelse");
        File.WriteAllBytes(exe, bytes);

        Assert.Equal(ExePatcher.Result.NotFound, ExePatcher.PatchWhitelist(exe));
        Assert.Equal(bytes, File.ReadAllBytes(exe));
    }

    [Fact]
    public void Keeps_the_whitelist_its_length()
    {
        Assert.Equal(ExePatcher.ClientSwitches.Length, ExePatcher.ServerSwitches.Length);
    }
}
