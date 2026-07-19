namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for <see cref="GpuTuning.BuildCurvePlan"/>, the core flatten-and-clamp logic that
/// turns a voltage cap (and optional clock) into per-anchor frequency deltas.</summary>
public class CurvePlanTests
{
    [Fact]
    public void PlainCap_KeepsTheCapAtStock_AndFlattensFromTheAnchorAbove()
    {
        var stock = TestCurves.Realistic();        // anchor 10 = (1000 mV, 2500 MHz)
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: null, capPoints: 8);

        Assert.Equal(1000, plan.CapMv);
        Assert.Equal(2500, plan.CapMhz);           // the stock clock at the cap
        Assert.Equal(0, plan.CapDeltaMhz);         // no offset written at the cap
        Assert.Equal(1020, plan.FlatMv);           // the flatten starts one anchor above the cap...
        Assert.Equal(2550, plan.FlatMhz);          // ...at that anchor's own stock clock

        // A plain cap has no offset, so everything through the flat start is at stock; above it the
        // curve is flattened down to the flat start's stock clock.
        for (int i = 0; i < stock.Count; i++)
        {
            int expectedKhz = i <= 11 ? 0 : (2550 - stock[i].Mhz) * 1000;
            Assert.Equal(expectedKhz, plan.DeltasKhz[i]);
        }
    }

    [Fact]
    public void ExplicitClock_RaisesTheCapBandAndFlattensAbove()
    {
        var stock = TestCurves.Realistic();        // anchor 10 = (1000 mV, 2500 MHz)
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: 2600, capPoints: 8);

        Assert.Equal(1000, plan.CapMv);
        Assert.Equal(2600, plan.CapMhz);
        Assert.Equal(100, plan.CapDeltaMhz);       // the offset written at the cap anchor
        Assert.Equal(2650, plan.FlatMhz);          // the flat = the stock clock above the cap, +100

        // d = 2600 - 2500 = 100. The band is the cap (10) plus the 7 below it (3..9), and the flat
        // start (11) carries the same offset; all carry +100. Above the flat start the curve is
        // flattened down to 2650; below the band it is untouched.
        for (int i = 0; i < stock.Count; i++)
        {
            int expectedKhz = i switch
            {
                < 3 => 0,
                <= 11 => 100 * 1000,
                _ => (2650 - stock[i].Mhz) * 1000,
            };
            Assert.Equal(expectedKhz, plan.DeltasKhz[i]);
        }
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
        // The flat's base is floored at the cap's own stock clock, so a curve that dips just above
        // the cap can't drag the flat under it - which the non-decreasing pass would otherwise take
        // out of the cap anchor, silently delivering less than was asked for.
        var stock = WithFlatSpotAbove(10, dipMhz: 10);
        Assert.True(GpuTuning.CurveFreqsReadable(stock));   // a dip this small still reads clean

        var plan = GpuTuning.BuildCurvePlan(stock, capMv: stock[10].Mv, targetMhz: 2600, capPoints: 8);

        Assert.Equal(2600, plan.CapMhz);                    // the requested clock, intact
        Assert.Equal(2600, plan.FlatMhz);
    }

    [Fact]
    public void LevelCurveAboveTheCap_KeepsTheCapClock_WithThePlateauStartingThere()
    {
        // Across a level pair there is no higher anchor to start the flat on, so the plateau begins
        // at the cap itself and the boost settles a step lower. Nothing is shaved: the cap anchor
        // still holds its stock clock, which is all this can promise there.
        var stock = WithFlatSpotAbove(10);
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: stock[10].Mv, targetMhz: null, capPoints: 8);

        Assert.Equal(stock[10].Mhz, plan.CapMhz);
        Assert.Equal(plan.CapMhz, plan.FlatMhz);
        Assert.Equal(0, plan.DeltasKhz[10]);                // the cap anchor is untouched
    }

    [Fact]
    public void CapWithExactlyTwoAnchorsAbove_IsAllowed_AndLowersTheTop()
    {
        // The tightest cap that still forms a plateau: the flat spans the last two anchors, so the
        // top one comes down to it.
        var stock = TestCurves.Realistic();        // anchor 17 = (1140 mV, 2850 MHz)
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1140, targetMhz: null, capPoints: 8);

        Assert.Equal(2850, plan.CapMhz);
        Assert.Equal(2900, plan.FlatMhz);          // anchor 18's own stock clock
        Assert.Equal(2900, TestCurves.Apply(stock, plan.DeltasKhz).Max());   // down from 2950
    }

    [Theory]
    [InlineData(1)]   // only the cap point itself
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(10)]
    public void CapBand_SharesTheCapOffset_DownToCapPointsAnchors(int capPoints)
    {
        var stock = TestCurves.Realistic();        // capMv 1000 -> anchor k = 10, d = 100
        const int k = 10, d = 100;
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: 2600, capPoints);

        int bandStart = k - (capPoints - 1);
        for (int i = 0; i < bandStart; i++)
        {
            Assert.Equal(0, plan.DeltasKhz[i]);            // untouched below the band
        }

        for (int i = bandStart; i <= k; i++)
        {
            Assert.Equal(d * 1000, plan.DeltasKhz[i]);     // band + cap share the cap's offset
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

        // The cap anchor holds the cap clock, and every anchor from the flat start up sits on the
        // flat top (the cap and the band below stay under it, so the boost settles on the cap).
        int capIndex = stock.FindIndex(p => p.Mv == plan.CapMv);
        Assert.Equal(plan.CapMhz, effective[capIndex]);
        int flatIndex = stock.FindIndex(p => p.Mv == plan.FlatMv);
        for (int i = flatIndex; i < effective.Length; i++)
        {
            Assert.Equal(plan.FlatMhz, effective[i]);
        }
    }

    [Fact]
    public void Changes_ListExactlyTheMovedAnchors()
    {
        // Anchor i = (800 + 20i mV, 2000 + 50i MHz); cap k = 10 (1000 mV), band 3..9, flat start 11.
        // Moved anchors: 3..11 carry the cap's +100 offset (the flat start's own stock 2550 + 100 is
        // the 2650 flat top), 12 (2600 MHz) rises to it, and 14..19 flatten down to it. Anchor 13
        // already sits at 2650 and 0..2 are below the band - no change.
        var stock = TestCurves.Realistic();
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: 2600, capPoints: 8);

        var expected = new List<GpuTuning.CurveChange>();
        for (int i = 3; i <= 11; i++)
        {
            expected.Add(new(stock[i].Mv, stock[i].Mhz, stock[i].Mhz + 100, 100_000));
        }

        expected.Add(new(stock[12].Mv, 2600, 2650, 50_000));
        for (int i = 14; i <= 19; i++)
        {
            expected.Add(new(stock[i].Mv, stock[i].Mhz, 2650, (2650 - stock[i].Mhz) * 1000));
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
        // every writable anchor is floored to anchor 0's stock clock, and the reported cap must be
        // that floor, not the target the plan never wrote anywhere.
        var stock = TestCurves.Realistic();        // anchor 0 = (800 mV, 2000 MHz)
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 800, targetMhz: 1900, capPoints: 8);

        Assert.Equal(800, plan.CapMv);
        Assert.Equal(2000, plan.CapMhz);
        Assert.Equal(0, plan.CapDeltaMhz);         // nothing was written at the unwritable anchor 0
        Assert.Equal(0, plan.DeltasKhz[0]);
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
