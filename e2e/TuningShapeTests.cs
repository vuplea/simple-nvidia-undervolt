using System.Globalization;
using System.Text.RegularExpressions;

namespace SimpleNvidiaUndervolt.E2E;

/// <summary>
/// Tests for the exact tuning state an apply leaves on the card — read back per-anchor through
/// NVAPI, no load required. The existing write tests assert that deltas landed and the top came
/// down; these pin the <em>shape</em>: where the offsets sit relative to the cap anchor (the settle
/// point placement rides on it), what a memory-only tune touches, what <c>status</c> reports, and
/// what <c>clear</c> resets.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class TuningShapeTests
{
    private readonly GpuFixture _gpu;

    public TuningShapeTests(GpuFixture gpu) => _gpu = gpu;

    [SkippableFact]
    public void PlainCap_WritesTheStraddle_AndFlattensAbove()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);
        var (stock, k) = ReadStockAndPickAnchor();

        var (exitCode, output) = App.RunUndervolt("--mv", App.Arg(stock[k].Mv));
        Assert.Equal(0, exitCode);
        App.AssertWriteConfirmed(output);

        // The shape signature in the written deltas: the cap anchor and the band below share one
        // offset near a spread below stock (the segment is aimed so the settle voltage holds the
        // stock clock, which writes the cap anchor a little under it), the flat top starts about
        // the fixed 16 MHz spread above the cap anchor, and everything from there up is one level
        // plateau. The rise and the cap offset carry a bin of slack each way: the post-write
        // refinement may move either by a few MHz to land the settle on the card's bin grid. A
        // flatten-at-the-cap shape would leave no rise between k and k+1; the
        // stock-slope-continued shape would show a stock-step rise instead of the fixed spread.
        int[] deltas = ReadDeltasAlignedWith(stock);
        Assert.Equal(deltas[k], deltas[k - 1]);
        Assert.InRange(deltas[k], -24_000, 8_000);
        long capAnchorKhz = stock[k].Mhz * 1000L + deltas[k];
        long flatTopKhz = stock[k + 1].Mhz * 1000L + deltas[k + 1];
        Assert.InRange(flatTopKhz - capAnchorKhz, 8_000, 24_000);
        // The written flat is exactly level relative to the curve the plan was built from; this
        // measures it against a fresh live read, and anchors step individually by a bin with
        // temperature between the two reads (see DEVELOPMENT.md), so each anchor gets one bin of
        // slack. A stock-slope-continued shape still fails - its clocks keep climbing across the
        // whole flat.
        for (int i = k + 2; i < deltas.Length; i++)
        {
            Assert.InRange(stock[i].Mhz * 1000L + deltas[i], flatTopKhz - 8_000, flatTopKhz + 8_000);
        }

        Assert.True(deltas[^1] < -15_000,
            $"the top anchor barely moved ({deltas[^1]} kHz) - the cap did not flatten the curve");
    }

    [SkippableFact]
    public void ReducedClockCap_WritesOneSharedOffset_FromTheBandThroughTheFlatStart()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);
        var (stock, k) = ReadStockAndPickAnchor();
        int targetMhz = stock[k].Mhz - 60; // a reduction: safe to hold on any card

        var (exitCode, output) = App.RunUndervolt("--mv", App.Arg(stock[k].Mv), "--mhz", App.Arg(targetMhz));
        Assert.Equal(0, exitCode);
        App.AssertWriteConfirmed(output);

        // The cap anchor's offset is one value carried by the band below it - that shared offset is
        // what keeps the clock on a stock-parallel slope however deep the boost settles - and the
        // flat top starts about the fixed 16 MHz spread above the cap anchor, with a bin of slack
        // each way for the post-write refinement. Both hold regardless of whether the run planned
        // from the saved reference or a live read.
        // The margin on the reduction is loose on purpose: planned from the saved reference, the
        // written delta is (live - reference) - 60, so the thermal gap between this run and the
        // reference capture rides on it; this only has to prove a real reduction landed.
        int[] deltas = ReadDeltasAlignedWith(stock);
        Assert.True(deltas[k] < -20_000, $"the cap anchor's delta ({deltas[k]} kHz) is not the requested reduction");
        Assert.Equal(deltas[k], deltas[k - 1]);
        Assert.InRange(
            (stock[k + 1].Mhz * 1000L + deltas[k + 1]) - (stock[k].Mhz * 1000L + deltas[k]),
            8_000, 24_000);
    }

    [SkippableFact]
    public void MemoryOnlyTune_SetsTheMemoryClock_AndLeavesTheCurveAtStock()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);
        int baseMhz = GpuTuning.BaseMemoryClockMhz(_gpu.Gpu);

        // No voltage cap: this is the memory-only path, which must not touch the V/F curve (and so
        // works at any power state, with no curve read to go wrong).
        var (exitCode, _) = App.RunUndervolt("--mem-offset", "100");

        Assert.Equal(0, exitCode);
        TuningSnapshot after = TuningSnapshot.Read(_gpu.Gpu);
        Assert.True(after.MemoryClockKhz.Ok, after.MemoryClockKhz.Error);
        Assert.Equal(baseMhz + 100, after.MemoryClockKhz.Value / 1000);
        Assert.All(GpuTuning.CurveDeltasKhz(_gpu.Gpu), d => Assert.Equal(0, d));
    }

    [SkippableFact]
    public void Status_WhileCapped_ReportsTheOperatingPoint()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);
        var (stock, k) = ReadStockAndPickAnchor();

        var (applyCode, applyOutput) = App.RunUndervolt("--mv", App.Arg(stock[k].Mv));
        Assert.Equal(0, applyCode);
        App.AssertWriteConfirmed(applyOutput);

        var (exitCode, output) = App.Run(null, "status");

        // The status line names the point the boost settles on. The apply refines its write
        // against the read-back until the realized point is the plan's settle (one boost step
        // below the flat start, floored at the cap), so a same-power-state status read moments
        // later reports that point - one boost step of slack each way covers an anchor stepping
        // with temperature between the two reads. A kept-closest refinement whose miss exceeds a
        // step fails here by design. The settle-law physics itself is LoadTests' subject.
        Assert.Equal(0, exitCode);
        Match m = Regex.Match(output, @"operating point (\d+) mV");
        Assert.True(m.Success, $"no 'operating point' report in: {output}");
        int settleMv = Math.Max(stock[k].Mv, stock[k + 1].Mv - GpuTuning.BoostStepMv);
        Assert.InRange(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            settleMv - GpuTuning.BoostStepMv, settleMv + GpuTuning.BoostStepMv);
    }

    [SkippableFact]
    public void Clear_ResetsTheMemoryOffsetToo()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);
        int baseMhz = GpuTuning.BaseMemoryClockMhz(_gpu.Gpu);

        // 'clear' also removes the real logon task, so shield whatever the user has persisted.
        PersistenceBackup backup = PersistenceBackup.Create();
        try
        {
            // Assert the offset is really live first: without this the test would pass just as
            // green if the setup had silently failed and there was nothing to reset.
            var (setupCode, _) = App.RunUndervolt("--mem-offset", "100");
            Assert.Equal(0, setupCode);
            TuningSnapshot offset = TuningSnapshot.Read(_gpu.Gpu);
            Assert.True(offset.MemoryClockKhz.Ok, offset.MemoryClockKhz.Error);
            Assert.Equal(baseMhz + 100, offset.MemoryClockKhz.Value / 1000);

            var (exitCode, _) = App.Run(null, "clear");

            Assert.Equal(0, exitCode);
            TuningSnapshot after = TuningSnapshot.Read(_gpu.Gpu);
            Assert.True(after.MemoryClockKhz.Ok, after.MemoryClockKhz.Error);
            Assert.Equal(baseMhz, after.MemoryClockKhz.Value / 1000);
        }
        finally
        {
            backup.Restore();
        }
    }

    private (IReadOnlyList<(int Mv, int Mhz)> Stock, int CapAnchor) ReadStockAndPickAnchor()
    {
        IReadOnlyList<(int Mv, int Mhz)> stock = StockProbe.ResetAndReadStockOrSkip(_gpu.Gpu);
        return (stock, StockProbe.PickCapAnchor(stock));
    }

    /// <summary>The applied per-anchor deltas, skipping the test if the live curve's anchor count
    /// doesn't match the stock read the assertions index into (a driver-level change mid-test).</summary>
    private int[] ReadDeltasAlignedWith(IReadOnlyList<(int Mv, int Mhz)> stock)
    {
        int[] deltas = GpuTuning.CurveDeltasKhz(_gpu.Gpu);
        Skip.If(deltas.Length != stock.Count, "the curve's anchor count changed between reads.");
        return deltas;
    }
}
