namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for the linear V/F-curve interpolation used to derive the peak frequency.</summary>
public class InterpolationTests
{
    [Fact]
    public void FreqAtVoltage_InterpolatesBetweenAnchors()
    {
        var curve = TestCurves.Realistic();        // (1000 mV, 2500 MHz) and (1020 mV, 2550 MHz)
        Assert.Equal(2525, GpuTuning.FreqAtVoltage(curve, 1010), precision: 6);
    }

    [Fact]
    public void Interpolation_ClampsBelowTheFirstAnchor()
    {
        var curve = TestCurves.Realistic();        // first anchor (800 mV, 2000 MHz)
        Assert.Equal(2000, GpuTuning.FreqAtVoltage(curve, 500), precision: 6);
    }

    [Fact]
    public void Interpolation_ClampsAboveTheLastAnchor()
    {
        var curve = TestCurves.Realistic();        // last anchor (1180 mV, 2950 MHz)
        Assert.Equal(2950, GpuTuning.FreqAtVoltage(curve, 1300), precision: 6);
    }
}
