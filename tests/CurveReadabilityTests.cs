namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for <see cref="GpuTuning.CurveFreqsReadable"/>, which decides whether the live
/// frequency column is a clean read or a transitional/idle collapse that must not be acted on.</summary>
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
    public void AWholesaleCollapse_IsNotReadable()
    {
        // Every clock low (peak never reaches a boost clock) — the deep-idle shape.
        Assert.False(GpuTuning.CurveFreqsReadable(TestCurves.Collapsed()));
    }

    // CurveVoltsPlausible gates *writing*: it accepts any card whose voltage axis looks like a real
    // V/F table (even at idle, where only the frequency column has collapsed) and rejects a card
    // whose tuning-buffer offsets don't match, where the read decodes as garbage.

    [Fact]
    public void VoltsPlausible_AcceptsACleanCurve()
    {
        Assert.True(GpuTuning.CurveVoltsPlausible(TestCurves.Realistic()));
    }

    [Fact]
    public void VoltsPlausible_StaysTrueAtIdle_WhenOnlyFreqsCollapsed()
    {
        // A supported card at idle: voltages are still valid, only the frequencies collapsed. Memory
        // writes must remain possible, so the voltage gate accepts it.
        Assert.True(GpuTuning.CurveVoltsPlausible(TestCurves.Collapsed()));
    }

    [Fact]
    public void VoltsPlausible_RejectsAShortGarbageRead()
    {
        Assert.False(GpuTuning.CurveVoltsPlausible(TestCurves.Garbage()));
    }

    [Fact]
    public void VoltsPlausible_RejectsANarrowVoltageSpan()
    {
        var narrow = Enumerable.Range(0, 20).Select(i => (900 + i, 2000 + i * 50)).ToList();
        Assert.False(GpuTuning.CurveVoltsPlausible(narrow));
    }

    [Fact]
    public void VoltsPlausible_RejectsANonMonotonicVoltageRead()
    {
        var curve = TestCurves.Realistic();                       // 20 ascending points
        curve[12] = (curve[11].Mv - 50, curve[12].Mhz);          // voltage dips below its neighbour
        Assert.False(GpuTuning.CurveVoltsPlausible(curve));
    }
}
