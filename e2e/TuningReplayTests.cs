namespace SimpleNvidiaUndervolt.E2E;

/// <summary>
/// End-to-end tests for the applied-tuning exports (<c>tune</c>/<c>status --out-tuning-file</c>)
/// and their exact replay (<c>tune --in-tuning-file</c>): the exported document must re-apply the
/// same per-anchor deltas and memory offset the original tune wrote, through the real CLI.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class TuningReplayTests
{
    private readonly GpuFixture _gpu;

    public TuningReplayTests(GpuFixture gpu) => _gpu = gpu;

    [SkippableFact]
    public void ExportedTuning_ReplaysTheExactTuning()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);
        string tuneFile = Path.Combine(Path.GetTempPath(), $"snu-e2e-tune-{Guid.NewGuid():N}.json");
        string statusFile = Path.Combine(Path.GetTempPath(), $"snu-e2e-status-{Guid.NewGuid():N}.json");

        // The 'clear' below removes the user's real logon task and persisted tuning file - restore
        // them, like every test that touches persistence.
        PersistenceBackup backup = PersistenceBackup.Create();
        try
        {
            var (tuneExit, tuneOutput) = App.RunUndervolt("--mv", "900", "--mem-offset", "100",
                "--out-tuning-file", tuneFile);
            Assert.Equal(0, tuneExit);
            Assert.Contains("Exported the tuning", tuneOutput);
            int[] appliedDeltas = GpuTuning.CurveDeltasKhz(_gpu.Gpu);
            int appliedMemoryKhz = App.ReadMemoryClockKhz(_gpu.Gpu);
            Assert.Contains(appliedDeltas, d => d != 0);

            // The status export reads the same applied state back off the card, so the two documents
            // must carry the same tuned anchors.
            var (statusExit, statusOutput) = App.Run(null, "status", "--out-tuning-file", statusFile);
            Assert.Equal(0, statusExit);
            Assert.Contains("Exported the applied tuning", statusOutput);
            TuningDoc fromTune = TuningDocuments.ReadTuningFile(tuneFile);
            TuningDoc fromStatus = TuningDocuments.ReadTuningFile(statusFile);
            Assert.Equal(
                fromTune.Curve!.Select(e => (e.Mv, e.Offset)),
                fromStatus.Curve!.Select(e => (e.Mv, e.Offset)));
            Assert.Equal(fromTune.MemoryOffset, fromStatus.MemoryOffset);

            // Replay onto a different starting state, so the equality below proves the document -
            // not leftovers of the original tune - produced the applied result.
            Assert.Equal(0, App.Run(null, "clear").ExitCode);

            var (replayExit, replayOutput) = App.Run(null,
                "tune", "--in-tuning-file", tuneFile, "--no-persist");
            App.SkipIfCurveTransient(replayOutput);
            Assert.Equal(0, replayExit);
            Assert.Contains("Re-applying the tuning from", replayOutput);
            Assert.DoesNotContain("didn't change after writing", replayOutput);
            Assert.Equal(appliedDeltas, GpuTuning.CurveDeltasKhz(_gpu.Gpu));
            Assert.Equal(appliedMemoryKhz, App.ReadMemoryClockKhz(_gpu.Gpu));
        }
        finally
        {
            backup.Restore();
            File.Delete(tuneFile);
            File.Delete(statusFile);
        }
    }
}
