using System.ComponentModel;
using System.Diagnostics;

namespace SimpleNvidiaUndervolt;

/// <summary>Runs a console helper (schtasks, powershell) hidden and synchronously — the one place the
/// start/drain/wait boilerplate lives. Output is captured rather than inherited so the helper's chatter
/// (e.g. schtasks' "SUCCESS:") doesn't interleave with the app's own.</summary>
internal static class ChildProcess
{
    /// <summary>How long a helper may run before it is killed. Generous — schtasks and the shortcut
    /// write finish in seconds, and the e2e suite's task-driving script polls for up to a minute —
    /// but bounded: a hung helper would otherwise hang the whole command, and the silent logon
    /// re-apply would sit in Task Scheduler as still running with nothing on screen.</summary>
    private const int TimeoutMs = 120_000;

    /// <summary>Runs <paramref name="exe"/> with the given arguments and returns its exit code and
    /// trimmed stdout/stderr. Throws when the process cannot be started at all, or when it doesn't
    /// finish within <see cref="TimeoutMs"/> (it is then killed).</summary>
    public static (int ExitCode, string Output, string Error) Run(string exe, params string[] arguments)
        => RunIn(null, exe, arguments);

    /// <summary>Runs a PowerShell script non-interactively — how the app reaches COM the AOT build
    /// can't activate in-process (WScript.Shell shortcuts).</summary>
    public static (int ExitCode, string Output, string Error) RunPowerShell(string script)
        => Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script);

    /// <summary>Like <see cref="Run"/>, with the child started in <paramref name="workingDir"/> rather
    /// than inheriting this process's current directory (null inherits).</summary>
    public static (int ExitCode, string Output, string Error) RunIn(string? workingDir, string exe,
        params string[] arguments)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDir is not null)
        {
            psi.WorkingDirectory = workingDir;
        }

        foreach (string arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process process = StartHelper(psi, exe);

        // Drain both pipes concurrently: reading one to end before the other can deadlock if the child
        // fills the second pipe's buffer while we're blocked on the first.
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeoutMs))
        {
            // Kill the whole tree - a helper may itself be waiting on a child it spawned.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // It exited in the race with the kill (or can't be killed); the timeout is still the story.
            }

            throw new CliError($"{exe} didn't finish within {TimeoutMs / 1000} seconds and was terminated.");
        }

        // The pipes can outlive the exited helper if it spawned a child that inherited them; bound
        // the drain like the wait, so an inherited handle can't hang the command either.
        if (!Task.WaitAll(new Task[] { outputTask, errorTask }, TimeoutMs))
        {
            throw new CliError($"{exe} exited, but its output didn't complete within "
                                + $"{TimeoutMs / 1000} seconds (a process it spawned may still hold its pipes).");
        }

        return (process.ExitCode, outputTask.Result.Trim(), errorTask.Result.Trim());
    }

    /// <summary>Starts the helper, converting a start failure (a missing or blocked executable) to a
    /// <see cref="CliError"/>: an environment problem to report as a message, not a bug with a stack
    /// (see <see cref="ErrorReporter.Describe"/>).</summary>
    private static Process StartHelper(ProcessStartInfo psi, string exe)
    {
        try
        {
            return Process.Start(psi) ?? throw new CliError($"Could not start {exe}.");
        }
        catch (Win32Exception ex)
        {
            throw new CliError($"Could not start {exe}: {ex.Message}");
        }
    }
}
