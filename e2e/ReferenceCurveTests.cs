using Microsoft.Win32;

namespace SimpleNvidiaUndervolt.E2E;

/// <summary>
/// End-to-end tests for the saved reference curve: the <c>save-reference</c> capture (including the
/// reset-and-restore it performs on a tuned card) and the tuning that plans from it. Every test sets
/// the saved state it needs rather than inheriting the machine's, so both the reference and the live
/// planning path are covered on every run, and restores the user's own reference afterwards.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class ReferenceCurveTests
{
    private readonly GpuFixture _gpu;

    public ReferenceCurveTests(GpuFixture gpu) => _gpu = gpu;

    [SkippableFact]
    public void SaveReference_CapturesTheStockCurve_AndStatusReportsIt()
    {
        WithNoReference(() =>
        {
            var (exitCode, output) = SaveReference();

            Assert.Equal(0, exitCode);
            Assert.Contains("stock points", output);

            var (statusExit, status) = App.Run(null, "status");
            Assert.Equal(0, statusExit);
            Assert.Contains("Reference curve: saved", status);
        });
    }

    [SkippableFact]
    public void SaveReference_WithATuningApplied_ResetsForTheCaptureAndRestoresIt()
    {
        WithNoReference(() =>
        {
            // Tune the memory too: the snapshot recovers that offset from the absolute P0 clock minus
            // the factory base (see AppliedTuning.MemoryDelta), which is the subtlest part of the
            // round-trip and the one a curve-only assertion would miss.
            var (tuneExit, _) = App.RunUndervolt("--mv", "900", "--mem-offset", "100");
            Assert.Equal(0, tuneExit);
            int[] appliedDeltas = GpuTuning.CurveDeltasKhz(_gpu.Gpu);
            int appliedMemoryKhz = ReadMemoryClockKhz();
            Assert.Contains(appliedDeltas, d => d != 0);

            var (exitCode, output) = SaveReference();

            Assert.Equal(0, exitCode);
            Assert.Contains("A tuning is applied", output);
            Assert.Contains("Previous tuning restored.", output);
            Assert.Equal(appliedDeltas, GpuTuning.CurveDeltasKhz(_gpu.Gpu)); // the same tuning, exactly
            Assert.Equal(appliedMemoryKhz, ReadMemoryClockKhz());
            Assert.Equal(0u, NvApi.GetCoreVoltageBoostPercent(_gpu.Gpu));
        });
    }

    [SkippableFact]
    public void Undervolt_PlansFromTheReference_AndStillVerifiesTheWrite()
    {
        WithNoReference(() =>
        {
            Assert.Equal(0, SaveReference().ExitCode);

            var (exitCode, output) = App.RunUndervolt("--mv", "900");

            Assert.Equal(0, exitCode);
            Assert.Contains("Planning from the reference curve", output);
            App.AssertWriteConfirmed(output);
            Assert.Contains(GpuTuning.CurveDeltasKhz(_gpu.Gpu), d => d != 0);
        });
    }

    [SkippableFact]
    public void Undervolt_WithoutAReference_PlansFromTheLiveCurve_AndSuggestsSavingOne()
    {
        WithNoReference(() =>
        {
            var (exitCode, output) = App.RunUndervolt("--mv", "900");

            Assert.Equal(0, exitCode);
            Assert.Contains("Tip: run 'save-reference'", output);
            App.AssertWriteConfirmed(output);
        });
    }

    [SkippableFact]
    public void Undervolt_WithAReferenceFromAnotherCard_WarnsAndFallsBackToTheLiveCurve()
    {
        WithNoReference(() =>
        {
            Assert.Equal(0, SaveReference().ExitCode);

            // Re-key the saved reference to a card this isn't: the identity check must reject it
            // rather than plan another card's per-chip curve onto this one.
            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(ReferenceCurve.KeyPath, writable: true))
            {
                key.SetValue("PciIds", "DEADBEEF-DEADBEEF-DEADBEEF-DEADBEEF");
            }

            var (exitCode, output) = App.RunUndervolt("--mv", "900");

            Assert.Equal(0, exitCode);
            Assert.Contains("doesn't match this GPU's identity", output);
            App.AssertWriteConfirmed(output);
        });
    }

    /// <summary>The absolute P0 memory clock, which moves with an applied memory offset.</summary>
    private int ReadMemoryClockKhz()
    {
        Reading<int> clock = TuningSnapshot.Read(_gpu.Gpu).MemoryClockKhz;
        Assert.True(clock.Ok, clock.Error);
        return clock.Value;
    }

    /// <summary>Runs <c>save-reference</c>, skipping the test on a transitional curve read.</summary>
    private static (int ExitCode, string Output) SaveReference()
    {
        var (exitCode, output) = App.Run(null, "save-reference");
        App.SkipIfCurveTransient(output);
        return (exitCode, output);
    }

    /// <summary>Runs the body against a machine with no saved reference, restoring the user's own
    /// afterwards. Skips when the suite can't reach an elevated GPU.</summary>
    private void WithNoReference(Action body)
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);

        ReferenceCurveBackup backup = ReferenceCurveBackup.Create();
        try
        {
            ReferenceCurveBackup.Remove();
            body();
        }
        finally
        {
            backup.Restore();
        }
    }
}
