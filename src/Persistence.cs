using System.Diagnostics;

namespace SimpleNvidiaUndervolt;

/// <summary>Reports errors to stderr. When no console is watching (a shortcut or the logon task), the
/// <c>--interactive</c> message box surfaces them instead — it captures stderr too.</summary>
internal static class ErrorReporter
{
    public static void Report(string message) => Console.Error.WriteLine(message);
}

/// <summary>Makes an undervolt survive a reboot: copies the app into LocalAppData and registers a
/// Task Scheduler logon task that re-runs it (with <c>--interactive errors</c>, so a startup failure is
/// visible).
/// The <c>unpersist</c> command undoes both.</summary>
internal static class Persistence
{
    public const string TaskName = "nvidia-simple-undervolt";

    public static IReadOnlyList<string> Install(string[] undervoltArgs)
    {
        var log = new List<string>();

        string targetDir = InstallDir();
        string targetExe = CopyApp(targetDir);
        log.Add($"Installed to {targetDir}");

        // Run at logon (not at boot): the task then lives in the user's interactive session, so it can
        // reach the GPU and an --interactive error box is actually on screen. /RL HIGHEST runs it
        // elevated, which the driver writes need.
        string taskRun = $"\"{targetExe}\" {CommandLine.Join(StartupArgs(undervoltArgs))}";
        RunSchtasks("/Create", "/F", "/TN", TaskName, "/SC", "ONLOGON", "/RL", "HIGHEST", "/TR", taskRun);
        log.Add($"Registered logon task '{TaskName}'.");
        log.Add($"Runs: {taskRun}");

        return log;
    }

    /// <summary>Undoes <see cref="Install"/> by removing the logon task, so the undervolt no longer
    /// re-applies at startup. The installed copy in LocalAppData is left in place. A missing task is
    /// reported, not an error.</summary>
    public static IReadOnlyList<string> Uninstall()
    {
        if (!TaskExists())
        {
            return new[] { $"No logon task '{TaskName}' was registered." };
        }

        RunSchtasks("/Delete", "/TN", TaskName, "/F");
        return new[] { $"Removed logon task '{TaskName}'; the undervolt will no longer re-apply at startup." };
    }

    private static string InstallDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "nvidia-simple-undervolt");

    /// <summary>Copies the running app (the executable plus its runtime sidecar files) into
    /// <paramref name="targetDir"/> and returns the path of the copied executable.</summary>
    private static string CopyApp(string targetDir)
    {
        string sourceDir = AppContext.BaseDirectory;
        string exeName = Path.GetFileName(Environment.ProcessPath
            ?? throw new NvApiException("can't determine the running executable's path."));
        Directory.CreateDirectory(targetDir);

        // Skip the copy when already running from the install location (re-persisting in place).
        if (!PathsEqual(sourceDir, targetDir))
        {
            foreach (string file in Directory.GetFiles(sourceDir))
            {
                File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
            }
        }

        return Path.Combine(targetDir, exeName);
    }

    /// <summary>The args the logon task runs: the original undervolt args stripped of every flag that
    /// doesn't belong to an unattended, already-elevated re-apply, then the fixed set this task needs.
    /// The task is itself the persistence, runs from no fixed directory and is already elevated, so it
    /// must not re-persist (<c>--no-persist</c>), must not touch links (<c>--no-shortcut-rename</c>), and
    /// surfaces failures through a box (<c>--interactive errors</c>) since no console is watching.</summary>
    internal static IReadOnlyList<string> StartupArgs(string[] args)
    {
        List<string> kept = CommandLine.StripFlags(args,
            bare: new[] { "--no-persist", "--dry-run", "--no-shortcut-rename", "--no-elevate" },
            withValue: new[] { "--pipe-name", "--shortcut-name" },
            withOptionalValue: new[] { "--interactive", "--save-shortcut" });

        kept.Add("--no-persist");
        kept.Add("--no-shortcut-rename");
        kept.Add("--interactive");
        kept.Add("errors");
        return kept;
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static bool TaskExists() => RunSchtasks(throwOnError: false, "/Query", "/TN", TaskName).ExitCode == 0;

    private static (int ExitCode, string Error) RunSchtasks(params string[] arguments)
        => RunSchtasks(throwOnError: true, arguments);

    private static (int ExitCode, string Error) RunSchtasks(bool throwOnError, params string[] arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(psi)
            ?? throw new NvApiException("could not start schtasks.exe.");
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (throwOnError && process.ExitCode != 0)
        {
            throw new NvApiException($"schtasks failed: {error.Trim()}");
        }

        return (process.ExitCode, error);
    }
}
