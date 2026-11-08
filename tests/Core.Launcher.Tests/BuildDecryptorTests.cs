using Core.Launcher.Hosting.Builds;

namespace Core.Launcher.Tests;

public sealed class BuildDecryptorTests
{
    [Fact]
    public void A_non_packed_exe_is_left_untouched_with_no_backup_kept()
    {
        using var folder = new TempFolder();
        var exe = folder.Combine("FortniteClient-Win64-Shipping.exe");
        var original = new byte[2048];
        Random.Shared.NextBytes(original);
        File.WriteAllBytes(exe, original);

        var log = new List<string>();
        new BuildDecryptor(log.Add).Decrypt(exe, "Fortnite 1.8", enabled: true);

        Assert.Equal(original, File.ReadAllBytes(exe));
        Assert.False(File.Exists(exe + BuildDecryptor.BackupSuffix));
        Assert.Contains(log, line => line.Contains("isn't packed"));
    }

    [Fact]
    public void Progress_is_idle_again_once_decryption_returns()
    {
        using var folder = new TempFolder();
        var exe = folder.Combine("FortniteClient-Win64-Shipping.exe");
        File.WriteAllBytes(exe, new byte[2048]);

        var decryptor = new BuildDecryptor(_ => { });
        var ranWhileRunning = false;
        decryptor.Changed += () => ranWhileRunning |= decryptor.Progress.Running;

        decryptor.Decrypt(exe, "Fortnite 1.8", enabled: true);

        Assert.True(ranWhileRunning);
        Assert.False(decryptor.Progress.Running);
        Assert.Equal(DecryptionProgress.Idle, decryptor.Progress);
    }

    [Fact]
    public void A_missing_exe_is_caught_and_never_throws()
    {
        using var folder = new TempFolder();
        var exe = folder.Combine("missing.exe");

        var log = new List<string>();
        var exception = Record.Exception(() => new BuildDecryptor(log.Add).Decrypt(exe, "Fortnite 8.30", enabled: true));

        Assert.Null(exception);
        Assert.Contains(log, line => line.Contains("Couldn't decrypt"));
    }

    [Fact]
    public void Disabled_restores_the_packed_original_when_the_exe_was_decrypted()
    {
        using var folder = new TempFolder();
        var exe = folder.Combine("FortniteClient-Win64-Shipping.exe");
        var packed = new byte[2048];
        Random.Shared.NextBytes(packed);
        File.WriteAllBytes(exe + BuildDecryptor.BackupSuffix, packed);     // the untouched original
        File.WriteAllBytes(exe, new byte[4096]);                           // a "decrypted" exe differing from it

        var log = new List<string>();
        new BuildDecryptor(log.Add).Decrypt(exe, "Fortnite 4.0", enabled: false);

        Assert.Equal(packed, File.ReadAllBytes(exe));
        Assert.Contains(log, line => line.Contains("restored") && line.Contains("packed"));
    }

    [Fact]
    public void Disabled_does_nothing_without_a_backup()
    {
        using var folder = new TempFolder();
        var exe = folder.Combine("FortniteClient-Win64-Shipping.exe");
        var original = new byte[2048];
        Random.Shared.NextBytes(original);
        File.WriteAllBytes(exe, original);

        var log = new List<string>();
        new BuildDecryptor(log.Add).Decrypt(exe, "Fortnite 1.8", enabled: false);

        Assert.Equal(original, File.ReadAllBytes(exe));
        Assert.Empty(log);
    }
}
