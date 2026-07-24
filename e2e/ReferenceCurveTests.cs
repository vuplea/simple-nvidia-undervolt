namespace SimpleNvidiaUndervolt.E2E;

/// <summary>
/// End-to-end tests for the saved reference curve: the <c>set-reference-curve</c> capture (including
/// the reset-and-restore it performs on a tuned card) and the tuning that plans from it. Every test
/// sets the saved state it needs rather than inheriting the machine's, so both the reference and the
/// live planning path are covered on every run, and restores the user's own reference afterwards.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class ReferenceCurveTests
{
    private readonly GpuFixture _gpu;

    public ReferenceCurveTests(GpuFixture gpu) => _gpu = gpu;

    [SkippableFact]
    public void SetReferenceCurve_CapturesTheStockCurve_AndStatusReportsIt()
    {
        WithNoReference(() =>
        {
            var (exitCode, output) = SetReference();

            Assert.Equal(0, exitCode);
            Assert.Contains("stock points", output);

            var (statusExit, status) = App.Run(null, "status");
            Assert.Equal(0, statusExit);
            Assert.Contains("Reference curve: saved", status);
        });
    }

    [SkippableFact]
    public void SetReferenceCurve_WithATuningApplied_ResetsForTheCaptureAndRestoresIt()
    {
        WithNoReference(() =>
        {
            // Tune the memory too: the snapshot recovers that offset from the absolute P0 clock minus
            // the factory base (see AppliedTuning.MemoryDelta), which is the subtlest part of the
            // round-trip and the one a curve-only assertion would miss.
            var (tuneExit, _) = App.RunUndervolt("--mv", "900", "--mem-offset", "100");
            Assert.Equal(0, tuneExit);
            int[] appliedDeltas = GpuTuning.CurveDeltasKhz(_gpu.Gpu);
            int appliedMemoryKhz = App.ReadMemoryClockKhz(_gpu.Gpu);
            Assert.Contains(appliedDeltas, d => d != 0);

            var (exitCode, output) = SetReference();

            Assert.Equal(0, exitCode);
            Assert.Contains("A tuning is applied", output);
            Assert.Contains("Previous tuning restored.", output);
            Assert.Equal(appliedDeltas, GpuTuning.CurveDeltasKhz(_gpu.Gpu)); // the same tuning, exactly
            Assert.Equal(appliedMemoryKhz, App.ReadMemoryClockKhz(_gpu.Gpu));
            Assert.Equal(0u, NvApi.GetCoreVoltageBoostPercent(_gpu.Gpu));
        });
    }

    [SkippableFact]
    public void Undervolt_PlansFromTheReference_AndStillVerifiesTheWrite()
    {
        WithNoReference(() =>
        {
            Assert.Equal(0, SetReference().ExitCode);

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
            Assert.Contains("Tip: run 'set-reference-curve'", output);
            App.AssertWriteConfirmed(output);
        });
    }

    [SkippableFact]
    public void Undervolt_WithAReferenceFromAnotherCard_WarnsAndFallsBackToTheLiveCurve()
    {
        WithNoReference(() =>
        {
            Assert.Equal(0, SetReference().ExitCode);

            // Re-key the saved reference to a card this isn't: the identity check must reject it
            // rather than plan another card's curve onto this one.
            ReferenceCurveDoc doc = TuningDocuments.ParseReferenceCurveDoc(
                File.ReadAllText(ReferenceCurve.FilePath()), "the saved reference");
            doc.GpuPciIds = "DEADBEEF-DEADBEEF-DEADBEEF-DEADBEEF";
            File.WriteAllText(ReferenceCurve.FilePath(), TuningDocuments.Render(doc));

            var (exitCode, output) = App.RunUndervolt("--mv", "900");

            Assert.Equal(0, exitCode);
            Assert.Contains("doesn't match this GPU's identity", output);
            App.AssertWriteConfirmed(output);
        });
    }

    [SkippableFact]
    public void SetReferenceCurve_ExportsACurveFile_ThatImportsBack()
    {
        WithNoReference(() =>
        {
            string file = Path.Combine(Path.GetTempPath(), $"snu-e2e-reference-{Guid.NewGuid():N}.json");
            try
            {
                var (exitCode, output) = SetReference("--out-curve-file", file);
                Assert.Equal(0, exitCode);
                Assert.Contains("Exported the reference curve", output);

                ReferenceCurveDoc doc = TuningDocuments.ReadReferenceCurveFile(file);
                Assert.True(doc.Curve!.Length >= 16);

                // A fresh machine state importing the file must land in the same usable reference.
                ReferenceCurveBackup.Remove();
                var (importExit, importOutput) = App.Run(null, "set-reference-curve", "--in-curve-file", file);
                Assert.Equal(0, importExit);
                Assert.Contains("Reference set from", importOutput);

                var (statusExit, status) = App.Run(null, "status");
                Assert.Equal(0, statusExit);
                Assert.Contains("Reference curve: saved", status);
            }
            finally
            {
                File.Delete(file);
            }
        });
    }

    /// <summary>Runs <c>set-reference-curve</c>, skipping the test on a transitional curve read.</summary>
    private static (int ExitCode, string Output) SetReference(params string[] extraArgs)
    {
        var (exitCode, output) = App.Run(null, new[] { "set-reference-curve" }.Concat(extraArgs).ToArray());
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
