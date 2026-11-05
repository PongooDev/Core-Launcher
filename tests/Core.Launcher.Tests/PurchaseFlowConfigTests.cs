using System.Text;
using Core.Launcher.Hosting;
using Core.Launcher.Hosting.Backend;
using Core.Launcher.Hosting.Processes;

namespace Core.Launcher.Tests;

public sealed class PurchaseFlowConfigTests
{
    private const string LocalUrl = "http:///127.0.0.1:5595/purchase";

    [Fact]
    public void The_url_is_the_backends_host_after_a_triple_slash()
    {
        Assert.Equal(LocalUrl, PurchaseFlowConfig.Url(BackendEndpoint.Local(5595)));
        Assert.Equal("http:///my-house.duckdns.org:5600/purchase", PurchaseFlowConfig.Url(new BackendEndpoint("my-house.duckdns.org", 5600)));
        Assert.Equal("http:///[2001:db8::1]:5595/purchase", PurchaseFlowConfig.Url(new BackendEndpoint("2001:db8::1", 5595)));
        Assert.Equal("https:///core.example.com/purchase", PurchaseFlowConfig.Url(new BackendEndpoint("core.example.com", 443, Secure: true)));
        Assert.Equal("https:///core.example.com:8443/purchase", PurchaseFlowConfig.Url(new BackendEndpoint("core.example.com", 8443, Secure: true)));
    }

    [Fact]
    public void The_players_game_ini_is_in_fortnites_saved_config()
    {
        var paths = new LauncherPaths(@"D:\Launcher") { GameUserDir = @"C:\Users\Player\AppData\Local\FortniteGame" };

        Assert.Equal(@"C:\Users\Player\AppData\Local\FortniteGame\Saved\Config\WindowsClient\Game.ini", PurchaseFlowConfig.GameIniPath(paths));
    }

    [Fact]
    public void Creates_the_folder_and_file()
    {
        using var folder = new TempFolder();
        var ini = folder.Combine("Saved", "Config", "WindowsClient", "Game.ini");

        Assert.True(PurchaseFlowConfig.Write(ini, LocalUrl));
        Assert.Equal($"[EpicPurchaseFlow]\r\nProd={LocalUrl}\r\n", File.ReadAllText(ini));
        Assert.False(PurchaseFlowConfig.Write(ini, LocalUrl));
    }

    [Fact]
    public void Adds_the_section_after_the_files_others()
    {
        var existing = "[/Script/FortniteGame.FortGameUserSettings]\r\nbShowFPS=True\r\n\r\n[Paths]\r\nFoo=Bar\r\n";

        Assert.Equal(existing + $"\r\n[EpicPurchaseFlow]\r\nProd={LocalUrl}\r\n", PurchaseFlowConfig.Merge(existing, LocalUrl));
        Assert.Equal($"[A]\nB=1\n\n[EpicPurchaseFlow]\nProd={LocalUrl}\n", PurchaseFlowConfig.Merge("[A]\nB=1\n", LocalUrl));
        Assert.Equal($"[A]\r\nB=1\r\n\r\n[EpicPurchaseFlow]\r\nProd={LocalUrl}\r\n", PurchaseFlowConfig.Merge("[A]\r\nB=1", LocalUrl));
    }

    [Fact]
    public void Replaces_only_the_old_prod_line()
    {
        var existing = "[A]\r\nProd=keep\r\n\r\n[EpicPurchaseFlow]\r\nGamedev=gd\r\nProd=https://launcher-website-prod07.ol.epicgames.com/purchase\r\nStage=st\r\nprod=repeat\r\n\r\n[B]\r\nC=1\r\n";

        Assert.Equal(
            $"[A]\r\nProd=keep\r\n\r\n[EpicPurchaseFlow]\r\nGamedev=gd\r\nProd={LocalUrl}\r\nStage=st\r\n\r\n[B]\r\nC=1\r\n",
            PurchaseFlowConfig.Merge(existing, LocalUrl));
    }

    [Fact]
    public void Adds_prod_to_a_section_without_it()
    {
        Assert.Equal($"[EpicPurchaseFlow]\r\nStage=st\r\nProd={LocalUrl}\r\n\r\n[B]\r\nC=1\r\n",
            PurchaseFlowConfig.Merge("[EpicPurchaseFlow]\r\nStage=st\r\n\r\n[B]\r\nC=1\r\n", LocalUrl));
    }

    [Fact]
    public void Keeps_the_files_encoding_and_points_at_an_https_backend()
    {
        using var folder = new TempFolder();
        var ini = folder.Combine("Game.ini");
        File.WriteAllText(ini, "[EpicPurchaseFlow]\nProd=old\n\n[A]\nName=Ünïcode\n", Encoding.Unicode);
        var url = PurchaseFlowConfig.Url(new BackendEndpoint("core.example.com", 443, Secure: true));

        Assert.True(PurchaseFlowConfig.Write(ini, url));

        var bytes = File.ReadAllBytes(ini);
        Assert.Equal([0xFF, 0xFE], bytes[..2]);
        Assert.Equal("[EpicPurchaseFlow]\nProd=https:///core.example.com/purchase\n\n[A]\nName=Ünïcode\n", File.ReadAllText(ini));
    }
}
