namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for <see cref="GpuTuning.EffectiveOperatingPoint"/> — the "operating point
/// 960 mV / ~2880 MHz" readout the <c>status</c> core-curve line infers from a live read — for
/// <see cref="GpuTuning.PointAtVoltage"/>, the anchor lookup the post-apply report uses instead,
/// and for how <see cref="TuningSnapshot.DescribeCoreCurve"/> renders the point.</summary>
public class OperatingPointTests
{
    [Fact]
    public void FlattenedCurve_OperatingPointIsThePlansSettlePoint()
    {
        // The inference and the plan describe the same physical point: one boost step below the
        // flat start, on the cap→flat segment. On the plan's own effective curve the two must agree
        // exactly.
        var stock = TestCurves.Realistic();
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: null, capPoints: 8);

        Assert.Equal((plan.SettleMv, plan.SettleMhz),
            GpuTuning.EffectiveOperatingPoint(TestCurves.Effective(stock, plan.DeltasKhz)));
    }

    [Fact]
    public void FlatStartSnappedABinLow_MovesThePointOneAnchorUp_NotOntoTheBand()
    {
        // The read-back can re-snap the flat's first anchor a bin under the rest. Exact-equality
        // flat matching then treats that anchor as the segment's lower end: the inferred point
        // moves one anchor up (5 mV high, clock within a bin) rather than swallowing the cap anchor
        // into the flat and reporting a band anchor that isn't the operating point at all.
        var stock = TestCurves.Realistic();
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: null, capPoints: 8);
        var effective = TestCurves.Effective(stock, plan.DeltasKhz);
        effective[11] = (effective[11].Mv, effective[11].Mhz - 8); // the flat start snaps a bin under

        var point = GpuTuning.EffectiveOperatingPoint(effective);
        Assert.Equal(1035, point!.Value.Mv);          // one boost step below the apparent flat at 1040
        Assert.InRange(point.Value.Mhz, plan.SettleMhz - 8, plan.SettleMhz + 8);
    }

    [Fact]
    public void StaircaseTransientRead_WalksThePointUpTheStaircase()
    {
        // A read taken while the card's temperature moves away from the reference tilts the written
        // flat into plateaus a bin apart. The inference keys on the top plateau, so it reports high
        // up the staircase - the documented transient a re-read clears, and the reason the
        // post-apply report names its anchors outright instead of inferring them.
        var stock = TestCurves.Realistic();
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: null, capPoints: 8);
        var effective = TestCurves.Effective(stock, plan.DeltasKhz);
        for (int i = 14; i < effective.Count; i++)
        {
            effective[i] = (effective[i].Mv, effective[i].Mhz + 8);   // upper plateau, one bin up
        }

        Assert.True(GpuTuning.EffectiveOperatingPoint(effective)!.Value.Mv > plan.SettleMv);
    }

    // --- the post-apply report's anchor lookup (GpuTuning.PointAtVoltage) ---

    [Fact]
    public void PointAtVoltage_ReadsTheClockAtTheAnchorThePlanNames()
    {
        var stock = TestCurves.Realistic();
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: null, capPoints: 8);
        var effective = TestCurves.Effective(stock, plan.DeltasKhz);

        Assert.Equal((plan.CapMv, plan.CapMhz), GpuTuning.PointAtVoltage(effective, plan.CapMv));
        Assert.Equal((plan.FlatMv, plan.FlatMhz), GpuTuning.PointAtVoltage(effective, plan.FlatMv));
    }

    [Fact]
    public void PointAtVoltage_IsUnmovedByACumulativelyTiltedRead()
    {
        // The tilt a card far from its reference temperature produces: each flattened anchor reads
        // back a further 8 MHz up, so the whole flat becomes a staircase with no plateau left for
        // shape inference to key on. Naming the anchor outright is immune to the tilt.
        var stock = TestCurves.Realistic();
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: null, capPoints: 8);
        var effective = TestCurves.Effective(stock, plan.DeltasKhz);
        for (int i = 11; i < effective.Count; i++)
        {
            effective[i] = (effective[i].Mv, effective[i].Mhz + 8 * (i - 10));
        }

        Assert.Equal(plan.CapMv, GpuTuning.PointAtVoltage(effective, plan.CapMv)!.Value.Mv);
        Assert.Null(GpuTuning.EffectiveOperatingPoint(effective));   // no plateau left to infer from
    }

    [Fact]
    public void PointAtVoltage_IsNullWhenTheReadBackSkipsTheAnchor()
    {
        Assert.Null(GpuTuning.PointAtVoltage(TestCurves.Realistic(), mv: 1001));
    }

    [Fact]
    public void CurveWithoutAPlateau_HasNoOperatingPoint()
    {
        // With distinct anchor clocks the top anchor stands alone, and a single-anchor top is not a
        // flatten - a stock curve's peak, or one bin of noise on a flat's top edge. Inferring a
        // near-peak point from it would fabricate a number, so nothing is reported.
        Assert.Null(GpuTuning.EffectiveOperatingPoint(TestCurves.Realistic()));
    }

    [Fact]
    public void WhollyFlatCurve_OperatingPointIsTheFirstAnchor()
    {
        // Nothing sits below the flat, so its lowest anchor is the closest thing to a settle point.
        var flat = Enumerable.Range(0, 20).Select(i => (800 + i * 20, 2500)).ToList();

        Assert.Equal((800, 2500), GpuTuning.EffectiveOperatingPoint(flat));
    }

    [Fact]
    public void CollapsedRead_HasNoOperatingPoint()
    {
        Assert.Null(GpuTuning.EffectiveOperatingPoint(TestCurves.Collapsed()));
    }

    // --- status rendering (TuningSnapshot.DescribeCoreCurve) ---

    private static TuningSnapshot Snapshot(int[] offsetsKhz, (int Mv, int Mhz)? point) => new()
    {
        Name = "GPU",
        CoreCurveOffsetsKhz = Reading<int[]>.Success(offsetsKhz),
        OperatingPoint = point,
    };

    [Fact]
    public void DescribeCoreCurve_Tuned_AppendsTheOperatingPoint()
    {
        var snapshot = Snapshot(new[] { -200_000, -200_000 }, (960, 2880));

        Assert.Equal("-200 MHz on 2 point(s), operating point 960 mV / ~2880 MHz",
            snapshot.DescribeCoreCurve());
    }

    [Fact]
    public void DescribeCoreCurve_TunedButPointUnreadable_ShowsTheOffsetsAlone()
    {
        Assert.Equal("-200 MHz on 1 point(s)", Snapshot(new[] { -200_000 }, point: null).DescribeCoreCurve());
    }

    [Fact]
    public void DescribeCoreCurve_Stock_NeverShowsAnOperatingPoint()
    {
        // At stock the curve's top point is not a cap, even when the curve read it clean.
        Assert.Equal("stock", Snapshot(Array.Empty<int>(), (1180, 2950)).DescribeCoreCurve());
    }
}
