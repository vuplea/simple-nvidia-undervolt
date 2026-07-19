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
    public void PlainCap_LeavesTheCapAndFlatStartAtStock_AndFlattensOnlyAbove()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);
        var (stock, k) = ReadStockAndPickAnchor();

        var (exitCode, output) = App.RunUndervolt("--mv", App.Arg(stock[k].Mv));
        Assert.Equal(0, exitCode);
        App.AssertWriteConfirmed(output);

        // The shape signature, exact in the written deltas: nothing below or at the flat start
        // moves (a plain cap keeps stock clocks through it - the boost then settles one step below
        // the flat start, i.e. on the cap point), and everything above only comes down. A
        // flatten-at-the-cap shape would show a negative delta at k+1.
        int[] deltas = ReadDeltasAlignedWith(stock);
        for (int i = 0; i <= k + 1; i++)
        {
            Assert.True(deltas[i] == 0, $"anchor {i} ({stock[i].Mv} mV) moved by {deltas[i]} kHz below the flat start");
        }

        Assert.All(deltas, d => Assert.True(d <= 0, $"a plain cap wrote a positive delta ({d} kHz)"));
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

        // The cap's offset is one value carried by the band below the cap, the cap anchor and the
        // flat start above it - that shared offset is what keeps the clock on a stock-parallel
        // slope however deep the boost settles. Exact equality holds regardless of whether the run
        // planned from the saved reference or a live read.
        // The margin is loose on purpose: planned from the saved reference, the written delta is
        // (live - reference) - 60, so the thermal gap between this run and the reference capture
        // rides on it. The two equalities below are the actual shape signature; this only has to
        // prove a real reduction landed.
        int[] deltas = ReadDeltasAlignedWith(stock);
        Assert.True(deltas[k] < -20_000, $"the cap anchor's delta ({deltas[k]} kHz) is not the requested reduction");
        Assert.Equal(deltas[k], deltas[k - 1]);
        Assert.Equal(deltas[k], deltas[k + 1]);
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
    public void Status_WhileCapped_ReportsTheCapPoint()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);
        var (stock, k) = ReadStockAndPickAnchor();

        var (applyCode, applyOutput) = App.RunUndervolt("--mv", App.Arg(stock[k].Mv));
        Assert.Equal(0, applyCode);
        App.AssertWriteConfirmed(applyOutput);

        var (exitCode, output) = App.Run(null, "status");

        // The status line names the point the boost settles on - the cap anchor. One anchor of
        // slack: a read at a temperature away from the reference can seam the flat's edge, which
        // moves the reported point one anchor down (see GpuTuning.EffectiveCapPoint).
        Assert.Equal(0, exitCode);
        Match m = Regex.Match(output, @"capped at (\d+) mV");
        Assert.True(m.Success, $"no 'capped at' report in: {output}");
        Assert.InRange(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            stock[k - 1].Mv, stock[k].Mv);
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
