namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for the linear V/F-curve interpolation used to derive a missing peak coordinate.</summary>
public class InterpolationTests
{
    [Fact]
    public void FreqAtVoltage_InterpolatesBetweenAnchors()
    {
        var curve = TestCurves.Realistic();        // (1000 mV, 2500 MHz) and (1020 mV, 2550 MHz)
        Assert.Equal(2525, GpuTuning.FreqAtVoltage(curve, 1010), precision: 6);
    }

    [Fact]
    public void VoltageAtFreq_IsTheInverse()
    {
        var curve = TestCurves.Realistic();
        Assert.Equal(1010, GpuTuning.VoltageAtFreq(curve, 2525), precision: 6);
    }

    [Fact]
    public void Interpolation_ClampsBelowTheFirstAnchor()
    {
        var curve = TestCurves.Realistic();        // first anchor (800 mV, 2000 MHz)
        Assert.Equal(2000, GpuTuning.FreqAtVoltage(curve, 500), precision: 6);
        Assert.Equal(800, GpuTuning.VoltageAtFreq(curve, 100), precision: 6);
    }

    [Fact]
    public void Interpolation_ClampsAboveTheLastAnchor()
    {
        var curve = TestCurves.Realistic();        // last anchor (1180 mV, 2950 MHz)
        Assert.Equal(2950, GpuTuning.FreqAtVoltage(curve, 1300), precision: 6);
        Assert.Equal(1180, GpuTuning.VoltageAtFreq(curve, 5000), precision: 6);
    }

    [Fact]
    public void EmptyCurve_Throws()
    {
        Assert.Throws<NvApiException>(() => GpuTuning.FreqAtVoltage(new List<(int Mv, int Mhz)>(), 900));
    }
}
