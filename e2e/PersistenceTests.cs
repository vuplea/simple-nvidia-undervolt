using System.Diagnostics;
using System.Text;

namespace SimpleNvidiaUndervolt.E2E;

/// <summary>
/// End-to-end test for persistence through the real executable: it registers the logon task by running
/// the exe (persistence is default-on), then drives that task through Task Scheduler and checks it ran.
///
/// The app uses a fixed task name and <c>%LOCALAPPDATA%</c> install path, so this mutates machine-global
/// state the user may rely on. It backs up both the existing <c>nvidia-simple-undervolt</c> registration
/// (byte-for-byte) and the install folder, and restores both afterwards (the task restore is verified). A
/// backup that can't be restored is kept and the test fails loudly with its path, rather than leaving the
/// wrong task or a clobbered install in place.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class PersistenceTests
{
    [SkippableFact]
    public void Persist_RegistersATaskThatTaskSchedulerRunsSuccessfully()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);

        string task = Persistence.TaskName;
        string taskFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks", task);
        string installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "nvidia-simple-undervolt");

        string? taskBackup = BackUpFile(taskFile);
        string? installBackup = BackUpDirectory(installDir);
        try
        {
            // Register by running the real exe (persistence is the default for a real undervolt). This
            // also copies the app into installDir, overwriting it - hence the backup above.
            var (exitCode, output) = App.Run(null, "undervolt", "--mv", "925", "--no-shortcut-rename");
            Skip.If(output.Contains("the GPU is idle"),
                "the GPU is idle, so the undervolt was rejected before persisting - run a 3D load and retry.");
            Assert.Equal(0, exitCode);
            Assert.Contains("Registered logon task", output);
            Assert.True(TaskExists(task));

            // Drive the registered task through Task Scheduler and wait for it to finish.
            Assert.Equal(0, RunTaskAndWait(task));

            // 'unpersist' removes the task it just registered.
            var (unpersistCode, _) = App.Run(null, "unpersist");
            Assert.Equal(0, unpersistCode);
            Assert.False(TaskExists(task));
        }
        finally
        {
            RestoreDirectory(installDir, installBackup);
            RestoreTask(task, taskBackup);
        }
    }

    private readonly GpuFixture _gpu;

    public PersistenceTests(GpuFixture gpu) => _gpu = gpu;

    private static bool TaskExists(string task) => Schtasks("/Query", "/TN", task).ExitCode == 0;

    // --- task registration backup/restore ---

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
            Schtasks("/Delete", "/TN", task, "/F");
            return;
        }

        int exitCode = Schtasks("/Create", "/TN", task, "/XML", backup, "/F").ExitCode;
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"failed to restore the original '{task}' task (schtasks exit {exitCode}); backup kept at {backup}");
        }

        File.Delete(backup);
    }

    // --- install-folder backup/restore ---

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

    // --- Task Scheduler helpers ---

    /// <summary>Starts the task via Task Scheduler, waits for it to stop, and returns its last result.</summary>
    private static int RunTaskAndWait(string task)
    {
        string script =
            $"Start-ScheduledTask -TaskName '{task}';"
            + "Start-Sleep -Milliseconds 500;"
            + $"for ($i = 0; $i -lt 200 -and (Get-ScheduledTask -TaskName '{task}').State -eq 'Running'; $i++)"
            + " { Start-Sleep -Milliseconds 300 }"
            + $"(Get-ScheduledTask -TaskName '{task}' | Get-ScheduledTaskInfo).LastTaskResult";

        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using Process process = Process.Start(psi)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return int.Parse(output.Trim());
    }

    private static (int ExitCode, string Output) Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.Unicode, // /XML emits UTF-16
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(psi)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}
