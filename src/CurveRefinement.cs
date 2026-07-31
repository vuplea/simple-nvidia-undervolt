namespace SimpleNvidiaUndervolt;

/// <summary>
/// The post-write refinement of a planned curve write. The driver realizes written clocks by
/// rounding every anchor onto its own clock bin, and a flat written near a bin edge can realize
/// as a staircase whose plateau — and so the boost's settle point — sits a step above the plan's
/// promise (see DEVELOPMENT.md, "Where the boost settles"). No value-level rule predicts the
/// rounding, but it is deterministic at a fixed thermal state and readable back in milliseconds,
/// so the exact write is found by probing: when the plan's own write misses its operating point,
/// nearby (cap, flat) pairs are written and judged against their read-backs, keeping the best
/// realization. The candidate heuristics are tuned on measured hardware, but correctness never
/// rests on them: every pair is judged by its own read-back, every pair stays within
/// <see cref="MaxCapAdjustMhz"/>/<see cref="MinRiseMhz"/>..<see cref="MaxRiseMhz"/> of the plan's
/// shape, and a probe sequence that never lands keeps the closest attempt — the refinement can
/// only improve on the plan's write, and the realized point is reported from the final read-back
/// either way. What it cannot beat is the thermal slice: anchors step with temperature, so a
/// point landed at apply time can still sit a step away at operating temperature — physics the
/// report's notes carry, not error.
/// </summary>
internal static class CurveRefinement
{
    /// <summary>Extra writes the probing may spend after the plan's own — the measured cases
    /// converge in one or two — plus at most one landing re-write when the best pair isn't the
    /// last one probed.</summary>
    internal const int MaxExtraWrites = 8;

    /// <summary>The clock slack an on-target realization may keep: half a read-back bin — the
    /// resolution the effective curve reports at, so probing below it chases noise.</summary>
    internal const int ClockToleranceMhz = 4;

    /// <summary>How far a probed cap may sit from the plan's (MHz): about a bin — the refinement
    /// absorbs bin rounding, so a candidate further out is chasing something else.</summary>
    internal const int MaxCapAdjustMhz = 8;

    /// <summary>The written cap→flat rise a probe may carry (MHz). Below the minimum the pair
    /// measurably folds into one plateau and drops the settle a step (see
    /// <c>GpuTuning.FlatSpreadMhz</c>); above the maximum the probe has left the plan's shape.
    /// The bounds also keep every write the probing can produce inside the shape the e2e suite
    /// asserts.</summary>
    internal const int MinRiseMhz = 8;

    internal const int MaxRiseMhz = 24;

    /// <summary>What the refinement left on the card: the deltas the final write holds (also
    /// copied into the plan's array by <see cref="GpuTuning.Apply"/>), the settle point of the
    /// final read-back (null when it wasn't readable), how many extra writes were spent, and
    /// whether the point is the plan's own.</summary>
    internal sealed record Outcome(int[] DeltasKhz, (int Mv, int Mhz)? Realized, int ExtraWrites,
        bool OnTarget);

    /// <summary>
    /// Judges the plan's write by <paramref name="planReadBack"/> and, when its realized settle
    /// point misses the plan's, probes alternative (cap, flat) pairs through
    /// <paramref name="apply"/> — which writes a delta array and returns the effective read-back.
    /// Candidates come from the read-back itself (the flat re-aimed at the value its first anchor
    /// realized, the cap re-centered to keep the settle clock) with an alternating scan around
    /// the plan's flat as fallback. Ends on the best-scoring pair, re-applied if a later probe
    /// overwrote it; the outcome's realization is always the final read-back's, so a thermal step
    /// between probes can't leave a stale claim behind.
    /// </summary>
    public static Outcome Refine(Func<int[], IReadOnlyList<(int Mv, int Mhz)>> apply,
        IReadOnlyList<(int Mv, int Mhz)> stock, GpuTuning.CurvePlan plan,
        IReadOnlyList<(int Mv, int Mhz)> planReadBack)
    {
        (int Mv, int Mhz)? realized = GpuTuning.EffectiveOperatingPoint(planReadBack);
        if (OnTarget(realized, plan))
        {
            return new Outcome(plan.DeltasKhz, realized, 0, true);
        }

        int bestScore = Score(realized, plan);
        int[] bestDeltas = plan.DeltasKhz;
        var bestRealized = realized;
        int[] lastWritten = plan.DeltasKhz;
        int writes = 0;

        var tried = new HashSet<(int C, int V)> { (plan.CapMhz, plan.FlatMhz) };
        foreach ((int c, int v) in Candidates(plan, planReadBack))
        {
            if (writes >= MaxExtraWrites)
            {
                break;
            }

            if (v - c < MinRiseMhz || v - c > MaxRiseMhz
                || Math.Abs(c - plan.CapMhz) > MaxCapAdjustMhz || !tried.Add((c, v)))
            {
                continue;
            }

            int[] deltas = DeltasFor(stock, plan, c, v);
            IReadOnlyList<(int Mv, int Mhz)> effective = apply(deltas);
            writes++;
            lastWritten = deltas;

            var r = GpuTuning.EffectiveOperatingPoint(effective);
            int score = Score(r, plan);
            if (score < bestScore)
            {
                (bestScore, bestDeltas, bestRealized) = (score, deltas, r);
            }

            if (OnTarget(r, plan))
            {
                return new Outcome(deltas, r, writes, true);
            }
        }

        // Land on the best probed pair. Its realization is re-read rather than recalled: between
        // probes an anchor can step with temperature, and the report must carry what the card
        // holds now, not what this pair realized a moment ago.
        if (!ReferenceEquals(lastWritten, bestDeltas))
        {
            bestRealized = GpuTuning.EffectiveOperatingPoint(apply(bestDeltas));
            writes++;
        }

        return new Outcome(bestDeltas, bestRealized, writes, OnTarget(bestRealized, plan));
    }

    /// <summary>Whether a realization is the plan's settle point: the voltage exactly (settle
    /// voltages move in whole boost steps, so any miss is a full step) and the clock within
    /// read-back resolution.</summary>
    internal static bool OnTarget((int Mv, int Mhz)? realized, GpuTuning.CurvePlan plan)
        => realized is { } r && r.Mv == plan.SettleMv
                             && Math.Abs(r.Mhz - plan.SettleMhz) <= ClockToleranceMhz;

    /// <summary>The miss of a realization, for keep-best: clock error plus heavily weighted
    /// voltage error — a settle step costs power at every operating second, a few MHz is within
    /// read-back noise. An unreadable realization never wins.</summary>
    private static int Score((int Mv, int Mhz)? realized, GpuTuning.CurvePlan plan)
        => realized is { } r
            ? Math.Abs(r.Mhz - plan.SettleMhz) + 4 * Math.Abs(r.Mv - plan.SettleMv)
            : int.MaxValue;

    /// <summary>The probe sequence: first the pairs the read-back suggests — the flat re-aimed at
    /// the value its first anchor realized (the read-back is the local bin grid), with the cap
    /// kept, re-centered so the settle-point clock stays on the request, and dropped a step —
    /// then a flat-value scan alternating around the plan's own before walking deeper down: the
    /// driver rounds both ways, and a budget spent all downward would never reach an upward
    /// landing. The consuming loop's shape bounds filter what a distorted read-back suggests.</summary>
    private static IEnumerable<(int C, int V)> Candidates(GpuTuning.CurvePlan plan,
        IReadOnlyList<(int Mv, int Mhz)> planReadBack)
    {
        if (GpuTuning.PointAtVoltage(planReadBack, plan.FlatMv) is { } flatRead)
        {
            int vr = flatRead.Mhz;
            yield return (plan.CapMhz, vr);
            yield return (RecenteredCap(plan, vr), vr);
            yield return (plan.CapMhz - 4, vr - 8);
        }

        foreach (int d in new[] { -1, +1, -2, +2, -3, -4, -5, -6 })
        {
            yield return (plan.CapMhz, plan.FlatMhz + d);
        }
    }

    /// <summary>The cap that keeps the settle-point clock on the plan's request when the flat is
    /// re-aimed at <paramref name="vr"/>. The settle sits <c>t</c> of the way into the cap→flat
    /// gap (the plan's own geometry, not a fixed fraction — anchor gaps vary within one curve),
    /// so the segment through (settle, request) solves to <c>(S − t·vr) / (1 − t)</c>; at
    /// <c>t = 0</c> — a gap of one boost step or same-mV anchors — the settle sits on the cap
    /// anchor and the cap is the requested clock itself.</summary>
    private static int RecenteredCap(GpuTuning.CurvePlan plan, int vr)
    {
        double t = plan.FlatMv == plan.CapMv ? 0
            : (double)(plan.SettleMv - plan.CapMv) / (plan.FlatMv - plan.CapMv);
        return t == 0 ? plan.SettleMhz : (int)Math.Round((plan.SettleMhz - t * vr) / (1 - t));
    }

    /// <summary>The delta array for a probed (cap, flat) pair — the plan's own shape
    /// (<see cref="GpuTuning.CapShapeMhz"/>) at the probed clocks, relative to the same stock
    /// curve the plan was built from. Anchor 0 stays unwritten like every plan.</summary>
    private static int[] DeltasFor(IReadOnlyList<(int Mv, int Mhz)> stock,
        GpuTuning.CurvePlan plan, int capMhz, int flatMhz)
    {
        int[] newMhz = GpuTuning.CapShapeMhz(stock, plan.CapIndex, capMhz, flatMhz, plan.CapPoints);
        var deltasKhz = new int[stock.Count];
        for (int i = 1; i < stock.Count; i++)
        {
            deltasKhz[i] = (newMhz[i] - stock[i].Mhz) * 1000;
        }

        return deltasKhz;
    }
}
