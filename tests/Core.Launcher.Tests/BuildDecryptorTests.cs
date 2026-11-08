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

    [Fact]
    public void Enabled_launches_an_exe_it_already_decrypted_as_it_is_even_while_it_runs()
    {
        // #17: leaving the toggle on re-decrypted the decrypted exe on every launch, and a server running it made the
        // client's attempt fail and try to restore over it.
        using var folder = new TempFolder();
        var exe = folder.Combine("FortniteClient-Win64-Shipping.exe");
        var decrypted = ExeDecryptorTests.SyntheticExe(textSections: 2, extraSection: ".cimp");
        File.WriteAllBytes(exe, decrypted);
        File.WriteAllBytes(exe + BuildDecryptor.BackupSuffix, ExeDecryptorTests.SyntheticExe(textSections: 2));
        File.WriteAllText(exe + BuildDecryptor.MarkerSuffix, "");

        var log = new List<string>();
        using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))   // a running game: no writers
        {
            new BuildDecryptor(log.Add).Decrypt(exe, "Fortnite 4.2", enabled: true);
        }

        Assert.Equal(decrypted, File.ReadAllBytes(exe));
        Assert.Contains(log, line => line.Contains("already decrypted"));
        Assert.DoesNotContain(log, line => line.Contains("Couldn't"));
    }

    [Fact]
    public void Enabled_starts_again_from_the_backup_when_no_decrypt_of_its_own_is_recorded()
    {
        // Launcher 1.0.34 kept no marker and may have rewritten the exe more than once; the original is the way back.
        using var folder = new TempFolder();
        var exe = folder.Combine("FortniteClient-Win64-Shipping.exe");
        var original = ExeDecryptorTests.SyntheticExe(textSections: 1);   // not packed, so nothing is launched
        File.WriteAllBytes(exe + BuildDecryptor.BackupSuffix, original);
        File.WriteAllBytes(exe, ExeDecryptorTests.SyntheticExe(textSections: 2, extraSection: ".cimp"));

        var log = new List<string>();
        new BuildDecryptor(log.Add).Decrypt(exe, "Fortnite 4.2", enabled: true);

        Assert.Equal(original, File.ReadAllBytes(exe));
        Assert.True(File.Exists(exe + BuildDecryptor.BackupSuffix));
        Assert.Contains(log, line => line.Contains("afresh"));
    }

    [Fact]
    public void Disabled_restores_the_original_and_drops_the_decrypted_marker()
    {
        using var folder = new TempFolder();
        var exe = folder.Combine("FortniteClient-Win64-Shipping.exe");
        var packed = ExeDecryptorTests.SyntheticExe(textSections: 2);
        File.WriteAllBytes(exe + BuildDecryptor.BackupSuffix, packed);
        File.WriteAllBytes(exe, ExeDecryptorTests.SyntheticExe(textSections: 2, extraSection: ".cimp"));
        File.WriteAllText(exe + BuildDecryptor.MarkerSuffix, "");

        new BuildDecryptor(_ => { }).Decrypt(exe, "Fortnite 4.2", enabled: false);

        Assert.Equal(packed, File.ReadAllBytes(exe));
        Assert.False(File.Exists(exe + BuildDecryptor.MarkerSuffix));
    }
}
