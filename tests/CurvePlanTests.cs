namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for <see cref="GpuTuning.BuildCurvePlan"/>, the core flatten-and-clamp logic that
/// turns a voltage cap (and optional clock) into per-anchor frequency deltas. The geometry under
/// test: the cap→flat-start segment is a straight line through the settle point (one 5 mV boost
/// step below the flat start) holding the requested clock, spanning a fixed 16 MHz rise. On the
/// 20 mV-spaced test curve the settle voltage sits 15 mV into the gap, so the cap anchor is
/// written 12 MHz below the requested clock and the flat top 4 MHz above it.</summary>
public class CurvePlanTests
{
    [Fact]
    public void PlainCap_AimsTheSettlePointAtTheStockClock()
    {
        var stock = TestCurves.Realistic();        // anchor 10 = (1000 mV, 2500 MHz)
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: null, capPoints: 8);

        Assert.Equal(1015, plan.SettleMv);         // one boost step below the flat start
        Assert.Equal(2500, plan.SettleMhz);        // the stock clock at the cap, held at the settle
        Assert.Equal(1000, plan.CapMv);
        Assert.Equal(2488, plan.CapMhz);           // 12 below: the line through (1015, 2500)
        Assert.Equal(1020, plan.FlatMv);           // the flatten starts one anchor above the cap...
        Assert.Equal(2504, plan.FlatMhz);          // ...exactly 16 above the cap anchor

        // The band (3..10) carries the cap anchor's -12, everything from the flat start up sits on
        // the 2504 flat top, and below the band the curve is untouched.
        for (int i = 0; i < stock.Count; i++)
        {
            int expectedKhz = i switch
            {
                < 3 => 0,
                <= 10 => -12_000,
                _ => (2504 - stock[i].Mhz) * 1000,
            };
            Assert.Equal(expectedKhz, plan.DeltasKhz[i]);
        }
    }

    [Fact]
    public void ExplicitClock_PlacesTheLineThroughTheSettlePoint()
    {
        var stock = TestCurves.Realistic();        // anchor 10 = (1000 mV, 2500 MHz)
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: 2600, capPoints: 8);

        Assert.Equal(1015, plan.SettleMv);
        Assert.Equal(2600, plan.SettleMhz);        // the requested clock, held at the settle point
        Assert.Equal(2588, plan.CapMhz);
        Assert.Equal(88_000, plan.DeltasKhz[10]);  // the offset written at the cap anchor
        Assert.Equal(2604, plan.FlatMhz);

        // d = 2588 - 2500 = +88 carried by the band (3..10); the flat top is 2604 from the flat
        // start up; below the band the curve is untouched.
        for (int i = 0; i < stock.Count; i++)
        {
            int expectedKhz = i switch
            {
                < 3 => 0,
                <= 10 => 88 * 1000,
                _ => (2604 - stock[i].Mhz) * 1000,
            };
            Assert.Equal(expectedKhz, plan.DeltasKhz[i]);
        }
    }

    [Fact]
    public void FlatStartOneBoostStepAway_PutsTheSettleOnTheCapAnchor()
    {
        // 5 mV anchor spacing: the settle voltage IS the cap anchor, so the cap anchor holds the
        // requested clock outright and the whole 16 MHz rise sits above it.
        var stock = Enumerable.Range(0, 20).Select(i => (Mv: 800 + i * 5, Mhz: 2000 + i * 50)).ToList();
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: stock[10].Mv, targetMhz: 2600, capPoints: 8);

        Assert.Equal(stock[10].Mv, plan.SettleMv);
        Assert.Equal(2600, plan.SettleMhz);
        Assert.Equal(2600, plan.CapMhz);
        Assert.Equal(2616, plan.FlatMhz);
    }

    [Fact]
    public void TenMillivoltGap_StraddlesTheTarget_HalfTheSpreadEachSide()
    {
        // The measured Blackwell shape: a 10 mV gap above the cap puts the settle voltage mid-gap,
        // so the segment straddles the target - cap anchor 8 below it, flat top 8 above.
        var stock = TestCurves.Realistic();
        stock[11] = (stock[10].Mv + 10, stock[11].Mhz);
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: 2600, capPoints: 8);

        Assert.Equal(1005, plan.SettleMv);
        Assert.Equal(2600, plan.SettleMhz);
        Assert.Equal(2592, plan.CapMhz);
        Assert.Equal(2608, plan.FlatMhz);
    }

    [Theory]
    [InlineData(1180)]   // the top anchor
    [InlineData(1160)]   // the penultimate one: only the top anchor is left above, so no plateau forms
    public void CapWithoutRoomToFlattenAbove_Throws(int capMv)
    {
        // The flat needs two anchors above the cap to be a plateau. With less, no anchor is reduced -
        // a plain cap there would write nothing at all - so the request is refused rather than
        // silently applied as a no-op.
        var stock = TestCurves.Realistic();        // anchors 0..19, top = (1180 mV, 2950 MHz)

        var error = Assert.Throws<CliError>(
            () => GpuTuning.BuildCurvePlan(stock, capMv, targetMhz: null, capPoints: 8));
        Assert.Contains("no room above it to flatten", error.Message);
    }

    /// <summary>A curve whose clock doesn't rise across the pair at <paramref name="at"/>: level (the
    /// pinned floor clock the driver holds over the lowest anchors) or dipping by
    /// <paramref name="dipMhz"/> (a wobble a readable curve may still carry).</summary>
    private static List<(int Mv, int Mhz)> WithFlatSpotAbove(int at, int dipMhz = 0)
    {
        var stock = TestCurves.Realistic();
        stock[at + 1] = (stock[at + 1].Mv, stock[at].Mhz - dipMhz);
        return stock;
    }

    [Fact]
    public void DipAboveTheCap_DoesNotShaveTheRequestedClock()
    {
        // The flat sits a fixed rise above the cap anchor rather than on the stock clock above it,
        // so a curve that dips just above the cap can't drag the flat under the cap - which the
        // non-decreasing pass would otherwise take out of the cap anchor, silently delivering less
        // than was asked for.
        var stock = WithFlatSpotAbove(10, dipMhz: 10);
        Assert.True(GpuTuning.CurveFreqsReadable(stock));   // a dip this small still reads clean

        var plan = GpuTuning.BuildCurvePlan(stock, capMv: stock[10].Mv, targetMhz: 2600, capPoints: 8);

        Assert.Equal(2600, plan.SettleMhz);                 // the requested clock, intact
        Assert.Equal(2604, plan.FlatMhz);
    }

    [Fact]
    public void LevelCurveAboveTheCap_StillFormsThePlateau_AndHoldsTheStockClock()
    {
        // Across a level stock pair the fixed rise still separates the flat from the cap anchor, so
        // the plateau begins above the cap and the settle point keeps the cap's stock clock -
        // nothing depends on the stock curve rising between the two.
        var stock = WithFlatSpotAbove(10);
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: stock[10].Mv, targetMhz: null, capPoints: 8);

        Assert.Equal(stock[10].Mhz, plan.SettleMhz);
        Assert.Equal(plan.CapMhz + 16, plan.FlatMhz);
    }

    [Fact]
    public void CapWithExactlyTwoAnchorsAbove_IsAllowed_AndLowersTheTop()
    {
        // The tightest cap that still forms a plateau: the flat spans the last two anchors, so the
        // top one comes down to it.
        var stock = TestCurves.Realistic();        // anchor 17 = (1140 mV, 2850 MHz)
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1140, targetMhz: null, capPoints: 8);

        Assert.Equal(2850, plan.SettleMhz);
        Assert.Equal(2838, plan.CapMhz);
        Assert.Equal(2854, plan.FlatMhz);          // the fixed rise above the cap anchor
        Assert.Equal(2854, TestCurves.Apply(stock, plan.DeltasKhz).Max());   // down from 2950
    }

    [Theory]
    [InlineData(1)]   // only the cap point itself
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(10)]
    public void CapBand_SharesTheCapAnchorsOffset_DownToCapPointsAnchors(int capPoints)
    {
        var stock = TestCurves.Realistic();        // capMv 1000 -> anchor k = 10
        const int k = 10, d = 88;                  // 2600 requested -> 2588 written at the cap anchor
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: 2600, capPoints);

        int bandStart = k - (capPoints - 1);
        for (int i = 0; i < bandStart; i++)
        {
            Assert.Equal(0, plan.DeltasKhz[i]);            // untouched below the band
        }

        for (int i = bandStart; i <= k; i++)
        {
            Assert.Equal(d * 1000, plan.DeltasKhz[i]);     // band + cap share the cap anchor's offset
        }
    }

    [Theory]
    [InlineData(1000, null, 8)]    // plain cap
    [InlineData(1000, 2600, 8)]    // raise the clock at the cap
    [InlineData(1000, 2300, 8)]    // a clock below points beneath the cap (exercises the downward clamp)
    [InlineData(840, null, 8)]     // cap near the bottom of the curve
    [InlineData(1140, null, 8)]    // the highest cap that still has room to flatten above
    [InlineData(1000, 2600, 1)]    // narrowest band
    [InlineData(1000, 2600, 20)]   // band wider than the curve below the cap
    public void Result_IsAFlatCappedNonDecreasingCurve(int capMv, int? targetMhz, int capPoints)
    {
        var stock = TestCurves.Realistic();
        var plan = GpuTuning.BuildCurvePlan(stock, capMv, targetMhz, capPoints);
        int[] effective = TestCurves.Apply(stock, plan.DeltasKhz);

        // Non-decreasing — the driver rejects a curve that dips.
        for (int i = 1; i < effective.Length; i++)
        {
            Assert.True(effective[i] >= effective[i - 1],
                $"curve dips at anchor {i}: {effective[i - 1]} -> {effective[i]}");
        }

        // The flat top is the plan's flat clock, and nothing rises above it.
        Assert.Equal(plan.FlatMhz, effective.Max());

        // The cap anchor holds its written clock, every anchor from the flat start up sits on the
        // flat top, and the settle point is the segment's clock at the settle voltage — the
        // requested clock, when no floor bit.
        int capIndex = stock.FindIndex(p => p.Mv == plan.CapMv);
        Assert.Equal(plan.CapMhz, effective[capIndex]);
        int flatIndex = stock.FindIndex(p => p.Mv == plan.FlatMv);
        for (int i = flatIndex; i < effective.Length; i++)
        {
            Assert.Equal(plan.FlatMhz, effective[i]);
        }

        Assert.Equal(plan.SettleMhz, GpuTuning.SegmentClockAt(
            (plan.CapMv, plan.CapMhz), (plan.FlatMv, plan.FlatMhz), plan.SettleMv));
        if (targetMhz is { } f)
        {
            Assert.Equal(f, plan.SettleMhz);
        }
    }

    [Fact]
    public void Changes_ListExactlyTheMovedAnchors()
    {
        // Anchor i = (800 + 20i mV, 2000 + 50i MHz); cap k = 10 (1000 mV), band 3..10, flat start 11.
        // Moved anchors: 3..10 carry the cap anchor's +88 (the 2588 the line through the 1015 mV /
        // 2600 MHz settle point puts there), and 11..19 sit on the 2604 flat top, 16 above it.
        var stock = TestCurves.Realistic();
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: 2600, capPoints: 8);

        var expected = new List<GpuTuning.CurveChange>();
        for (int i = 3; i <= 10; i++)
        {
            expected.Add(new(stock[i].Mv, stock[i].Mhz, stock[i].Mhz + 88, 88_000));
        }

        for (int i = 11; i <= 19; i++)
        {
            expected.Add(new(stock[i].Mv, stock[i].Mhz, 2604, (2604 - stock[i].Mhz) * 1000));
        }

        Assert.Equal(expected, plan.Changes);
    }

    [Fact]
    public void CapSnapsToTheNearestAnchorVoltage()
    {
        var stock = TestCurves.Realistic();        // anchors at ...980, 1000, 1020... mV
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1006, targetMhz: null, capPoints: 8);

        Assert.Equal(1000, plan.CapMv);            // 1006 is closest to the 1000 mV anchor
    }

    [Theory]
    [InlineData(null)]   // a plain cap
    [InlineData(2600)]   // a cap that also sets a clock
    public void OnACollapsedCurve_Throws(int? targetMhz)
    {
        // The flatten reads the curve's frequencies either way, so any cap needs a readable curve.
        var collapsed = TestCurves.Collapsed();
        Assert.False(GpuTuning.CurveFreqsReadable(collapsed));

        Assert.Throws<CliError>(
            () => GpuTuning.BuildCurvePlan(collapsed, capMv: 1000, targetMhz, capPoints: 8));
    }

    [Fact]
    public void CapAtTheLowestAnchor_ReportsTheFlooredClock_NotTheUnwritableTarget()
    {
        // Anchor 0 has no control entry, so with the cap there a below-stock clock can't be realized:
        // every writable anchor is floored to anchor 0's stock clock, and the reported settle clock
        // must be that floor, not the target the plan never wrote anywhere.
        var stock = TestCurves.Realistic();        // anchor 0 = (800 mV, 2000 MHz)
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 800, targetMhz: 1900, capPoints: 8);

        Assert.Equal(800, plan.CapMv);
        Assert.Equal(2000, plan.CapMhz);
        Assert.Equal(2000, plan.SettleMhz);
        Assert.Equal(0, plan.DeltasKhz[0]);        // nothing was written at the unwritable anchor 0
    }

    [Fact]
    public void Anchor0_IsNeverWritten_EvenWhenTheBandReachesIt()
    {
        // A band wider than the curve reaches anchor 0, and a raised clock would give it the cap's offset -
        // but the lowest anchor has no control entry, so the plan must leave it at 0 to match a real apply.
        var stock = TestCurves.Realistic();
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: 2600, capPoints: 40);

        Assert.Equal(0, plan.DeltasKhz[0]);
        Assert.DoesNotContain(plan.Changes, c => c.Mv == stock[0].Mv);
    }

    [Fact]
    public void EmptyCurve_Throws()
    {
        Assert.Throws<CliError>(
            () => GpuTuning.BuildCurvePlan(new List<(int Mv, int Mhz)>(), capMv: 1000, targetMhz: null, capPoints: 8));
    }
}
