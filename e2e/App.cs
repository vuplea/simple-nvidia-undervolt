using System.Globalization;

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
        var (exitCode, output, error) = ChildProcess.RunIn(workingDir, ExePath(), args);
        return (exitCode, error.Length == 0 ? output : $"{output}\n{error}");
    }

    /// <summary>Runs <c>tune</c> with the given tuning flags, never persisting, and skips the test
    /// when the curve didn't read back cleanly (a brief transitional state).</summary>
    public static (int ExitCode, string Output) RunUndervolt(params string[] tuning)
    {
        var (exitCode, output) = Run(null, tuning.Append("--no-persist").Prepend("tune").ToArray());
        SkipIfCurveTransient(output);
        return (exitCode, output);
    }

    /// <summary>Skips the calling test when the run was rejected because the V/F curve read back
    /// collapsed — a brief power-state transition, so a retry condition rather than a failure.</summary>
    public static void SkipIfCurveTransient(string output)
        => Skip.If(output.Contains(GpuTuning.TransientReadMarker),
            "the curve didn't read back cleanly (a brief transitional state), so the run was rejected - retry.");

    /// <summary>Asserts the run verified its write against the effective curve: the confirmation printed
    /// and the "offsets don't match / reverted" path was not taken.</summary>
    public static void AssertWriteConfirmed(string output)
    {
        Assert.Contains("Confirming operating point", output);
        Assert.DoesNotContain("didn't change after writing", output);
    }

    /// <summary>A number as a command-line argument — invariant, like every number this CLI parses.</summary>
    public static string Arg(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The absolute P0 memory clock, which moves with an applied memory offset — what the
    /// memory round-trip assertions compare. Fails the test on an unreadable clock.</summary>
    public static int ReadMemoryClockKhz(IntPtr gpu)
    {
        Reading<int> clock = TuningSnapshot.Read(gpu).MemoryClockKhz;
        Assert.True(clock.Ok, clock.Error);
        return clock.Value;
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
