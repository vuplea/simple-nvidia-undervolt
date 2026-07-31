namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for <see cref="GpuTuning.VerifyWriteReachedCurve"/>, the post-write cross-check that
/// confirms a curve write actually reached the effective (status) curve. It is the genuine control-table
/// layout check: on a card whose control offsets don't fit, the deltas land in reserved bytes and the
/// curve reads back at stock, which this must catch. Exercised here against synthetic read-backs.</summary>
public class CurveWriteVerificationTests
{
    private static GpuTuning.CurvePlan PlainCapPlan(out List<(int Mv, int Mhz)> stock)
    {
        stock = TestCurves.Realistic();   // anchor 10 = (1000 mV, 2500 MHz); anchors 11..19 sit above it
        return GpuTuning.BuildCurvePlan(stock, capMv: 1000, targetMhz: null, capPoints: 8);
    }

    /// <summary>An effective read-back built by overriding the frequency at each anchor's voltage.
    /// (Absolute frequencies — unlike <see cref="TestCurves.Effective"/>, which takes kHz deltas.)</summary>
    private static List<(int Mv, int Mhz)> WithFreqs(IReadOnlyList<(int Mv, int Mhz)> stock, int[] mhz)
        => stock.Select((p, i) => (p.Mv, mhz[i])).ToList();

    [Fact]
    public void WriteThatLanded_IsConfirmed()
    {
        var plan = PlainCapPlan(out var stock);
        var effective = TestCurves.Effective(stock, plan.DeltasKhz); // the curve the write intends

        Assert.Equal(GpuTuning.WriteVerification.Confirmed, GpuTuning.VerifyWriteReachedCurve(plan, effective));
    }

    [Fact]
    public void WriteThatMissed_LeavesTheCurveAtStock_IsNotReflected()
    {
        // The failure mode: control offsets don't fit, deltas hit reserved bytes, the curve is unchanged.
        var plan = PlainCapPlan(out var stock);
        var effective = WithFreqs(stock, stock.Select(p => p.Mhz).ToArray());

        Assert.Equal(GpuTuning.WriteVerification.NotReflected, GpuTuning.VerifyWriteReachedCurve(plan, effective));
    }

    [Fact]
    public void DriverSmoothedTheFlatten_ButAnchorsCameDown_IsConfirmed()
    {
        // The driver honored the reduction only partway (more than a bin, less than requested). The write
        // clearly reached the curve, so this must not be mistaken for a layout mismatch.
        var plan = PlainCapPlan(out var stock);
        var mhz = stock.Select(p => p.Mhz).ToArray();
        foreach (var c in plan.Changes.Where(c => c.NewMhz < c.OldMhz))
        {
            mhz[stock.FindIndex(p => p.Mv == c.Mv)] = c.OldMhz - 30; // down 30 MHz (> one 15 MHz bin)
        }

        Assert.Equal(GpuTuning.WriteVerification.Confirmed,
            GpuTuning.VerifyWriteReachedCurve(plan, WithFreqs(stock, mhz)));
    }

    [Fact]
    public void SubBinNoise_DoesNotCountAsMovement_IsNotReflected()
    {
        // A change smaller than the bin granularity is noise, not a realized reduction.
        var plan = PlainCapPlan(out var stock);
        var mhz = stock.Select(p => p.Mhz).ToArray();
        foreach (var c in plan.Changes.Where(c => c.NewMhz < c.OldMhz))
        {
            mhz[stock.FindIndex(p => p.Mv == c.Mv)] = c.OldMhz - 5; // below the 15 MHz bin
        }

        Assert.Equal(GpuTuning.WriteVerification.NotReflected,
            GpuTuning.VerifyWriteReachedCurve(plan, WithFreqs(stock, mhz)));
    }

    [Theory]
    [InlineData(9, nameof(GpuTuning.WriteVerification.Confirmed))]     // every reduction landed
    [InlineData(5, nameof(GpuTuning.WriteVerification.Confirmed))]     // the exact majority boundary
    [InlineData(4, nameof(GpuTuning.WriteVerification.Unverifiable))]  // just under it: wobble, no verdict
    [InlineData(0, nameof(GpuTuning.WriteVerification.NotReflected))]  // nothing moved - the layout-mismatch signature
    public void MovedAnchorCount_DecidesTheVerdict(int movedCount, string expectedVerdict)
    {
        // Only a read-back where NO reduction registered convicts the write (a true layout miss leaves
        // every anchor at stock); a moved minority must yield no verdict rather than a spurious revert.
        var expected = Enum.Parse<GpuTuning.WriteVerification>(expectedVerdict);
        var plan = PlainCapPlan(out var stock);
        var reduced = plan.Changes.Where(c => c.OldMhz - c.NewMhz >= 15).ToList();
        // capMv 1000 flattens the flat start and everything above (11..19) down by at least a bin;
        // the band's small straddle share is sub-bin and never judged.
        Assert.Equal(9, reduced.Count);

        var mhz = stock.Select(p => p.Mhz).ToArray();
        for (int i = 0; i < movedCount; i++)
        {
            mhz[stock.FindIndex(p => p.Mv == reduced[i].Mv)] = reduced[i].NewMhz;
        }

        Assert.Equal(expected, GpuTuning.VerifyWriteReachedCurve(plan, WithFreqs(stock, mhz)));
    }

    [Fact]
    public void PlanWhoseReductionsAreAllSubBin_IsUnverifiable()
    {
        // Near the top of a real curve the anchors sit only 7-8 MHz apart, so a cap two anchors below
        // the top plans a single sub-bin reduction. Even a perfectly landed write can't register
        // against the bin-sized noise tolerance, so there is nothing measurable - no verdict, never
        // the revert path (which would misdiagnose a layout mismatch).
        var stock = Enumerable.Range(0, 20).Select(i => (Mv: 800 + i * 20, Mhz: 2000 + i * 8)).ToList();
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: stock[17].Mv, targetMhz: null, capPoints: 8);
        Assert.All(plan.Changes, c => Assert.True(c.OldMhz - c.NewMhz < 15)); // all below the bin

        var landed = TestCurves.Effective(stock, plan.DeltasKhz);
        Assert.Equal(GpuTuning.WriteVerification.Unverifiable,
            GpuTuning.VerifyWriteReachedCurve(plan, landed));
    }

    [Fact]
    public void PureOverclock_WithNoReductions_IsUnverifiable()
    {
        // No anchor is flattened down, so there is no reliable signal (a raise the driver may clamp).
        var changes = new[] { new GpuTuning.CurveChange(1000, 2500, 2600, 100_000) };
        var plan = new GpuTuning.CurvePlan(SettleMv: 1015, SettleMhz: 2638, CapMv: 1000, CapMhz: 2600,
            FlatMv: 1020, FlatMhz: 2650, changes, DeltasKhz: Array.Empty<int>(),
            CapIndex: 10, CapPoints: 8);

        Assert.Equal(GpuTuning.WriteVerification.Unverifiable,
            GpuTuning.VerifyWriteReachedCurve(plan, TestCurves.Realistic()));
    }

    [Fact]
    public void ReadBackMissingTheEditedAnchors_IsUnverifiable()
    {
        // The read-back holds no matching voltages, so no reduction can be judged - proceed without a verdict.
        var plan = PlainCapPlan(out var stock);
        var elsewhere = stock.Select(p => (p.Mv + 1, p.Mhz)).ToList();

        Assert.Equal(GpuTuning.WriteVerification.Unverifiable,
            GpuTuning.VerifyWriteReachedCurve(plan, elsewhere));
    }

    [Fact]
    public void DuplicateMillivoltsInTheReadBack_AreNotJudged_TheRestCarryTheVerdict()
    {
        // Adjacent status anchors can truncate to the same millivolt, and a read-back can even
        // repeat anchors - here appended still at their stock clock, which keeps the read
        // monotonic. An ambiguous millivolt can't be attributed to one anchor, so those changes
        // are skipped and the landed unique-millivolt reductions still confirm the write.
        var plan = PlainCapPlan(out var stock);
        var effective = TestCurves.Effective(stock, plan.DeltasKhz);
        effective.AddRange(stock.Where(p => p.Mv >= 1100)); // 5 of the 9 reduced anchors, duplicated

        Assert.Equal(GpuTuning.WriteVerification.Confirmed, GpuTuning.VerifyWriteReachedCurve(plan, effective));
    }

    [Fact]
    public void ChangeOnlyOnADuplicatedMillivoltAnchor_YieldsNoVerdict()
    {
        // A single-anchor document tuning the upper member of a same-millivolt pair: matching by
        // voltage would read the untouched lower member's clock and convict a landed write -
        // reverting a good tuning - so an ambiguous anchor yields no judgment at all.
        var stock = TestCurves.Realistic();
        stock[13] = (stock[12].Mv, stock[13].Mhz);
        var changes = new[]
        {
            new GpuTuning.CurveChange(stock[13].Mv, stock[13].Mhz, stock[13].Mhz - 20, -20_000),
        };
        var effective = stock.Select((p, i) => i == 13 ? (p.Mv, p.Mhz - 20) : p).ToList();

        Assert.Equal(GpuTuning.WriteVerification.Unverifiable,
            GpuTuning.VerifyWriteReachedCurve(changes, effective, stock));
    }

    /// <summary>A curve a bin below another — the shape the live card takes when it is warmer than
    /// the reference was at capture.</summary>
    private static List<(int Mv, int Mhz)> ShiftedDown(IReadOnlyList<(int Mv, int Mhz)> curve, int mhz = 20)
        => curve.Select(p => (p.Mv, p.Mhz - mhz)).ToList();

    [Fact]
    public void ReferencePlan_WriteThatMissed_IsNotReflected_DespiteTheThermalShift()
    {
        // A plan built from the saved reference measures from the curve as captured, while the card
        // now runs a bin lower. A write that landed nowhere reads back as that live curve - and
        // against the reference alone every anchor looks "moved", confirming a write that never
        // happened on exactly the unported card this check exists to catch.
        var reference = TestCurves.Realistic();
        var plan = GpuTuning.BuildCurvePlan(reference, capMv: 1000, targetMhz: null, capPoints: 8);
        var liveStock = ShiftedDown(reference);

        Assert.Equal(GpuTuning.WriteVerification.Confirmed,
            GpuTuning.VerifyWriteReachedCurve(plan, liveStock));
        Assert.Equal(GpuTuning.WriteVerification.NotReflected,
            GpuTuning.VerifyWriteReachedCurve(plan, liveStock, liveStock));
    }

    [Fact]
    public void ReferencePlan_WriteThatLanded_IsConfirmed()
    {
        // The driver applies the written deltas to the live curve, not to the reference the plan
        // measured from, so the baseline must be what the read-back is judged against.
        var reference = TestCurves.Realistic();
        var plan = GpuTuning.BuildCurvePlan(reference, capMv: 1000, targetMhz: null, capPoints: 8);
        var liveStock = ShiftedDown(reference);

        Assert.Equal(GpuTuning.WriteVerification.Confirmed,
            GpuTuning.VerifyWriteReachedCurve(plan, TestCurves.Effective(liveStock, plan.DeltasKhz), liveStock));
    }

    [Fact]
    public void ReferencePlan_WithACollapsedBaseline_IsUnverifiable()
    {
        // The baseline was read during a power-state transition: no verdict, rather than one built
        // on a collapsed pre-write curve.
        var reference = TestCurves.Realistic();
        var plan = GpuTuning.BuildCurvePlan(reference, capMv: 1000, targetMhz: null, capPoints: 8);

        Assert.Equal(GpuTuning.WriteVerification.Unverifiable, GpuTuning.VerifyWriteReachedCurve(
            plan, TestCurves.Effective(reference, plan.DeltasKhz), TestCurves.Collapsed()));
    }

    [Fact]
    public void CollapsedReadBack_IsUnverifiable()
    {
        // A power-state change between the write and the read-back collapsed every clock, so a realized
        // reduction can't be told from the collapse. No verdict - rather than counting the collapsed
        // values as "moved" and confirming a write that may have missed.
        var plan = PlainCapPlan(out var stock);
        var collapsed = WithFreqs(stock, stock.Select(_ => 195).ToArray());

        Assert.Equal(GpuTuning.WriteVerification.Unverifiable,
            GpuTuning.VerifyWriteReachedCurve(plan, collapsed));
    }
}
