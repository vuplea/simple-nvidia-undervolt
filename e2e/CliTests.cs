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

        var (exitCode, output) = App.RunUndervolt("--mv", "925", "--dry-run");

        Assert.Equal(0, exitCode);
        Assert.Contains("[dry run]", output);
        Assert.Equal(before, CurveDeltasKhz()); // nothing was written
    }

    [SkippableFact]
    public void Clear_ResetsTheCurveAndVoltageBoostToStock()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);

        // 'clear' doesn't only reset the GPU - it also removes the real logon task, so shield
        // whatever the user has persisted.
        PersistenceBackup backup = PersistenceBackup.Create();
        try
        {
            var (exitCode, _) = App.Run(null, "clear");

            Assert.Equal(0, exitCode);
            Assert.All(CurveDeltasKhz(), d => Assert.Equal(0, d));
            Assert.Equal(0u, NvApi.GetCoreVoltageBoostPercent(_gpu.Gpu));
        }
        finally
        {
            backup.Restore();
        }
    }

    [SkippableFact]
    public void Undervolt_WithMemoryOffset_AppliesIt()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);

        int baseMhz = GpuTuning.BaseMemoryClockMhz(_gpu.Gpu);

        // A real undervolt needs a clean curve read, so the whole apply (memory included) is rejected
        // (and the test skipped) on a collapsed read.
        var (exitCode, _) = App.RunUndervolt("--mv", "925", "--mem-offset", "100");

        Assert.Equal(0, exitCode);
        TuningSnapshot after = TuningSnapshot.Read(_gpu.Gpu);
        Assert.True(after.MemoryClockKhz.Ok, after.MemoryClockKhz.Error);
        Assert.Equal(baseMhz + 100, after.MemoryClockKhz.Value / 1000);
    }

    [SkippableFact]
    public void Undervolt_WritesCurveDeltasAndVerifiesTheyLanded()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);

        var (exitCode, output) = App.RunUndervolt("--mv", "900");

        Assert.Equal(0, exitCode);
        Assert.Contains(CurveDeltasKhz(), d => d != 0);           // the cap flatten wrote per-anchor deltas
        App.AssertWriteConfirmed(output);
    }

    // A large change in the safe (reduction) direction: a deep cap, or a clock well below stock, flattens
    // a wide span down. The driver may smooth such a request short of the request (it floors the top
    // clock), but the write still lands - so the verification must confirm it and never take the revert
    // path. This is the case the reduction-based check is built to survive; guard it on real hardware.
    [SkippableTheory]
    [InlineData("--mv", "850")]                    // deep voltage cap, stock clock at the cap
    [InlineData("--mv", "1000", "--mhz", "2000")]  // a clock the driver smooths short of
    [InlineData("--mv", "1000", "--mhz", "1500")]  // an even larger reduction
    public void Undervolt_LargeSafeReduction_IsVerifiedAndLowersTheCurve(params string[] tuning)
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);

        var (exitCode, output) = App.RunUndervolt(tuning);

        Assert.Equal(0, exitCode);
        App.AssertWriteConfirmed(output);

        // The change measurably lowered the curve: the flatten wrote reductions, and the capped top sits
        // well below the stock max (recovered by backing the just-written deltas out of the same read).
        IReadOnlyList<(int Mv, int Mhz)> effective = NvApi.GetVfCurve(_gpu.Gpu);
        int[] deltas = NvApi.GetCurveFreqDeltasKhz(_gpu.Gpu, effective.Count);
        int effectiveMax = effective.Max(p => p.Mhz);
        int stockMax = effective.Select((p, i) => p.Mhz - deltas[i] / 1000).Max();
        Assert.True(effectiveMax < stockMax - 100,
            $"expected the capped top clock ({effectiveMax} MHz) well below the stock max ({stockMax} MHz)");
        Assert.Contains(deltas, d => d < 0);
    }

    [SkippableFact]
    public void SaveShortcut_TargetsTheInstalledCopyWithBakedArgs()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);

        InTempDirectory(temp =>
        {
            // --dry-run so it only writes the .lnk; the exe runs with temp as its working directory.
            var (exitCode, _) = App.Run(temp, "tune", "--mv", "925", "--mhz", "2880", "--dry-run", "--save-shortcut");

            Assert.Equal(0, exitCode);
            string lnk = Path.Combine(temp, "Tune 925mV 2880MHz.lnk");
            Assert.True(File.Exists(lnk), $"expected the shortcut at {lnk}");

            // The link targets the installed -nocmd copy (windowless, survives the downloaded exe
            // moving), not the exe that saved it.
            var (target, arguments, workingDir) = Lnk.Read(lnk);
            Assert.Equal(Persistence.InstalledNoCmdExePath(), target);
            Assert.Equal(temp, workingDir.TrimEnd('\\'));
            Assert.Equal("--mv 925 --mhz 2880", arguments); // no command word - a leading option implies 'tune'
        });
    }

    [SkippableFact]
    public void SaveShortcut_HonoursACustomName()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);

        InTempDirectory(temp =>
        {
            var (exitCode, _) = App.Run(temp, "tune", "--mv", "925", "--dry-run", "--save-shortcut", "Quiet");

            Assert.Equal(0, exitCode);
            var (_, arguments, _) = Lnk.Read(Path.Combine(temp, "Quiet.lnk"));
            Assert.Equal("--mv 925", arguments);
        });
    }

    [SkippableFact]
    public void DryRunSaveShortcut_InvalidRequest_LeavesNoShortcutBehind()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);

        InTempDirectory(temp =>
        {
            // 1300 mV fails the plausible-range validation; the failed dry run must not leave a .lnk
            // that re-runs a command which can only fail again.
            var (exitCode, output) = App.Run(temp, "tune", "--mv", "1300", "--dry-run", "--save-shortcut");

            Assert.Equal(1, exitCode);
            Assert.Contains("outside the plausible", output);
            Assert.Empty(Directory.GetFiles(temp, "*.lnk"));
        });
    }

    [SkippableFact]
    public void LinkLaunchedUndervolt_BadgesTheLaunchingLinkOnDisk()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);

        InTempDirectory(temp =>
        {
            // Real links (made by the app itself), so their icons read back; the unrelated file can
            // stay a stub - marking must leave it alone either way.
            App.Run(temp, "tune", "--mv", "925", "--dry-run", "--save-shortcut"); // the launched one
            App.Run(temp, "tune", "--mv", "900", "--dry-run", "--save-shortcut");
            Touch(temp, "Some Game.lnk");

            // Marking runs only after a verified apply, which needs a clean curve read.
            var (exitCode, output) = LinkLaunch.Run(Path.Combine(temp, "Tune 925mV.lnk"), temp,
                App.ExePath(), "--mv", "925", "--no-persist");

            App.SkipIfCurveTransient(output);
            Assert.Equal(0, exitCode);

            // The launched link gains the badge icon; every file keeps its name; the others are
            // untouched.
            Assert.Equal(Persistence.InstalledActiveIconPath() + ",0",
                Lnk.ReadIcon(Path.Combine(temp, "Tune 925mV.lnk")));
            Assert.Equal(Shortcut.NoIcon, Lnk.ReadIcon(Path.Combine(temp, "Tune 900mV.lnk")));
            Assert.True(File.Exists(Path.Combine(temp, "Some Game.lnk")));
        });
    }

    [SkippableFact]
    public void LinkLaunchedUndervolt_ClearsTheBadgeFromThePreviouslyActiveLink()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);

        InTempDirectory(temp =>
        {
            App.Run(temp, "tune", "--mv", "925", "--dry-run", "--save-shortcut");
            App.Run(temp, "tune", "--mv", "900", "--dry-run", "--save-shortcut");

            var (_, first) = LinkLaunch.Run(Path.Combine(temp, "Tune 925mV.lnk"), temp,
                App.ExePath(), "--mv", "925", "--no-persist");
            App.SkipIfCurveTransient(first);
            var (exitCode, output) = LinkLaunch.Run(Path.Combine(temp, "Tune 900mV.lnk"), temp,
                App.ExePath(), "--mv", "900", "--no-persist");
            App.SkipIfCurveTransient(output);

            Assert.Equal(0, exitCode);
            Assert.Equal(Persistence.InstalledActiveIconPath() + ",0",
                Lnk.ReadIcon(Path.Combine(temp, "Tune 900mV.lnk")));
            Assert.Equal(Shortcut.NoIcon, Lnk.ReadIcon(Path.Combine(temp, "Tune 925mV.lnk")));
        });
    }

    [SkippableFact]
    public void SaveShortcutApply_BadgesTheSavedLink()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);

        InTempDirectory(temp =>
        {
            // A real (non-dry-run) save writes the exact link its settings describe, so that link is
            // the live profile and gets the badge without a launch identity.
            var (exitCode, output) = App.Run(temp, "tune", "--mv", "925", "--no-persist", "--save-shortcut");

            App.SkipIfCurveTransient(output);
            Assert.Equal(0, exitCode);
            Assert.Equal(Persistence.InstalledActiveIconPath() + ",0",
                Lnk.ReadIcon(Path.Combine(temp, "Tune 925mV.lnk")));
        });
    }

    [SkippableFact]
    public void PlainUndervolt_TouchesNoLinks()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);

        InTempDirectory(temp =>
        {
            // Only a link-launched run has a link identity to mark; a terminal run in a directory
            // full of links - even one whose settings match a link's name - must leave them alone.
            App.Run(temp, "tune", "--mv", "925", "--dry-run", "--save-shortcut"); // "Tune 925mV.lnk"

            var (exitCode, output) = App.Run(temp, "tune", "--mv", "925", "--no-persist");

            App.SkipIfCurveTransient(output);
            Assert.Equal(0, exitCode);
            Assert.Equal(Shortcut.NoIcon, Lnk.ReadIcon(Path.Combine(temp, "Tune 925mV.lnk")));
        });
    }

    private int[] CurveDeltasKhz() => GpuTuning.CurveDeltasKhz(_gpu.Gpu);

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
