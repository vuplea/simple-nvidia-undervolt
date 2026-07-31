namespace SimpleNvidiaUndervolt;

/// <summary>
/// The post-write refinement of a planned curve write. The driver realizes written clocks by
/// rounding every anchor onto its own clock bin, and a flat written near a bin edge can realize
/// as a staircase whose plateau — and so the boost's settle point — sits a step above the plan's
/// promise (see DEVELOPMENT.md, "Where the boost settles"). No value-level rule predicts the
/// rounding, but it is deterministic at a fixed thermal state and readable back in milliseconds,
/// so the exact write is found by probing: when the plan's own write misses its operating point,
/// nearby (cap, flat) pairs are written and judged against their read-backs, keeping the best
/// realization — measured, never modeled, so it carries no constants from any one card. The
/// budget is small and the values move by less than a bin, and a probe sequence that never lands
/// keeps the closest attempt: the refinement can only improve on the plan's write, and the
/// realized point is reported from the final read-back either way. What it cannot beat is the
/// thermal slice: anchors step with temperature, so a point landed at apply time can still sit a
/// step away at operating temperature — physics the report's notes carry, not error.
/// </summary>
internal static class CurveRefinement
{
    /// <summary>Extra writes the probing may spend after the plan's own. The measured cases
    /// converge in one or two; the budget covers a full bin of flat values either way.</summary>
    internal const int MaxExtraWrites = 8;

    /// <summary>The clock slack an on-target realization may keep: half a read-back bin — the
    /// resolution the effective curve reports at, so probing below it chases noise.</summary>
    internal const int ClockToleranceMhz = 4;

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
    /// realized, the cap re-centered to keep the settle clock) with a small scan as fallback.
    /// Ends on the best-scoring pair, re-applied if a later probe overwrote it; the outcome's
    /// realization is always the final read-back's, so a thermal step between probes can't leave
    /// a stale claim behind.
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

            if (v <= c || !tried.Add((c, v)))
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
    /// kept, re-centered so the settle-point clock stays on the request, and dropped a step — then
    /// a flat-value scan around the plan's own, downward through one bin and one step up.</summary>
    private static IEnumerable<(int C, int V)> Candidates(GpuTuning.CurvePlan plan,
        IReadOnlyList<(int Mv, int Mhz)> planReadBack)
    {
        if (GpuTuning.PointAtVoltage(planReadBack, plan.FlatMv) is { } flatRead)
        {
            int vr = flatRead.Mhz;
            yield return (plan.CapMhz, vr);
            yield return (2 * plan.SettleMhz - vr, vr);
            yield return (plan.CapMhz - 4, vr - 8);
        }

        for (int d = 1; d <= 6; d++)
        {
            yield return (plan.CapMhz, plan.FlatMhz - d);
        }

        yield return (plan.CapMhz, plan.FlatMhz + 1);
        yield return (plan.CapMhz, plan.FlatMhz + 2);
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
