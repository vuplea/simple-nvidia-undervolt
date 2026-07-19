namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for the reference curve's pure logic: the registry point rendering, the
/// anchor-voltage cross-check against the live curve, and the GPU identity match. The registry IO
/// itself stays a thin untested shell.</summary>
public class ReferenceCurveTests
{
    [Fact]
    public void PointLines_RoundTrip()
    {
        var curve = TestCurves.Realistic();

        var parsed = ReferenceCurve.TryParsePointLines(ReferenceCurve.ToPointLines(curve));

        Assert.NotNull(parsed);
        Assert.Equal(curve, parsed);
    }

    [Theory]
    [InlineData("800")]           // missing frequency
    [InlineData("800 2000 extra")]
    [InlineData("800,2000")]      // wrong separator
    [InlineData("eight 2000")]
    [InlineData("")]
    public void ParsePointLines_RejectsMalformedLine(string badLine)
    {
        string[] lines = ReferenceCurve.ToPointLines(TestCurves.Realistic()).Append(badLine).ToArray();

        Assert.Null(ReferenceCurve.TryParsePointLines(lines));
    }

    [Fact]
    public void AnchorsMatch_SameVoltages_Matches_EvenWithShiftedFrequencies()
    {
        var reference = TestCurves.Realistic();

        // The live curve's frequency column shifts with temperature; only the voltages are the key.
        var live = reference.Select(p => (p.Mv, p.Mhz + 30)).ToList();

        Assert.True(ReferenceCurve.AnchorsMatch(reference, live));
    }

    [Fact]
    public void AnchorsMatch_DifferentCountOrVoltage_DoesNotMatch()
    {
        var reference = TestCurves.Realistic();

        Assert.False(ReferenceCurve.AnchorsMatch(reference, TestCurves.Realistic(19)));

        var movedAnchor = TestCurves.Realistic();
        movedAnchor[7] = (movedAnchor[7].Mv + 5, movedAnchor[7].Mhz);
        Assert.False(ReferenceCurve.AnchorsMatch(reference, movedAnchor));
    }

    private static readonly GpuIdentity Card =
        new("NVIDIA GeForce RTX 5080", "2C0210DE-11223344-000000A1-2C0210DE", "0123456789ABCDEF");

    [Fact]
    public void Identity_SameCard_Matches()
    {
        Assert.True(Card.Matches(Card with { }));
    }

    [Fact]
    public void Identity_MissingSerialOnEitherSide_StillMatches()
    {
        // The serial is best-effort (a driver update may stop reporting it) - it only
        // discriminates when both sides have one.
        Assert.True(Card.Matches(Card with { BoardSerial = null }));
        Assert.True((Card with { BoardSerial = null }).Matches(Card));
    }

    [Fact]
    public void Identity_DifferentCard_DoesNotMatch()
    {
        Assert.False(Card.Matches(Card with { Name = "NVIDIA GeForce RTX 5070" }));
        Assert.False(Card.Matches(Card with { PciIds = "2F0410DE-11223344-000000A1-2F0410DE" }));

        // Same model, different physical unit: the stock curve is per-chip, so it must not match.
        Assert.False(Card.Matches(Card with { BoardSerial = "FEDCBA9876543210" }));
    }
}
