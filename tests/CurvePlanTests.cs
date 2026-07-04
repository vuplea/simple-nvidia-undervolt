namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for <see cref="GpuTuning.BuildCurvePlan"/>, the core flatten-and-clamp logic that
/// turns a voltage cap (and optional clock) into per-anchor frequency deltas.</summary>
public class CurvePlanTests
{
    [Fact]
    public void PlainCap_FlattensCapAnchorAndAbove_ToStockClockThere()
    {
        var stock = TestCurves.Realistic();        // anchor 10 = (1000 mV, 2500 MHz)
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: null, capPoints: 8);

        Assert.Equal(1000, plan.CapMv);
        Assert.Equal(2500, plan.CapMhz);           // the stock clock at the cap

        // A plain cap has no offset (delta 0 at the cap), so the band changes nothing below it.
        for (int i = 0; i < stock.Count; i++)
        {
            int expectedKhz = i < 10 ? 0 : (2500 - stock[i].Mhz) * 1000;
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

        // d = 2600 - 2500 = 100. The band is the cap (10) plus the 7 below it (3..9): all carry +100.
        // Above the cap the curve is flattened down to 2600; below the band it is untouched.
        for (int i = 0; i < stock.Count; i++)
        {
            int expectedKhz = i switch
            {
                < 3 => 0,
                <= 10 => 100 * 1000,
                _ => (2600 - stock[i].Mhz) * 1000,
            };
            Assert.Equal(expectedKhz, plan.DeltasKhz[i]);
        }
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
    [InlineData(1180, null, 8)]    // cap at the very top
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

        // The flat top is the cap clock, and nothing rises above it.
        Assert.Equal(plan.CapMhz, effective.Max());

        // Every anchor at or above the cap voltage sits on the flat top (the band below stays under it,
        // so the boost still pins at the cap voltage).
        int capIndex = stock.FindIndex(p => p.Mv == plan.CapMv);
        for (int i = capIndex; i < effective.Length; i++)
        {
            Assert.Equal(plan.CapMhz, effective[i]);
        }
    }

    [Fact]
    public void Changes_ListExactlyTheMovedAnchors()
    {
        // Anchor i = (800 + 20i mV, 2000 + 50i MHz); cap k = 10 (1000 mV), band 3..9. Moved anchors:
        // 3..10 carry the cap's +100 offset, 11 (2550 MHz) rises to the 2600 flat top, and 13..19
        // flatten down to it. Anchor 12 already sits at 2600 and 0..2 are below the band - no change.
        var stock = TestCurves.Realistic();
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: 2600, capPoints: 8);

        var expected = new List<GpuTuning.CurveChange>();
        for (int i = 3; i <= 10; i++)
        {
            expected.Add(new(stock[i].Mv, stock[i].Mhz, stock[i].Mhz + 100, 100_000));
        }

        expected.Add(new(stock[11].Mv, 2550, 2600, 50_000));
        for (int i = 13; i <= 19; i++)
        {
            expected.Add(new(stock[i].Mv, stock[i].Mhz, 2600, (2600 - stock[i].Mhz) * 1000));
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
