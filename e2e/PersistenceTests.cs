namespace SimpleNvidiaUndervolt.E2E;

/// <summary>
/// End-to-end test for persistence through the real executable: it registers the logon task by running
/// the exe (persistence is default-on), then drives that task through Task Scheduler and checks it ran.
///
/// The app uses a fixed task name and Program Files install path, so this mutates machine-global
/// state the user may rely on — it runs inside a <see cref="PersistenceBackup"/>, which puts the
/// original task registration and install folder back afterwards.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class PersistenceTests
{
    private readonly GpuFixture _gpu;

    public PersistenceTests(GpuFixture gpu) => _gpu = gpu;

    [SkippableFact]
    public void Persist_RegistersATaskThatTaskSchedulerRunsSuccessfully()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);

        string task = Persistence.TaskName;
        PersistenceBackup backup = PersistenceBackup.Create();
        try
        {
            // Register by running the real exe (persistence is the default for a real undervolt). This
            // also copies the app into the install folder, overwriting it - hence the backup above.
            var (exitCode, output) = App.Run(null, "tune", "--mv", "925");
            App.SkipIfCurveTransient(output);
            Assert.Equal(0, exitCode);
            Assert.Contains("Registered logon task", output);
            Assert.True(PersistenceBackup.TaskExists(task));

            // Drive the registered task through Task Scheduler and wait for it to finish.
            Assert.Equal(0, RunTaskAndWait(task));

            // 'clear' restores stock and removes the task it just registered.
            var (clearCode, _) = App.Run(null, "clear");
            Assert.Equal(0, clearCode);
            Assert.False(PersistenceBackup.TaskExists(task));
        }
        finally
        {
            backup.Restore();
        }
    }

    /// <summary>Starts the task via Task Scheduler, waits for that run to finish, and returns its
    /// result. The wait keys on <c>LastRunTime</c> advancing past its pre-start value — a fixed sleep
    /// could read the <em>previous</em> run's result if the task were slow to start. A run that never
    /// finishes (a failure would hold a message box open) is stopped rather than left behind, and fails
    /// the test.</summary>
    private static int RunTaskAndWait(string task)
    {
        string script =
            $"$t = '{task}';"
            + "$before = (Get-ScheduledTask -TaskName $t | Get-ScheduledTaskInfo).LastRunTime;"
            + "Start-ScheduledTask -TaskName $t;"
            + "for ($i = 0; $i -lt 200; $i++) {"
            + "  $info = Get-ScheduledTask -TaskName $t | Get-ScheduledTaskInfo;"
            + "  if ($info.LastRunTime -ne $before -and (Get-ScheduledTask -TaskName $t).State -ne 'Running')"
            + "    { Write-Output $info.LastTaskResult; exit 0 }"
            + "  Start-Sleep -Milliseconds 300"
            + "};"
            + "Stop-ScheduledTask -TaskName $t;"
            + "Write-Output 'TIMEOUT'";

        var (_, output, error) = ChildProcess.RunPowerShell(script);
        if (output == "TIMEOUT")
        {
            throw new InvalidOperationException(
                "the task didn't finish within 60s and was stopped (a failed re-apply holds its "
                + "failure box open - check the GPU state).");
        }

        if (!int.TryParse(output, out int result))
        {
            throw new InvalidOperationException(
                "could not read the task's last result from Task Scheduler "
                + $"(output: '{output}'{(error.Length == 0 ? "" : $", stderr: '{error}'")}).");
        }

        return result;
    }
}
