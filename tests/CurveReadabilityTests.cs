namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for <see cref="GpuTuning.CurveFreqsReadable"/>, which decides whether the live
/// frequency column is a clean read or a transitional collapse that must not be acted on.</summary>
public class CurveReadabilityTests
{
    [Fact]
    public void CleanCurve_IsReadable()
    {
        Assert.True(GpuTuning.CurveFreqsReadable(TestCurves.Realistic()));
    }

    [Fact]
    public void TooFewPoints_IsNotReadable()
    {
        Assert.False(GpuTuning.CurveFreqsReadable(TestCurves.Realistic(n: 10)));
    }

    [Fact]
    public void ACollapsedPoint_IsNotReadable()
    {
        var curve = TestCurves.Realistic();
        curve[7] = (curve[7].Mv, 50);              // a sub-100 MHz garbage point
        Assert.False(GpuTuning.CurveFreqsReadable(curve));
    }

    [Fact]
    public void ANonMonotonicDip_IsNotReadable()
    {
        var curve = TestCurves.Realistic();
        curve[12] = (curve[12].Mv, curve[11].Mhz - 100);   // dips below its left neighbour
        Assert.False(GpuTuning.CurveFreqsReadable(curve));
    }

    [Fact]
    public void ASmallReshapeDip_IsReadable()
    {
        // The driver re-shapes the curve it reports (bin snapping, thermal shift, smoothing around
        // an applied flatten), so a clean read can dip a few bins between neighbours - only a real
        // collapse dips by hundreds of MHz.
        var curve = TestCurves.Realistic();
        curve[12] = (curve[12].Mv, curve[11].Mhz - 20);
        Assert.True(GpuTuning.CurveFreqsReadable(curve));
    }

    [Fact]
    public void AWholesaleCollapse_IsNotReadable()
    {
        // Every clock low (peak never reaches a boost clock) — the wholesale transitional collapse.
        Assert.False(GpuTuning.CurveFreqsReadable(TestCurves.Collapsed()));
    }

    [Fact]
    public void ALowBoostMobileCurve_IsReadable()
    {
        // A power-limited mobile part can top out well below a desktop boost clock (1270 MHz here);
        // the boost threshold tolerates it while the wholesale collapse above still fails.
        var lowClock = Enumerable.Range(0, 20).Select(i => (800 + i * 20, 700 + i * 30)).ToList();
        Assert.True(GpuTuning.CurveFreqsReadable(lowClock));
    }

    // CurveVoltsPlausible gates *writing*: it accepts any card whose voltage axis looks like a real
    // V/F table (even when the frequency column has collapsed) and rejects a card whose
    // tuning-buffer offsets don't match, where the read decodes as garbage.

    [Fact]
    public void VoltsPlausible_AcceptsACleanCurve()
    {
        Assert.True(GpuTuning.CurveVoltsPlausible(TestCurves.Realistic()));
    }

    [Fact]
    public void VoltsPlausible_StaysTrueWhenOnlyFreqsCollapsed()
    {
        // A supported card mid-collapse: the voltage axis is still valid, so the write gate (which
        // checks only voltages) accepts it.
        Assert.True(GpuTuning.CurveVoltsPlausible(TestCurves.Collapsed()));
    }

    [Fact]
    public void VoltsPlausible_RejectsAShortGarbageRead()
    {
        Assert.False(GpuTuning.CurveVoltsPlausible(TestCurves.Garbage()));
    }

    [Fact]
    public void VoltsPlausible_AcceptsALowVoltageTable()
    {
        // A low-voltage mobile table topping out at 854 mV (span 304) still reads as a real one.
        var lowVolt = Enumerable.Range(0, 20).Select(i => (550 + i * 16, 2000 + i * 50)).ToList();
        Assert.True(GpuTuning.CurveVoltsPlausible(lowVolt));
    }

    [Fact]
    public void VoltsPlausible_RejectsANarrowVoltageSpan()
    {
        var narrow = Enumerable.Range(0, 20).Select(i => (900 + i, 2000 + i * 50)).ToList();
        Assert.False(GpuTuning.CurveVoltsPlausible(narrow));
    }

    [Fact]
    public void VoltsPlausible_AcceptsDuplicateTruncatedMillivolts()
    {
        // The raw table ascends in microvolts, so adjacent anchors can truncate to the same millivolt
        // - that is a real table, not a garbage read.
        var curve = TestCurves.Realistic();
        curve[5] = (curve[4].Mv, curve[5].Mhz);
        Assert.True(GpuTuning.CurveVoltsPlausible(curve));
    }

    [Fact]
    public void VoltsPlausible_RejectsANonMonotonicVoltageRead()
    {
        var curve = TestCurves.Realistic();                       // 20 ascending points
        curve[12] = (curve[11].Mv - 50, curve[12].Mhz);          // voltage dips below its neighbour
        Assert.False(GpuTuning.CurveVoltsPlausible(curve));
    }
}
