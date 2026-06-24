namespace SimpleNvidiaUndervolt.E2E;

/// <summary>
/// End-to-end tests that run the real executable for the action and verify the result with direct library
/// calls (reading the GPU through <see cref="NvApi"/>/<see cref="GpuTuning"/>, or inspecting files). The
/// test host is elevated, so a write command runs in place rather than prompting. Writes go through the
/// shared <see cref="GpuFixture"/>, which restores the original tuning when the suite ends.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class CliTests
{
    private readonly GpuFixture _gpu;

    public CliTests(GpuFixture gpu) => _gpu = gpu;

    [SkippableFact]
    public void Status_ReportsTheTuning()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);

        var (exitCode, output) = App.Run(null, "status");

        Assert.Equal(0, exitCode);
        Assert.Contains("Core curve offset", output);
        Assert.Contains("Memory clock", output);
    }

    [SkippableFact]
    public void DryRunUndervolt_PrintsThePlanButWritesNothing()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);

        int[] before = CurveDeltasKhz();

        var (exitCode, output) = App.Run(null, "undervolt", "--mv", "925", "--dry-run", "--no-persist", "--no-shortcut-rename");

        Assert.Equal(0, exitCode);
        Assert.Contains("[dry run]", output);
        Assert.Equal(before, CurveDeltasKhz()); // nothing was written
    }

    [SkippableFact]
    public void Clear_ResetsTheCurveAndVoltageBoostToStock()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);

        var (exitCode, _) = App.Run(null, "clear");

        Assert.Equal(0, exitCode);
        Assert.All(CurveDeltasKhz(), d => Assert.Equal(0, d));
        Assert.Equal(0u, NvApi.GetCoreVoltageBoostPercent(_gpu.Gpu));
    }

    [SkippableFact]
    public void Undervolt_WithMemoryOffset_AppliesIt()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);

        int baseMhz = GpuTuning.BaseMemoryClockMhz(_gpu.Gpu);

        // The memory write goes in before the curve, so it lands even at idle (when the curve is skipped).
        var (exitCode, _) = App.Run(null,
            "undervolt", "--mv", "925", "--mem-offset", "100", "--no-persist", "--no-shortcut-rename");

        Assert.Equal(0, exitCode);
        GpuTuning after = GpuTuning.Read(_gpu.Gpu);
        Assert.True(after.MemoryClockKhz.Ok, after.MemoryClockKhz.Error);
        Assert.Equal(baseMhz + 100, after.MemoryClockKhz.Value / 1000);
    }

    [SkippableFact]
    public void Undervolt_UnderLoad_WritesCurveDeltas()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);

        var (exitCode, output) = App.Run(null, "undervolt", "--mv", "900", "--no-persist", "--no-shortcut-rename");

        Assert.Equal(0, exitCode);
        Skip.If(output.Contains("the GPU is idle"),
            "the GPU is idle, so the curve flatten was skipped - run a 3D load and retry.");

        // The cap flatten wrote per-anchor deltas to the curve.
        Assert.Contains(CurveDeltasKhz(), d => d != 0);
    }

    [SkippableFact]
    public void SaveShortcut_WritesASelfTargetingLnkWithBakedArgs()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);

        InTempDirectory(temp =>
        {
            // --dry-run so it only writes the .lnk; the exe runs with temp as its working directory.
            var (exitCode, _) = App.Run(temp, "undervolt", "--mv", "925", "--mhz", "2880", "--dry-run", "--save-shortcut");

            Assert.Equal(0, exitCode);
            string lnk = Path.Combine(temp, "Undervolt 925mV 2880MHz.lnk");
            Assert.True(File.Exists(lnk), $"expected the shortcut at {lnk}");

            var (target, arguments, workingDir) = Lnk.Read(lnk);
            Assert.Equal(App.ExePath(), target);
            Assert.Equal(temp, workingDir.TrimEnd('\\'));
            Assert.Equal(
                "undervolt --mv 925 --mhz 2880 --shortcut-name \"Undervolt 925mV 2880MHz\" --interactive",
                arguments);
        });
    }

    [SkippableFact]
    public void SaveShortcut_HonoursACustomName()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);

        InTempDirectory(temp =>
        {
            var (exitCode, _) = App.Run(temp, "undervolt", "--mv", "925", "--dry-run", "--save-shortcut", "Quiet");

            Assert.Equal(0, exitCode);
            var (_, arguments, _) = Lnk.Read(Path.Combine(temp, "Quiet.lnk"));
            Assert.Equal("undervolt --mv 925 --shortcut-name Quiet --interactive", arguments);
        });
    }

    [SkippableFact]
    public void Undervolt_MarksTheMatchingLinkActiveOnDisk()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);

        InTempDirectory(temp =>
        {
            Touch(temp, "Undervolt 925mV.lnk");          // matches the settings below -> becomes [ACTIVE]
            Touch(temp, "[ACTIVE] Undervolt 900mV.lnk");  // stale marker -> cleared
            Touch(temp, "Some Game.lnk");                 // unrelated -> left alone

            // A real apply (renames the link); the curve may be skipped at idle but marking still runs.
            var (exitCode, _) = App.Run(temp, "undervolt", "--mv", "925", "--no-persist");

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(temp, "[ACTIVE] Undervolt 925mV.lnk")));
            Assert.False(File.Exists(Path.Combine(temp, "Undervolt 925mV.lnk")));
            Assert.True(File.Exists(Path.Combine(temp, "Undervolt 900mV.lnk")));
            Assert.False(File.Exists(Path.Combine(temp, "[ACTIVE] Undervolt 900mV.lnk")));
            Assert.True(File.Exists(Path.Combine(temp, "Some Game.lnk")));
        });
    }

    private int[] CurveDeltasKhz()
        => NvApi.GetCurveFreqDeltasKhz(_gpu.Gpu, NvApi.GetVfCurve(_gpu.Gpu).Count);

    private static void Touch(string dir, string name) => File.WriteAllText(Path.Combine(dir, name), "");

    private static void InTempDirectory(Action<string> body)
    {
        string temp = Path.Combine(Path.GetTempPath(), "nvundervolt-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            body(temp);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }
}
