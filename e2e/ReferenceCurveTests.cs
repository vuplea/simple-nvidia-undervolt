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
            var (tuneExit, _) = App.RunUndervolt("--mv", "900");
            Assert.Equal(0, tuneExit);
            int[] applied = GpuTuning.CurveDeltasKhz(_gpu.Gpu);
            Assert.Contains(applied, d => d != 0);

            var (exitCode, output) = SaveReference();

            Assert.Equal(0, exitCode);
            Assert.Contains("A tuning is applied", output);
            Assert.Contains("Previous tuning restored.", output);
            Assert.Equal(applied, GpuTuning.CurveDeltasKhz(_gpu.Gpu)); // byte-for-byte the same tuning
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
            Assert.Contains("different GPU hardware", output);
            App.AssertWriteConfirmed(output);
        });
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
