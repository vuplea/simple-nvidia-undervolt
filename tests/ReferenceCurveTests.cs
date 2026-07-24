namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for the reference curve's pure logic: the anchor-voltage cross-check against the
/// live curve. The file IO itself stays a thin untested shell (the stored document's parsing and
/// identity matching are covered by <see cref="TuningDocumentsTests"/>).</summary>
public class ReferenceCurveTests
{
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
}
