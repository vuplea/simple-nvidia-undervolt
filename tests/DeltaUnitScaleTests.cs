namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for the control-table delta-unit witness: parsing the graphics delta range out of
/// a ClkDomainsGetInfo buffer and mapping it to the field's units-per-kHz scale.</summary>
public class DeltaUnitScaleTests
{
    private const int EntryBase = 0x28;
    private const int EntryStride = 72;

    /// <summary>A ClkDomainsGetInfo-sized buffer with the given (type, rangeMax, rangeMin) entries
    /// from entry 0 up; everything else zero.</summary>
    private static byte[] DomainsBuffer(params (int Type, int RangeMax, int RangeMin)[] entries)
    {
        var bytes = new byte[2344];
        for (int i = 0; i < entries.Length; i++)
        {
            int e = EntryBase + i * EntryStride;
            BitConverter.GetBytes(entries[i].Type).CopyTo(bytes, e + 0x04);
            BitConverter.GetBytes(entries[i].RangeMax).CopyTo(bytes, e + 0x28);
            BitConverter.GetBytes(entries[i].RangeMin).CopyTo(bytes, e + 0x2C);
        }

        return bytes;
    }

    [Fact]
    public void GraphicsDeltaRange_ReadsTheGraphicsEntry()
    {
        // The 5090 shape: graphics (type 0) then memory (type 4, its own wider limit).
        byte[] bytes = DomainsBuffer((0, 1_000_000, -1_000_000), (4, 3_000_000, -1_000_000));
        Assert.Equal((1_000_000, -1_000_000), NvApi.GraphicsDeltaRange(bytes));
    }

    [Fact]
    public void GraphicsDeltaRange_SkipsAnEmptyGraphicsTypedEntry()
    {
        // Unpopulated entries read as all zeros, which is also the graphics type id - only a
        // populated range identifies the real entry.
        byte[] bytes = DomainsBuffer((0, 0, 0), (0, 2_000_000, -2_000_000));
        Assert.Equal((2_000_000, -2_000_000), NvApi.GraphicsDeltaRange(bytes));
    }

    [Fact]
    public void GraphicsDeltaRange_SkipsOtherDomains()
    {
        byte[] bytes = DomainsBuffer((4, 3_000_000, -1_000_000), (0, 2_000_000, -2_000_000));
        Assert.Equal((2_000_000, -2_000_000), NvApi.GraphicsDeltaRange(bytes));
    }

    [Fact]
    public void GraphicsDeltaRange_NullWhenNothingPopulated()
    {
        Assert.Null(NvApi.GraphicsDeltaRange(new byte[2344]));
        Assert.Null(NvApi.GraphicsDeltaRange(Array.Empty<byte>()));
    }

    [Fact]
    public void CurveDeltaUnitScale_DoubledOnlyOnTheExactPascalSignatureAndArchitecture()
    {
        Assert.Equal(2, NvApi.CurveDeltaUnitScale((2_000_000, -2_000_000), NvApi.ARCHITECTURE_PASCAL));
        Assert.Equal(2, NvApi.CurveDeltaUnitScale((2_000_000, -2_000_000), 0x134)); // any Pascal sub-id
        Assert.Equal(1, NvApi.CurveDeltaUnitScale((1_000_000, -1_000_000), NvApi.ARCHITECTURE_PASCAL));
        Assert.Equal(1, NvApi.CurveDeltaUnitScale(null, NvApi.ARCHITECTURE_PASCAL));

        // The signature without a Pascal architecture must not scale: a future plain-unit card that
        // genuinely widens its limit to +/-2000 MHz would otherwise get doubled (overshooting)
        // writes. Volta sits outside the band (untested), and an unreadable architecture doesn't
        // scale either - both take the reported-at-half-depth miss instead.
        Assert.Equal(1, NvApi.CurveDeltaUnitScale((2_000_000, -2_000_000), 0x1B0));
        Assert.Equal(1, NvApi.CurveDeltaUnitScale((2_000_000, -2_000_000), NvApi.ARCHITECTURE_VOLTA));
        Assert.Equal(1, NvApi.CurveDeltaUnitScale((2_000_000, -2_000_000), null));

        // Off-signature ranges must not scale even on Pascal: an unscaled write on a doubled-unit
        // card lands at half depth (reported as such), while a wrongly doubled write would
        // overshoot the request.
        Assert.Equal(1, NvApi.CurveDeltaUnitScale((2_000_000, -1_000_000), NvApi.ARCHITECTURE_PASCAL));
        Assert.Equal(1, NvApi.CurveDeltaUnitScale((3_000_000, -3_000_000), NvApi.ARCHITECTURE_PASCAL));
    }
}
