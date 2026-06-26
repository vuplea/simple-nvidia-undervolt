namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for <see cref="CurveLayout.TryDetect"/>, which recovers the status curve's
/// record layout (base/stride/volt/freq) from a raw buffer by finding the ascending-voltage array. Built
/// from synthetic buffers so the detector is exercised against known layouts without a GPU.</summary>
public class LayoutDetectionTests
{
    [Fact]
    public void DetectsTheKnownBlackwellLayout()
    {
        // base 0x40, volt +0x0C, freq +0x08 -> absolute columns 0x4C and 0x48.
        byte[] buf = BuildStatus(baseOffset: 0x40, stride: 28, voltOffset: 0x0C, freqOffset: 0x08,
            count: 127, peakBoostKhz: 2_900_000);

        Assert.True(CurveLayout.TryDetect(buf, out var d));
        Assert.Equal((28, 0x4C, 0x48, 127), (d.Stride, d.VoltColumn, d.FreqColumn, d.Count));
    }

    [Fact]
    public void DetectsADifferentLayout_NotHardcoded()
    {
        // base 0x20, volt +0x10, freq +0x04 -> absolute columns 0x30 and 0x24.
        byte[] buf = BuildStatus(baseOffset: 0x20, stride: 32, voltOffset: 0x10, freqOffset: 0x04,
            count: 100, peakBoostKhz: 2_500_000);

        Assert.True(CurveLayout.TryDetect(buf, out var d));
        Assert.Equal((32, 0x30, 0x24, 100), (d.Stride, d.VoltColumn, d.FreqColumn, d.Count));
    }

    [Fact]
    public void IdleCollapse_StillFindsTheVoltageAxis_ButReportsFreqUndetected()
    {
        // Every clock collapsed below a boost clock (the deep-idle shape): the voltage column is still
        // valid, so stride and the voltage column are found, but the freq column can't be identified.
        byte[] buf = BuildStatus(baseOffset: 0x40, stride: 28, voltOffset: 0x0C, freqOffset: 0x08,
            count: 127, peakBoostKhz: 200_000);

        Assert.True(CurveLayout.TryDetect(buf, out var d));
        Assert.Equal((28, 0x4C, 127), (d.Stride, d.VoltColumn, d.Count));
        Assert.Equal(-1, d.FreqColumn);
    }

    [Fact]
    public void NoCurve_ReturnsFalse()
    {
        Assert.False(CurveLayout.TryDetect(new byte[7208], out _));
    }

    /// <summary>A synthetic status buffer: <paramref name="count"/> records of <paramref name="stride"/>
    /// bytes starting at <paramref name="baseOffset"/>, each carrying an ascending core voltage (uV) and a
    /// non-decreasing frequency (kHz) that ramps to <paramref name="peakBoostKhz"/>. Everything else is
    /// zero, so the only ascending-voltage run is the real one.</summary>
    private static byte[] BuildStatus(int baseOffset, int stride, int voltOffset, int freqOffset,
        int count, int peakBoostKhz)
    {
        var buf = new byte[baseOffset + (count + 4) * stride];
        for (int i = 0; i < count; i++)
        {
            int record = baseOffset + i * stride;
            int uv = 700_000 + i * 4_000;                                  // 700 mV up in 4 mV steps
            int khz = 1_000_000 + (peakBoostKhz - 1_000_000) * i / (count - 1); // ramp to the peak
            BitConverter.GetBytes(uv).CopyTo(buf, record + voltOffset);
            BitConverter.GetBytes(khz).CopyTo(buf, record + freqOffset);
        }

        return buf;
    }
}
