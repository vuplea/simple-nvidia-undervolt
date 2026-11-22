namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for the curve's frequency unit: which architecture counts it in half-kHz, and the
/// status decode that brings either unit to true MHz.</summary>
public class CurveUnitTests
{
    [Fact]
    public void CurveUnitsPerKhz_HalfKhzOnlyOnPascal()
    {
        Assert.Equal(2, NvApi.CurveUnitsPerKhz(NvApi.ARCHITECTURE_PASCAL));

        // Volta (untested), Turing, Ampere, Ada, Blackwell, and an unreadable architecture.
        foreach (uint? plain in new uint?[] { 0x140, 0x160, 0x170, 0x190, 0x1B0, null })
        {
            Assert.Equal(1, NvApi.CurveUnitsPerKhz(plain));
        }
    }

    [Fact]
    public void DecodeVfCurve_ReadsTrueMhzInEitherUnit()
    {
        // What a GTX 1080 stores for 1556.5 MHz at 850 mV and 1873 MHz at 1050 mV.
        byte[] status = StatusBuffer((850_000, 3_113_000), (1_050_000, 3_746_000));

        Assert.Equal(new[] { (850, 1556), (1050, 1873) }, NvApi.DecodeVfCurve(status, unitsPerKhz: 2));
        Assert.Equal(new[] { (850, 3113), (1050, 3746) }, NvApi.DecodeVfCurve(status, unitsPerKhz: 1));
    }

    /// <summary>A status buffer holding the given core-domain (uV, raw frequency) entries from
    /// entry 0 up; everything else zero.</summary>
    private static byte[] StatusBuffer(params (int VoltUv, int RawFreq)[] entries)
    {
        var bytes = new byte[7208];
        for (int i = 0; i < entries.Length; i++)
        {
            int e = NvApi.StatusEntryBase + i * NvApi.StatusEntryStride;
            BitConverter.GetBytes(entries[i].RawFreq).CopyTo(bytes, e + NvApi.StatusFreqOffset);
            BitConverter.GetBytes(entries[i].VoltUv).CopyTo(bytes, e + NvApi.StatusVoltOffset);
        }

        return bytes;
    }
}
