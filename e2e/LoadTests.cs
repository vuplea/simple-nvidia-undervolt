using System.Globalization;
using System.Text.RegularExpressions;

namespace SimpleNvidiaUndervolt.E2E;

/// <summary>
/// Tests that need the GPU working, not idle: a Playwright-driven WebGL load
/// (<see cref="GpuLoadFixture"/>) holds the boost algorithm at its operating point, and the tests
/// assert where that point lands against an applied cap. This is the tool's actual contract — the
/// cap→flat segment is aimed so the boost, settling one 5 mV step below the flat start, lands on
/// the requested clock — and only a loaded card shows where the boost settles, so these are the
/// only tests that verify the undervolt does what it says, rather than that the curve write landed.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class LoadTests : IClassFixture<GpuLoadFixture>
{
    private readonly GpuFixture _gpu;
    private readonly GpuLoadFixture _load;

    public LoadTests(GpuFixture gpu, GpuLoadFixture load)
    {
        _gpu = gpu;
        _load = load;
    }

    [SkippableFact]
    public void PlainCapUnderLoad_BoostSettlesOnTheCapPoint()
    {
        var (stock, k) = StartLoadedRun();
        int capMv = stock[k].Mv;

        var (exitCode, output) = App.RunUndervolt("--mv", App.Arg(capMv));
        Assert.Equal(0, exitCode);
        App.AssertWriteConfirmed(output);

        var (settleMv, settleMhz) = SampleSettledPointOrSkip();
        AssertSettledOnTheEffectiveCap(stock, k, settleMv);

        // ...at the cap point's own stock clock, wherever in the anchor gap the settle voltage
        // falls - the straddle holds the requested clock at the settle point, so the tolerance is
        // read-back bins plus the thermal drift since the stock read, not an anchor step.
        Assert.InRange(settleMhz, stock[k].Mhz - 25, stock[k].Mhz + 25);
    }

    [SkippableFact]
    public void ReducedClockCapUnderLoad_BoostHoldsTheTargetAtTheCap()
    {
        var (stock, k) = StartLoadedRun();
        int capMv = stock[k].Mv;
        int targetMhz = stock[k].Mhz - 60; // a reduction: meaningful to measure, safe on any card

        var (exitCode, output) = App.RunUndervolt("--mv", App.Arg(capMv), "--mhz", App.Arg(targetMhz));
        Assert.Equal(0, exitCode);
        App.AssertWriteConfirmed(output);

        var (settleMv, settleMhz) = SampleSettledPointOrSkip();
        AssertSettledOnTheEffectiveCap(stock, k, settleMv);

        // The requested clock is held at the settle point. The flatten-at-the-cap shape fails this
        // (and the voltage check above): the boost lands one anchor below the cap, a stock-slope
        // step under the target. The stock-slope-continued shape fails it too on a wide anchor gap,
        // overshooting by half the stock step.
        Assert.InRange(settleMhz, targetMhz - 25, targetMhz + 25);
    }

    [SkippableFact]
    public void TelemetryUnderLoad_ReportsARealOperatingPoint()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);
        _load.SkipUnlessLoading(_gpu.Gpu);

        var (exitCode, output) = App.Run(null, "voltage");

        // The one-shot telemetry line: under load every column is live, so the reading must be a
        // boost-range operating point, not zeros or "n/a" placeholders.
        Assert.Equal(0, exitCode);
        Match m = Regex.Match(output, @"(\d+) mV\s+(\d+) MHz");
        Assert.True(m.Success, $"no 'NNN mV NNNN MHz' reading in: {output}");
        Assert.InRange(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), 600, 1300);
        Assert.True(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) >= NvApi.MinBoostClockKhz / 1000,
            $"clock under load below the boost range: {m.Value}");
    }

    /// <summary>The shared opening of a settle test: skip without a GPU, bring the load up first
    /// (so the card is at a working temperature before anything is planned or measured), then read
    /// the stock curve and pick the cap anchor.</summary>
    private (IReadOnlyList<(int Mv, int Mhz)> Stock, int CapAnchor) StartLoadedRun()
    {
        Skip.IfNot(_gpu.Available, _gpu.SkipReason);
        _load.SkipUnlessLoading(_gpu.Gpu);

        IReadOnlyList<(int Mv, int Mhz)> stock = StockProbe.ResetAndReadStockOrSkip(_gpu.Gpu);
        return (stock, StockProbe.PickCapAnchor(stock));
    }

    /// <summary>Asserts the settled voltage obeys the settle law against the curve the card
    /// actually holds: no lower than the cap point (a flatten-at-the-cap shape settles one step
    /// BELOW the cap, which is what the lower bound exists to catch) and no higher than one boost
    /// step below the effective top plateau's first anchor. The upper bound comes from the
    /// read-back rather than the written shape because the driver can round the written flat's
    /// tail a bin up, moving the plateau — and the settle — one or more anchors above the written
    /// flat start (the post-apply report carries a note when it does). The clock assertion beside
    /// each call bounds the plateau's height, so a wrong written shape can't vouch for itself
    /// through its own wrong plateau.</summary>
    private void AssertSettledOnTheEffectiveCap(IReadOnlyList<(int Mv, int Mhz)> stock, int k, int settleMv)
    {
        (int Mv, int Mhz)? point = GpuTuning.EffectiveOperatingPoint(NvApi.GetVfCurve(_gpu.Gpu));
        Skip.If(point is null, "the effective curve didn't read back cleanly - retry.");
        Assert.InRange(settleMv, stock[k].Mv - 2, point!.Value.Mv + 2);
    }

    /// <summary>The point the boost settled on: after a warm-up pause, samples the live telemetry
    /// until one voltage dominates, returning it with its average clock. A power-limited card skips —
    /// when TGP is pinned, the power governor picks the operating point and the cap's settle
    /// behavior isn't observable — after one attempt to lighten the load below the limit.</summary>
    private (int Mv, int Mhz) SampleSettledPointOrSkip()
    {
        for (int attempt = 0; ; attempt++)
        {
            Thread.Sleep(3000);

            var samples = new List<(int Mv, int Mhz, double Tgp)>();
            for (int i = 0; i < 10; i++)
            {
                var t = Telemetry.Sample(_gpu.Gpu);
                if (t.VoltageUv is { } uv && t.CoreMhz is { } mhz)
                {
                    samples.Add(((int)(uv / 1000), (int)mhz, t.PowerPercent ?? 0));
                }

                Thread.Sleep(700);
            }

            Skip.If(samples.Count < 8, "live voltage/clock telemetry is unavailable on this host.");

            if (samples.Average(s => s.Tgp) >= 92)
            {
                Skip.If(attempt > 0, "the card is power-limited even under a lightened load - TGP, "
                                     + "not the voltage cap, picks the operating point here.");
                _load.HalveIntensity();
                continue;
            }

            var modal = samples.GroupBy(s => s.Mv).OrderByDescending(g => g.Count()).First().ToList();
            Skip.If(modal.Count < samples.Count * 6 / 10,
                "the operating point never stabilized under load - retry on a quieter system.");
            return (modal[0].Mv, (int)Math.Round(modal.Average(s => (double)s.Mhz)));
        }
    }
}
