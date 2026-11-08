namespace Core.Launcher.Hosting.Builds;

/// <summary>What decryption is doing to one build's exe right now, so the UI can show a status and a progress bar.</summary>
public sealed record DecryptionProgress(bool Running, string Build, string Step)
{
    public static readonly DecryptionProgress Idle = new(false, "", "");

    /// <summary>"Decrypting Fortnite 4.0 — Reading the decrypted image".</summary>
    public string Describe() => Step.Length == 0 ? $"Decrypting {Build}" : $"Decrypting {Build} — {Step}";
}

/// <summary>
/// Un-packs a build's shipping exe before launch when the launcher is set to. Builds 1.9.1+ are Arxan-packed and
/// <see cref="ExeDecryptor"/> decrypts them in place; this runs it (reporting each step so the launcher can show
/// progress) when the setting is on, and puts the packed original back when it is off.
/// <para>
/// The exe is copied to <c>&lt;exe&gt;.ORIGINAL.bak</c> before the first decrypt and never modified unless that backup
/// exists, so the packed original is always recoverable. Any failure — including a build the decryptor does not yet
/// support, which throws — is caught and logged; the exe is restored from the backup in case a write was left
/// half-done, and the launch goes ahead on whatever exe is on disk rather than failing.
/// </para>
/// </summary>
public sealed class BuildDecryptor
{
    /// <summary>Appended to the exe path to hold the untouched packed original.</summary>
    public const string BackupSuffix = ".ORIGINAL.bak";

    private readonly Action<string> _log;
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public BuildDecryptor(Action<string> log) => _log = log;

    /// <summary>The build whose exe is being decrypted, and the latest step; <see cref="DecryptionProgress.Idle"/> when none is.</summary>
    public DecryptionProgress Progress { get; private set; } = DecryptionProgress.Idle;

    /// <summary>Raised whenever <see cref="Progress"/> changes (on the thread doing the decryption).</summary>
    public event Action? Changed;

    /// <summary>
    /// With <paramref name="enabled"/> on, decrypts <paramref name="executable"/> in place if it is packed, backing it
    /// up first; with it off, puts the packed original back when a previous run decrypted it. Never throws: a failure is
    /// logged and the exe is left ready to launch. Runs on the caller's thread (a background one) and takes a few seconds.
    /// </summary>
    public void Decrypt(string executable, string buildName, bool enabled)
    {
        var label = buildName.Length > 0 ? buildName : Path.GetFileName(executable);
        var backup = executable + BackupSuffix;

        if (!enabled)
        {
            // Decryption is off: undo a decrypt from a previous run so the build launches as its untouched packed exe.
            RevertToPacked(executable, backup, label);
            return;
        }

        var hadBackup = File.Exists(backup);
        try
        {
            Report(new DecryptionProgress(true, label, hadBackup ? "Checking the build" : "Backing up the original exe"));
            if (!hadBackup)
            {
                // Must come before any write: the exe is only ever modified once this copy of the original exists.
                File.Copy(executable, backup);
            }

            var result = ExeDecryptor.Decrypt(executable, step => Report(Progress with { Step = step }));

            // A build that isn't packed is never modified, so a backup made for it this run is just wasted disk; drop it.
            if (!hadBackup && result == ExeDecryptor.Result.NotEncrypted)
            {
                TryDelete(backup);
            }

            LogOnce(executable, result switch
            {
                ExeDecryptor.Result.Decrypted => $"Decrypted {label}'s exe ({executable}); the packed original is at {Path.GetFileName(backup)}",
                ExeDecryptor.Result.AlreadyDecrypted => $"{label}'s exe is already decrypted",
                _ => $"{label}'s exe isn't packed; launching it as is",
            });
        }
        catch (Exception exception)
        {
            // Build 8.30 and any other build the decryptor can't handle land here; the game still launches.
            _log($"Couldn't decrypt {label}'s exe ({exception.Message}); launching it as is");
            Restore(executable, backup);
        }
        finally
        {
            Report(DecryptionProgress.Idle);
        }
    }

    /// <summary>
    /// Puts the packed original back after a failed decrypt, in case a write was left half-done. The backup is kept: it
    /// was taken before any write, so it is a real original, and keeping it means a build that fails every launch (an
    /// unsupported one) is not re-copied each time.
    /// </summary>
    private void Restore(string executable, string backup)
    {
        if (!File.Exists(backup))
        {
            return;
        }

        try
        {
            File.Copy(backup, executable, overwrite: true);
            _log($"Restored {executable} from {Path.GetFileName(backup)}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log($"Couldn't restore {executable} from {Path.GetFileName(backup)} ({exception.Message})");
        }
    }

    /// <summary>
    /// Restores the packed original when decryption is off and a previous run decrypted the exe. Does nothing when there
    /// is no backup, or the exe already matches it (never decrypted, or already reverted).
    /// </summary>
    private void RevertToPacked(string executable, string backup, string label)
    {
        try
        {
            if (!File.Exists(backup) || !File.Exists(executable) || FilesEqual(executable, backup))
            {
                return;
            }

            File.Copy(backup, executable, overwrite: true);
            _log($"Decryption is off; restored {label}'s packed exe from {Path.GetFileName(backup)}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log($"Couldn't restore {label}'s packed exe from {Path.GetFileName(backup)} ({exception.Message})");
        }
    }

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        using var a = File.OpenRead(left);
        using var b = File.OpenRead(right);
        Span<byte> bufferA = stackalloc byte[8192];
        Span<byte> bufferB = stackalloc byte[8192];
        int read;
        while ((read = a.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false)) > 0)
        {
            b.ReadExactly(bufferB[..read]);
            if (!bufferA[..read].SequenceEqual(bufferB[..read]))
            {
                return false;
            }
        }

        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void LogOnce(string executable, string message)
    {
        lock (_gate)
        {
            if (!_reported.Add(executable))
            {
                return;
            }
        }

        _log(message);
    }

    private void Report(DecryptionProgress progress)
    {
        Progress = progress;
        Changed?.Invoke();
    }
}
