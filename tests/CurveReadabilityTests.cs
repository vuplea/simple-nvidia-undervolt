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
}
