namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for <see cref="CurveRefinement"/> — the post-write probing that lands the
/// planned operating point when the driver's bin rounding realizes the written flat as a
/// staircase. The driver is a synthetic snap function here: each test writes one measured (or
/// adversarial) rounding behavior and asserts the search's contract — lands when a landing
/// exists nearby, never worsens the plan's own realization, stays inside its budget, and always
/// reports the final read-back rather than a remembered one.</summary>
public class CurveRefinementTests
{
    /// <summary>A plan aimed mid-gap (cap anchor 10, settle 1015 mV / 2498 MHz) on the standard
    /// curve — the shape every snap function below distorts.</summary>
    private static (List<(int Mv, int Mhz)> Stock, GpuTuning.CurvePlan Plan) PlanFixture()
    {
        var stock = TestCurves.Realistic();
        return (stock, GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: 2498, capPoints: 8));
    }

    /// <summary>An apply function realizing each written flat anchor through
    /// <paramref name="snapFlat"/> (anchor index, written MHz) — the per-anchor rounding under
    /// test. Anchors outside the flat realize as written.</summary>
    private static Func<int[], IReadOnlyList<(int Mv, int Mhz)>> Driver(
        IReadOnlyList<(int Mv, int Mhz)> stock, Func<int, int, int> snapFlat)
        => deltas => stock.Select((p, i) =>
        {
            int written = p.Mhz + deltas[i] / 1000;
            return (p.Mv, i >= 11 ? snapFlat(i, written) : written);
        }).ToList();

    [Fact]
    public void PlanThatRealizesItsPoint_IsLeftAlone()
    {
        var (stock, plan) = PlanFixture();
        var driver = Driver(stock, (_, w) => w);   // realizes exactly as written

        var outcome = CurveRefinement.Refine(driver, stock, plan, driver(plan.DeltasKhz));

        Assert.True(outcome.OnTarget);
        Assert.Equal(0, outcome.ExtraWrites);
        Assert.Same(plan.DeltasKhz, outcome.DeltasKhz);
        Assert.Equal((plan.SettleMv, plan.SettleMhz), outcome.Realized);
    }

    [Fact]
    public void StaircasedFlat_LandsViaTheReadBacksOwnGridValue()
    {
        // The measured shape: a flat written off the bin grid keeps its first anchors rounded
        // down while the tail rounds up, so the plateau - and the settle - move a step higher.
        // The first targeted candidate re-aims the flat at the value its first anchor realized
        // (on-grid by construction), which realizes uniform and lands the point in one write.
        var (stock, plan) = PlanFixture();
        var driver = Driver(stock, (i, w) => w % 8 == 0 ? w : (w / 8) * 8 + (i <= 12 ? 0 : 8));

        var outcome = CurveRefinement.Refine(driver, stock, plan, driver(plan.DeltasKhz));

        Assert.True(outcome.OnTarget);
        Assert.Equal(1, outcome.ExtraWrites);
        Assert.Equal(plan.SettleMv, outcome.Realized!.Value.Mv);
    }

    [Fact]
    public void UnlandablePoint_KeepsTheClosestProbedWrite_WithinBudget()
    {
        // An adversary no probe can beat: the flat's first anchor always realizes a bin under the
        // rest, so the plateau always starts one anchor up and the settle voltage is never the
        // plan's. The search must spend at most its budget, keep whichever probe came closest,
        // and say the landing failed.
        var (stock, plan) = PlanFixture();
        var driver = Driver(stock, (i, w) => i == 11 ? w - 8 : w);

        var outcome = CurveRefinement.Refine(driver, stock, plan, driver(plan.DeltasKhz));

        Assert.False(outcome.OnTarget);
        Assert.InRange(outcome.ExtraWrites, 1, CurveRefinement.MaxExtraWrites + 1);

        // Never worse than the plan's own realization (same wrong plateau, clock no further off).
        var planned = GpuTuning.EffectiveOperatingPoint(driver(plan.DeltasKhz))!.Value;
        Assert.Equal(planned.Mv, outcome.Realized!.Value.Mv);
        Assert.True(Math.Abs(outcome.Realized.Value.Mhz - plan.SettleMhz)
                    <= Math.Abs(planned.Mhz - plan.SettleMhz));
    }

    [Fact]
    public void LandingWrite_ReportsTheFinalReadBack_NotTheRecordedOne()
    {
        // Between a probe and the landing re-write an anchor can step with temperature. When the
        // landing's read-back no longer shows the realization the probe recorded, the outcome
        // must carry the final read - here a plateau-less curve, so no point at all - rather than
        // claim the remembered one.
        var (stock, plan) = PlanFixture();
        int applies = 0;
        var flaky = new Func<int[], IReadOnlyList<(int Mv, int Mhz)>>(deltas =>
        {
            applies++;
            return applies <= CurveRefinement.MaxExtraWrites
                ? Driver(stock, (i, w) => i == 11 ? w - 8 : w)(deltas)
                : TestCurves.Realistic();   // the landing read: shifted to a plateau-less curve
        });

        var outcome = CurveRefinement.Refine(flaky, stock, plan, flaky(plan.DeltasKhz));

        Assert.False(outcome.OnTarget);
        Assert.Null(outcome.Realized);
    }

    [Fact]
    public void UnreadableFirstReadBack_StillConvergesFromTheScan()
    {
        // A transitional first read gives the targeted candidates nothing to aim with (its
        // collapsed clocks produce degenerate pairs, which are skipped); the scan then probes
        // near the plan and the rides-out read-backs land the point.
        var (stock, plan) = PlanFixture();
        var driver = Driver(stock, (_, w) => w);

        var outcome = CurveRefinement.Refine(driver, stock, plan, TestCurves.Collapsed());

        Assert.True(outcome.OnTarget);
        Assert.InRange(outcome.ExtraWrites, 1, CurveRefinement.MaxExtraWrites);
    }
}
