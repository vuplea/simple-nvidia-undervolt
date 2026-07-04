namespace SimpleNvidiaUndervolt.E2E;

/// <summary>
/// Backup of the machine-global state the app's persistence owns: the logon-task registration
/// (byte-for-byte, so the XML declaration and BOM survive re-registration) and the Program Files
/// install folder. Both belong to the user — a live undervolt they persisted — so every test that runs
/// a persisting undervolt or <c>clear</c> wraps itself in one of these and calls <see cref="Restore"/>
/// when it finishes. A restore that fails keeps the backup and throws with its path, rather than
/// leaving the wrong task or a clobbered install in place.
/// </summary>
internal sealed class PersistenceBackup
{
    private readonly string? _taskBackup;
    private readonly string? _installBackup;

    private PersistenceBackup(string? taskBackup, string? installBackup)
    {
        _taskBackup = taskBackup;
        _installBackup = installBackup;
    }

    public static PersistenceBackup Create()
    {
        string taskFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "Tasks", Persistence.TaskName);
        return new PersistenceBackup(BackUpFile(taskFile), BackUpDirectory(Persistence.InstallDir()));
    }

    public void Restore()
    {
        // Restore the task even when the directory restore fails (a lingering app process - say a
        // message box left open - locks the installed exe): losing the user's logon registration is
        // strictly worse than leaving a stale install, and the task points at a fixed path anyway.
        try
        {
            RestoreDirectory(Persistence.InstallDir(), _installBackup);
        }
        finally
        {
            RestoreTask(Persistence.TaskName, _taskBackup);
        }
    }

    public static bool TaskExists(string task)
        => ChildProcess.Run("schtasks.exe", "/Query", "/TN", task).ExitCode == 0;

    /// <summary>Copies the task's on-disk XML to a temp file (exact bytes, so the BOM is preserved), or
    /// returns null if the task isn't registered.</summary>
    private static string? BackUpFile(string taskFile)
    {
        if (!File.Exists(taskFile))
        {
            return null;
        }

        string backup = Path.Combine(Path.GetTempPath(), "nvundervolt-task-backup-" + Guid.NewGuid().ToString("N") + ".xml");
        File.Copy(taskFile, backup, overwrite: true);
        return backup;
    }

    /// <summary>Re-registers the original task from its byte-faithful backup (and confirms schtasks
    /// accepted it), or deletes the task if there was none. A restore that schtasks rejects keeps the
    /// backup and fails the test with its path, rather than leaving the wrong task in place.</summary>
    private static void RestoreTask(string task, string? backup)
    {
        if (backup is null)
        {
            ChildProcess.Run("schtasks.exe", "/Delete", "/TN", task, "/F");
            return;
        }

        var (exitCode, _, error) = ChildProcess.Run("schtasks.exe", "/Create", "/TN", task, "/XML", backup, "/F");
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"failed to restore the original '{task}' task (schtasks exit {exitCode}: {error}); backup kept at {backup}");
        }

        File.Delete(backup);
    }

    /// <summary>Copies the install folder to a temp backup, or returns null if it doesn't exist.</summary>
    private static string? BackUpDirectory(string installDir)
    {
        if (!Directory.Exists(installDir))
        {
            return null;
        }

        string backup = Path.Combine(Path.GetTempPath(), "nvundervolt-install-backup-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(installDir, backup);
        return backup;
    }

    /// <summary>Restores the install folder from its backup (replacing whatever the run wrote), or removes
    /// the folder if there was none. A failed restore keeps the backup and fails the test with its path.</summary>
    private static void RestoreDirectory(string installDir, string? backup)
    {
        try
        {
            if (Directory.Exists(installDir))
            {
                Directory.Delete(installDir, recursive: true);
            }

            if (backup is not null)
            {
                CopyDirectory(backup, installDir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"failed to restore the install folder '{installDir}'; backup kept at {backup}", ex);
        }

        if (backup is not null)
        {
            try { Directory.Delete(backup, recursive: true); } catch (IOException) { }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
