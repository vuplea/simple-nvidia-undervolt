namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for <see cref="GpuTuning.EffectiveCapPoint"/> — the "capped at 960 mV / 2880 MHz"
/// point the apply confirmation and the <c>status</c> core-curve line report — and for how
/// <see cref="TuningSnapshot.DescribeCoreCurve"/> renders it.</summary>
public class EffectiveCapTests
{
    [Fact]
    public void FlattenedCurve_CapIsTheAnchorBelowTheFlatTop()
    {
        // A plain cap at anchor 10 (1000 mV) flattens anchors 11..19 to anchor 11's stock 2550 MHz.
        // The boost settles one voltage step below the flat start, so the cap point is the anchor
        // below it - the cap anchor itself, exactly where the plan puts the requested point.
        var stock = TestCurves.Realistic();
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: null, capPoints: 8);

        Assert.Equal((1000, 2500), GpuTuning.EffectiveCapPoint(TestCurves.Effective(stock, plan.DeltasKhz)));
    }

    [Fact]
    public void SeamSnappedIntoTheFlat_CapReadsThroughIt()
    {
        // At a temperature away from the reference the read-back re-snaps flat anchors a bin apart.
        // The bin tolerance treats such a seam as part of the flat, so the report stays at the cap
        // anchor - the boost actually settles on the seam anchor, one above, but reading through the
        // tilt is what keeps a strongly tilted (staircase) transient read from reporting a near-peak
        // voltage as the cap.
        var stock = TestCurves.Realistic();
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: null, capPoints: 8);
        var effective = TestCurves.Effective(stock, plan.DeltasKhz);
        effective[11] = (effective[11].Mv, effective[11].Mhz - 8); // the flat start snaps a bin under

        Assert.Equal((1000, 2500), GpuTuning.EffectiveCapPoint(effective));
    }

    [Fact]
    public void StaircaseTransientRead_StillReportsTheCapAnchor()
    {
        // A read taken while the card's temperature is moving away from the reference tilts the
        // written flat into plateaus a bin apart. The anchor below the whole flat is still the cap
        // point; matching the top plateau exactly would walk the report far up the staircase.
        var stock = TestCurves.Realistic();
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: null, capPoints: 8);
        var effective = TestCurves.Effective(stock, plan.DeltasKhz);
        for (int i = 14; i < effective.Count; i++)
        {
            effective[i] = (effective[i].Mv, effective[i].Mhz + 8);   // upper plateau, one bin up
        }

        Assert.Equal((1000, 2500), GpuTuning.EffectiveCapPoint(effective));
    }

    // --- the post-apply report (GpuTuning.RealizedCapPoint) ---

    [Fact]
    public void RealizedCapPoint_ReadsTheClockAtTheAnchorThePlanCapped()
    {
        var stock = TestCurves.Realistic();
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: null, capPoints: 8);

        Assert.Equal((1000, 2500),
            GpuTuning.RealizedCapPoint(TestCurves.Effective(stock, plan.DeltasKhz), plan.CapMv));
    }

    [Fact]
    public void RealizedCapPoint_IsUnmovedByACumulativelyTiltedRead()
    {
        // The tilt a card far from its reference temperature produces: each flattened anchor reads
        // back a further 8 MHz up, so the whole flat becomes a staircase spanning several bins.
        // Shape-based inference walks up it; naming the capped anchor outright cannot.
        var stock = TestCurves.Realistic();
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: null, capPoints: 8);
        var effective = TestCurves.Effective(stock, plan.DeltasKhz);
        for (int i = 11; i < effective.Count; i++)
        {
            effective[i] = (effective[i].Mv, effective[i].Mhz + 8 * (i - 10));
        }

        Assert.Equal(1000, GpuTuning.RealizedCapPoint(effective, plan.CapMv)!.Value.Mv);
        Assert.True(GpuTuning.EffectiveCapPoint(effective)!.Value.Mv > 1000,
            "this is the read shape whose shape-based estimate walks up the staircase");
    }

    [Fact]
    public void RealizedCapPoint_IsNullWhenTheReadBackSkipsTheAnchor()
    {
        Assert.Null(GpuTuning.RealizedCapPoint(TestCurves.Realistic(), capMv: 1001));
    }

    [Fact]
    public void StockCurve_CapIsTheAnchorBelowItsTopPoint()
    {
        // With distinct anchor clocks the "flat" is the top anchor alone, so the anchor below it is
        // reported; the caller (DescribeCoreCurve) is what suppresses the cap on an untuned curve,
        // not this computation.
        Assert.Equal((1160, 2900), GpuTuning.EffectiveCapPoint(TestCurves.Realistic()));
    }

    [Fact]
    public void WhollyFlatCurve_CapIsTheFirstAnchor()
    {
        // Nothing sits below the flat, so its lowest anchor is the closest thing to a settle point.
        var flat = Enumerable.Range(0, 20).Select(i => (800 + i * 20, 2500)).ToList();

        Assert.Equal((800, 2500), GpuTuning.EffectiveCapPoint(flat));
    }

    [Fact]
    public void CollapsedRead_HasNoCapPoint()
    {
        Assert.Null(GpuTuning.EffectiveCapPoint(TestCurves.Collapsed()));
    }

    // --- status rendering (TuningSnapshot.DescribeCoreCurve) ---

    private static TuningSnapshot Snapshot(int[] offsetsKhz, (int Mv, int Mhz)? cap) => new()
    {
        Name = "GPU",
        CoreCurveOffsetsKhz = Reading<int[]>.Success(offsetsKhz),
        EffectiveCap = cap,
    };

    [Fact]
    public void DescribeCoreCurve_Tuned_AppendsTheCapPoint()
    {
        var snapshot = Snapshot(new[] { -200_000, -200_000 }, (960, 2880));

        Assert.Equal("-200 MHz on 2 point(s), capped at 960 mV / 2880 MHz", snapshot.DescribeCoreCurve());
    }

    [Fact]
    public void DescribeCoreCurve_TunedButCapUnreadable_ShowsTheOffsetsAlone()
    {
        Assert.Equal("-200 MHz on 1 point(s)", Snapshot(new[] { -200_000 }, cap: null).DescribeCoreCurve());
    }

    [Fact]
    public void DescribeCoreCurve_Stock_NeverShowsACap()
    {
        // At stock the curve's top point is not a cap, even when the curve read it clean.
        Assert.Equal("stock", Snapshot(Array.Empty<int>(), (1180, 2950)).DescribeCoreCurve());
    }
}
