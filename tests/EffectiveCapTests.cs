namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for <see cref="GpuTuning.EffectiveCapPoint"/> — the "capped at 960 mV / 2880 MHz"
/// point the apply confirmation and the <c>status</c> core-curve line report — and for how
/// <see cref="TuningSnapshot.DescribeCoreCurve"/> renders it.</summary>
public class EffectiveCapTests
{
    [Fact]
    public void FlattenedCurve_CapIsTheLowestAnchorOfTheFlatTop()
    {
        // A plain cap at anchor 10 (1000 mV) flattens anchors 10..19 to its stock 2500 MHz.
        var stock = TestCurves.Realistic();
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: null, capPoints: 8);

        Assert.Equal((1000, 2500), GpuTuning.EffectiveCapPoint(TestCurves.Effective(stock, plan.DeltasKhz)));
    }

    [Fact]
    public void StockCurve_CapIsItsTopPoint()
    {
        // With 50 MHz anchor steps only the top anchor sits within one bin of the max; the caller
        // (DescribeCoreCurve) is what suppresses the cap on an untuned curve, not this computation.
        Assert.Equal((1180, 2950), GpuTuning.EffectiveCapPoint(TestCurves.Realistic()));
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
