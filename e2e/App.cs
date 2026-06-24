using System.Diagnostics;

namespace SimpleNvidiaUndervolt.E2E;

/// <summary>Runs the real built executable (<c>src/bin/simple-nvidia-undervolt.exe</c>) as a process —
/// the actual shipping artifact. The e2e tests drive the GPU/shortcut/persistence side effects through
/// this and verify the results with direct library calls.</summary>
internal static class App
{
    /// <summary>Runs the app with the given args (optionally from <paramref name="workingDir"/>) and
    /// returns its exit code and combined stdout+stderr.</summary>
    public static (int ExitCode, string Output) Run(string? workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo(ExePath())
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

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("could not start the app.");
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    /// <summary>The built executable, found by walking up from the test output to the repo's src/bin.</summary>
    public static string ExePath()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "bin", "simple-nvidia-undervolt.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("could not locate simple-nvidia-undervolt.exe - build src first.");
    }
}
