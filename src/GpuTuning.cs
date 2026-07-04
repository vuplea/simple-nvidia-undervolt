namespace SimpleNvidiaUndervolt;

/// <summary>
/// Reset and write of the GPU tuning that MSI Afterburner programs into the driver, plus the curve
/// reads, plausibility checks and interpolation the writes depend on.
///
/// On Ada/Blackwell the tuning lives in several places: the core V/F curve offset in the
/// ClkVfPoints control table, the memory offset baked into the absolute P0 memory clock in
/// pstates 2.0, and the core voltage boost percentage. <see cref="TuningSnapshot"/> reads the same
/// knobs back for display.
/// </summary>
internal static class GpuTuning
{
    /// <summary>Resets tuning to stock, returning a human-readable line per action. Refuses on a card
    /// whose curve doesn't validate — the reset writes zeroes straight into the hardware-specific
    /// control-table offsets, so it needs the same recognized-curve guard as the undervolt write.
    /// A step the driver rejects (e.g. when not elevated) throws, so a partial reset surfaces as a
    /// failure the caller can report instead of masquerading as success.</summary>
    public static IReadOnlyList<string> Clear(IntPtr gpu)
    {
        IReadOnlyList<(int Mv, int Mhz)> curve = NvApi.GetVfCurve(gpu);
        if (!CurveVoltsPlausible(curve))
        {
            throw UnrecognizedCurveError(gpu, "reset the tuning");
        }

        // Zero every per-point core-clock offset the control table can hold (equivalent to the
        // driver's own reset) - the whole table, not just the anchors visible in this status decode,
        // so a foreign delta past a truncated read is cleared too - then the P0 clock offsets and
        // the voltage boost.
        int[] deltas = NvApi.GetCurveFreqDeltasKhz(gpu, NvApi.MaxCurveAnchors);
        NvApi.SetCurveFreqDeltasKhz(gpu, new int[NvApi.MaxCurveAnchors]);
        int cleared = deltas.Count(d => d != 0);
        NvApi.SetPstate0Offsets(gpu, graphicsDeltaKhz: 0, memoryDeltaKhz: 0, coreVoltageDeltaUv: 0);
        NvApi.SetCoreVoltageBoostPercent(gpu, 0);

        return new[]
        {
            cleared > 0 ? $"Core V/F curve: cleared {cleared} offset point(s)." : "Core V/F curve: already stock.",
            "Memory & core clock offsets: reset to 0.",
            "Core voltage boost: reset to 0%.",
        };
    }

    /// <summary>The stable phrase every rejected-transitional-read error carries. The e2e suite keys its
    /// retry-skip on it, so a reworded message must keep this fragment.</summary>
    internal const string TransientReadMarker = "didn't read back cleanly";

    /// <summary>The diagnosis shared by the write refusal below and status's read-only warning, so the
    /// two reports of the same condition can't drift apart.</summary>
    internal const string UnrecognizedCurvePhrase = "the V/F curve didn't read as a recognized NVIDIA table";

    /// <summary>Where an offsets-don't-fit error sends the user, shared by both errors that diagnose it.</summary>
    private const string PortingDocPointer = "See DEVELOPMENT.md (Porting the write offsets to a new card).";

    /// <summary>The refusal every write path uses when <see cref="CurveVoltsPlausible"/> rejects the
    /// curve: the tuning-buffer byte offsets are hardware-specific, so writing to an unrecognized card
    /// would land in unknown fields. Carries the detected-layout report, so the refusal itself is the
    /// bug report to file.</summary>
    public static CliError UnrecognizedCurveError(IntPtr gpu, string action) => new(
        $"Refusing to {action}: {UnrecognizedCurvePhrase} on this GPU, so the tuning-buffer "
        + $"offsets likely don't match this hardware. {PortingDocPointer}\n"
        + string.Join('\n', DetectedLayoutReport(gpu)));

    // --- Undervolt / overclock ---

    /// <summary>The factory base memory clock (MHz) — the reference a memory offset applies to. It is a
    /// static factory value, so unlike the core V/F curve it is readable regardless of power state.</summary>
    public static int BaseMemoryClockMhz(IntPtr gpu)
    {
        uint khz = NvApi.GetClockFrequencyKhz(gpu, NvApi.CLOCK_FREQ_TYPE_BASE, NvApi.CLOCK_DOMAIN_MEMORY);
        if (khz == 0)
        {
            throw new CliError("The base memory clock is unavailable.");
        }

        return (int)(khz / 1000);
    }

    /// <summary>The stock curve a real run plans from: resets all tuning first (each real run starts
    /// from stock anyway) and reads the curve directly, so the plan never depends on the
    /// live-minus-deltas recovery that an applied tuning distorts. <see cref="Clear"/>'s own guard
    /// refuses an unrecognized card before anything is written.</summary>
    public static IReadOnlyList<(int Mv, int Mhz)> ResetAndReadStock(IntPtr gpu)
    {
        Clear(gpu);
        return ReadUntilFreqsReadable(gpu);
    }

    /// <summary>The stock curve a dry run plans from, recovered read-only (live minus applied deltas).
    /// On a tuned card the driver re-shapes the effective curve it reports (bin snapping, thermal
    /// shift, smoothing around the flatten's cliff), so the recovery can stay distorted for as long as
    /// the tuning is active — that is diagnosed as such rather than polled as if it were a brief
    /// transition. An untuned card's unreadable read is the usual transition and is ridden out.</summary>
    public static IReadOnlyList<(int Mv, int Mhz)> RecoverStockReadOnly(IntPtr gpu)
    {
        IReadOnlyList<(int Mv, int Mhz)> curve = NvApi.GetVfCurve(gpu);
        if (!CurveVoltsPlausible(curve))
        {
            throw UnrecognizedCurveError(gpu, "compute the plan");
        }

        int[] deltas = NvApi.GetCurveFreqDeltasKhz(gpu, curve.Count);
        IReadOnlyList<(int Mv, int Mhz)> stock = SubtractDeltas(curve, deltas);
        if (CurveFreqsReadable(stock))
        {
            return stock;
        }

        if (deltas.Any(d => d != 0))
        {
            throw new CliError($"The V/F curve {TransientReadMarker}: a curve tuning is applied and "
                + "the driver re-shapes the effective curve around it, which can distort the "
                + "recovered stock curve for as long as the tuning is active. Run 'clear' first, or "
                + "drop --dry-run (a real run resets to stock before planning).");
        }

        return ReadUntilFreqsReadable(gpu);
    }

    /// <summary>Reads the V/F curve, re-reading for up to 3 s while the frequency column is collapsed.
    /// The status curve is the *live* curve: around a power-state change its frequency column can
    /// briefly read back collapsed (dips or garbage), which would corrupt any frequency-dependent
    /// computation. At a steady state - deep idle included - it reads cleanly (on Blackwell only the
    /// lowest anchors pin at a floor clock, still monotonic), so polling rides out a transition. If the
    /// read never comes clean, the last read is returned and the caller judges it
    /// (<see cref="BuildCurvePlan"/> rejects it; <see cref="ConfirmWrite"/> reports no verdict).</summary>
    private static IReadOnlyList<(int Mv, int Mhz)> ReadUntilFreqsReadable(IntPtr gpu)
    {
        const int pollIntervalMs = 50;
        const int maxWaitMs = 3000;
        IReadOnlyList<(int Mv, int Mhz)> curve = NvApi.GetVfCurve(gpu);
        for (int waited = 0; waited < maxWaitMs && !CurveFreqsReadable(curve); waited += pollIntervalMs)
        {
            Thread.Sleep(pollIntervalMs);
            curve = NvApi.GetVfCurve(gpu);
        }

        return curve;
    }

    private static IReadOnlyList<(int Mv, int Mhz)> SubtractDeltas(
        IReadOnlyList<(int Mv, int Mhz)> curve, int[] deltas)
    {
        var stock = new List<(int Mv, int Mhz)>(curve.Count);
        for (int i = 0; i < curve.Count; i++)
        {
            // Round the kHz delta, don't truncate: our own writes are whole MHz, but a foreign
            // (Afterburner) delta needn't be, and truncation would skew the recovered stock point.
            stock.Add((curve[i].Mv, curve[i].Mhz - (int)Math.Round(deltas[i] / 1000.0)));
        }

        return stock;
    }

    /// <summary>
    /// Writes the undervolt onto the freshly reset card (the caller planned from
    /// <see cref="ResetAndReadStock"/>, whose reset this relies on) in the order the driver requires:
    /// the memory offset first (<see cref="NvApi.SetPstate0Offsets"/> re-derives the perf table and
    /// wipes curve deltas), then the curve flatten that caps the voltage. A flat top makes the boost
    /// algorithm hold the voltage at the cap; the band built into <paramref name="plan"/> cushions a
    /// voltage undershoot. Ends by reading the effective curve back and verifying the write actually
    /// landed (see <see cref="ConfirmWrite"/>), reverting to stock and throwing if it didn't. Any step
    /// the driver rejects throws too — so the caller persists and reports "done" only on a real,
    /// verified undervolt.
    /// </summary>
    public static IReadOnlyList<string> Apply(IntPtr gpu, CurvePlan plan, int? targetMhz, int? memoryDeltaKhz)
    {
        if (memoryDeltaKhz is { } delta)
        {
            NvApi.SetPstate0Offsets(gpu, graphicsDeltaKhz: 0, memoryDeltaKhz: delta, coreVoltageDeltaUv: 0);
        }

        try
        {
            NvApi.SetCurveFreqDeltasKhz(gpu, plan.DeltasKhz);
        }
        catch
        {
            // Don't leave a partial apply behind: the memory offset is already live, and without the
            // cap it isn't the tuning that was asked for. The revert is best-effort - the write's own
            // error is the one to surface.
            try
            {
                Clear(gpu);
            }
            catch (Exception)
            {
            }

            throw;
        }

        return ConfirmWrite(gpu, plan, targetMhz);
    }

    /// <summary>Writes a memory-only tuning: resets everything to stock first (a real run sets the
    /// whole tuning state, exactly like the curve path's reset), then writes the memory offset. The
    /// curve is never read beyond <see cref="Clear"/>'s own guard, so this works at any power state.</summary>
    public static IReadOnlyList<string> ApplyMemoryOnly(IntPtr gpu, int memoryDeltaKhz)
    {
        var log = new List<string>(Clear(gpu));
        NvApi.SetPstate0Offsets(gpu, graphicsDeltaKhz: 0, memoryDeltaKhz: memoryDeltaKhz, coreVoltageDeltaUv: 0);
        log.Add("Memory clock offset written.");
        return log;
    }

    /// <summary>Describes the change <see cref="Apply"/> would make, for <c>--dry-run</c> — writes nothing.</summary>
    public static IReadOnlyList<string> DescribePlan(CurvePlan plan, int? targetMhz)
    {
        string action = $"cap at {plan.CapMv} mV / {plan.CapMhz} MHz" + (targetMhz is null ? " (stock)" : string.Empty);
        var log = new List<string> { $"[dry run] Would {action}; {plan.Changes.Count} point(s) change:" };
        foreach (CurveChange c in plan.Changes)
        {
            log.Add($"[dry run]   {c.Mv,4} mV: {c.OldMhz} -> {c.NewMhz} MHz "
                    + $"(delta {c.NewDeltaKhz / 1000.0:+0;-0} MHz)");
        }

        if (plan.Changes.Count == 0)
        {
            log.Add("[dry run]   curve already matches; nothing to write.");
        }

        return log;
    }

    /// <summary>A single per-anchor curve change, for reporting.</summary>
    internal readonly record struct CurveChange(int Mv, int OldMhz, int NewMhz, int NewDeltaKhz);

    /// <summary>The computed curve write: the per-point frequency deltas (kHz, index-aligned with the
    /// curve), the cap anchor and its flat frequency, and a description of every point that moves.</summary>
    internal sealed record CurvePlan(int CapMv, int CapMhz, IReadOnlyList<CurveChange> Changes,
        int[] DeltasKhz);

    /// <summary>
    /// Builds the curve write that caps voltage at <paramref name="capMv"/>, with every delta measured
    /// from the <paramref name="stock"/> curve. The cap anchor and every point above it are flattened to
    /// the cap frequency F (<paramref name="targetMhz"/> if given, else the stock clock at the cap); a
    /// flat top makes the boost algorithm hold the voltage there. A band of <paramref name="capPoints"/>
    /// anchors counting down from the cap (the cap itself plus the points below it) carries the cap's own
    /// frequency offset, so when the boost settles a bin or two below the cap under load the clock doesn't
    /// fall off a steep (overclocked) curve back to stock. Everything below the band stays at stock.
    /// Finally the curve is made non-decreasing (which the driver requires).
    /// </summary>
    internal static CurvePlan BuildCurvePlan(IReadOnlyList<(int Mv, int Mhz)> stock,
        int capMv, int? targetMhz, int capPoints)
    {
        int n = stock.Count;

        // Flattening reads the curve's per-anchor frequencies (the flat top's deltas are measured from
        // them), so every cap needs a clean read, not just one that also sets a clock - a cap computed
        // from a collapsed (transitional) read would write wrong deltas.
        if (!CurveFreqsReadable(stock))
        {
            throw new CliError($"The V/F curve {TransientReadMarker} (usually a brief power-state "
                + "transition) - retry in a moment, or retry under a 3D load.");
        }

        int k = NearestAnchorIndex(stock, capMv);

        // Flatten the cap anchor and above to F; the band of `capPoints` anchors ending at the cap
        // carries the cap's offset (the summary above explains both).
        int f = targetMhz ?? stock[k].Mhz;
        int capDeltaMhz = f - stock[k].Mhz;
        int bandStart = Math.Max(0, k - (capPoints - 1));
        var newMhz = new int[n];
        for (int i = 0; i < n; i++)
        {
            if (i >= k)
            {
                newMhz[i] = f;
            }
            else if (i >= bandStart)
            {
                newMhz[i] = stock[i].Mhz + capDeltaMhz;
            }
            else
            {
                newMhz[i] = stock[i].Mhz;
            }
        }

        // Keep the curve non-decreasing (the driver requires it): clamp each point below the cap down
        // to its right neighbour - this only bites when the cap's clock lands below stock. The points
        // at and above the cap are already the constant f, so the whole curve is non-decreasing after
        // this pass.
        for (int i = k - 1; i >= 0; i--)
        {
            newMhz[i] = Math.Min(newMhz[i], newMhz[i + 1]);
        }

        // Anchor 0 (the lowest voltage) has no control entry, so it stays at its stock clock; the writable
        // anchors must not fall below it or the effective curve would dip at anchor 0. This only bites when
        // the cap's clock lands below the lowest anchor's stock clock. Anchor 0 itself is floored too - not
        // to write it (its delta stays 0 below), but so newMhz[0], which becomes the reported cap when the
        // cap sits at anchor 0, reflects the clock the unwritable anchor actually keeps. (max with a
        // constant keeps the sequence non-decreasing.)
        for (int i = 0; i < n; i++)
        {
            newMhz[i] = Math.Max(newMhz[i], stock[0].Mhz);
        }

        // The write path skips anchor 0 (see NvApi.SetCurveFreqDeltasKhz), so leave its delta at 0 and out
        // of the change list - the plan, and --dry-run, then reflect exactly what a real apply produces.
        var deltasKhz = new int[n];
        var changes = new List<CurveChange>();
        for (int i = 1; i < n; i++)
        {
            int deltaKhz = (newMhz[i] - stock[i].Mhz) * 1000;
            deltasKhz[i] = deltaKhz;
            if (deltaKhz != 0)
            {
                changes.Add(new CurveChange(stock[i].Mv, stock[i].Mhz, newMhz[i], deltaKhz));
            }
        }

        return new CurvePlan(stock[k].Mv, newMhz[k], changes, deltasKhz);
    }

    private static int NearestAnchorIndex(IReadOnlyList<(int Mv, int Mhz)> curve, int mv)
    {
        int best = 0;
        for (int i = 1; i < curve.Count; i++)
        {
            if (Math.Abs(curve[i].Mv - mv) < Math.Abs(curve[best].Mv - mv))
            {
                best = i;
            }
        }

        return best;
    }

    // --- V/F curve reads: readability and plausibility checks, layout reporting, interpolation ---

    /// <summary>Whether the curve's frequency column is a clean, usable read. Around a power-state
    /// change the live status can briefly read back collapsed — sometimes wholesale (every clock tiny),
    /// sometimes dips in the steep idle->boost region. At a steady state, deep idle included, the read
    /// passes. A usable read is a full, monotonic, plausible curve that reaches a boost clock.</summary>
    public static bool CurveFreqsReadable(IReadOnlyList<(int Mv, int Mhz)> curve)
    {
        if (curve.Count < 16)
        {
            return false;
        }

        int max = 0;
        for (int i = 0; i < curve.Count; i++)
        {
            if (curve[i].Mhz < 100)
            {
                return false; // a collapsed/garbage point
            }

            if (i > 0 && curve[i].Mhz < curve[i - 1].Mhz - MaxBenignDipMhz)
            {
                return false; // not monotonic - a corrupt read
            }

            max = Math.Max(max, curve[i].Mhz);
        }

        return max >= NvApi.MinBoostClockKhz / 1000; // reaches a real boost clock
    }

    /// <summary>The adjacent-anchor dip a clean read may legitimately contain: the driver re-shapes
    /// the curve it reports (bin snapping, thermal shift, smoothing around an applied flatten), which
    /// can wobble neighbouring anchors by a few 7-8 MHz bins — most visibly in the stock curve
    /// recovered from a tuned card, where live-minus-delta seams at the flatten band's edges. A
    /// transitional collapse dips by hundreds of MHz, so it still fails.</summary>
    private const int MaxBenignDipMhz = 25;

    /// <summary>Whether the curve's <em>voltage</em> axis looks like a real NVIDIA V/F table: a long,
    /// ascending run of plausible core voltages. The voltage column is power-state
    /// independent (unlike the frequency column <see cref="CurveFreqsReadable"/> guards), so this
    /// stays true on a supported card even at idle, but reads false when the status-buffer offsets
    /// don't match the hardware and the bytes decode as garbage (a short or narrow list). It gates
    /// <em>writing</em>: the tuning-buffer offsets are hardware-specific, so if the curve we target
    /// isn't one we recognize, no tuning should be written at all.</summary>
    public static bool CurveVoltsPlausible(IReadOnlyList<(int Mv, int Mhz)> curve)
    {
        // A real table has ~127 anchors; a mismatched layout breaks out after a few points.
        if (curve.Count < 16)
        {
            return false;
        }

        // Ascending in voltage. The raw table ascends in microvolts, so adjacent anchors can truncate
        // to the same millivolt - equality is a real table, a decrease is a garbage read off an
        // unrecognized layout. (Checking it here also makes the span below well-defined rather than
        // assuming the caller pre-sorted.)
        for (int i = 1; i < curve.Count; i++)
        {
            if (curve[i].Mv < curve[i - 1].Mv)
            {
                return false;
            }
        }

        // ...and it spans up into real boost-voltage territory (tolerant of low-voltage mobile tables).
        int span = curve[^1].Mv - curve[0].Mv;
        return span >= 200 && curve[^1].Mv >= 850;
    }

    // The line triple every layout report prints — the warning attached to a failed plausibility check
    // and the 'layout' command — so the two renderings of the same facts can't drift apart.
    internal static string BuildLine => $"build: {Product.Name} {Product.Version}";
    internal static string DetectedLine(CurveLayout d) => $"detected: {d.DescribeColumns()}";
    internal static string CompiledLine => $"compiled: {CurveLayout.DescribeCompiled()}";

    /// <summary>When <see cref="CurveVoltsPlausible"/> rejects a read, re-detects the status buffer's
    /// actual layout and describes it next to the offsets this build compiled in — diagnostic detail to
    /// drop into a bug report when a card's tuning-buffer layout isn't the one we expect. Best-effort: any
    /// read failure is reported as a line rather than thrown.</summary>
    public static IReadOnlyList<string> DetectedLayoutReport(IntPtr gpu)
    {
        try
        {
            string detected = CurveLayout.TryDetect(NvApi.ReadVfCurveStatusRaw(gpu), out CurveLayout d)
                ? DetectedLine(d)
                : "detected: no V/F curve found in the status buffer";
            return new[] { BuildLine, detected, CompiledLine };
        }
        catch (Exception ex)
        {
            return new[] { BuildLine, $"detected: layout read failed ({ex.Message})" };
        }
    }

    /// <summary>The stock frequency (MHz) at a given voltage, linearly interpolated over the
    /// ascending, non-empty curve, clamping at the ends.</summary>
    public static double FreqAtVoltage(IReadOnlyList<(int Mv, int Mhz)> curve, double mv)
    {
        if (mv <= curve[0].Mv)
        {
            return curve[0].Mhz;
        }

        if (mv >= curve[^1].Mv)
        {
            return curve[^1].Mhz;
        }

        // The clamps leave curve[0].Mv < mv < curve[^1].Mv, so a bracketing pair exists.
        int i = 1;
        while (curve[i].Mv < mv)
        {
            i++;
        }

        var (mv0, mhz0) = curve[i - 1];
        var (mv1, mhz1) = curve[i];
        return mhz0 + (mv - mv0) / (mv1 - mv0) * (mhz1 - mhz0);
    }

    /// <summary>The V/F curve's bin granularity: clock read-backs land on ~15 MHz steps, so a comparison
    /// against an intended clock tolerates this much noise (and a change smaller than it isn't real).</summary>
    private const int CurveBinMhz = 15;

    /// <summary>
    /// Reads the effective curve back to (1) verify the write actually reached it and (2) report the
    /// realized cap. The control table's byte offsets are hardware-specific and — unlike the status read,
    /// which self-validates via <see cref="CurveVoltsPlausible"/> — have nothing to check themselves
    /// against, so on a card they don't fit the deltas land in reserved bytes the driver ignores and the
    /// curve comes back at stock. The effective (status) curve is independently validated, so
    /// cross-checking the write against it is the only genuine confirmation the control layout is right.
    /// A write that provably didn't land reverts to stock and throws rather than reporting a cap that
    /// isn't there; a read we simply couldn't take is reported but not treated as failure.
    /// </summary>
    private static IReadOnlyList<string> ConfirmWrite(IntPtr gpu, CurvePlan plan, int? targetMhz)
    {
        IReadOnlyList<(int Mv, int Mhz)> effective;
        try
        {
            // Poll out a power-state transition like the pre-write read does: a collapsed read-back
            // yields no verdict (see VerifyWriteReachedCurve), so riding out the transition keeps the
            // confirmation real. A curve legitimately flattened below the boost floor can never read
            // clean, so it takes one read straight to the no-verdict path instead of waiting out the
            // full window.
            effective = plan.CapMhz < NvApi.MinBoostClockKhz / 1000
                ? NvApi.GetVfCurve(gpu)
                : ReadUntilFreqsReadable(gpu);
        }
        catch (Exception ex)
        {
            // A failed read-back is a read glitch, not positive evidence the write missed (a layout
            // mismatch reads back fine — just unchanged — and is caught below), so don't revert on it.
            return new[] { $"Curve: unreadable, can't confirm the write ({ex.Message}); verify with 'status'." };
        }

        if (VerifyWriteReachedCurve(plan, effective) == WriteVerification.NotReflected)
        {
            // Undo the wrong write. Our zero-write hits the same reserved bytes the delta write did, and
            // the real delta field was never touched, so stock is restored either way. The mismatch is
            // the primary error - a revert that itself fails must not replace it.
            string reverted = "reverted to stock";
            try
            {
                Clear(gpu);
            }
            catch (Exception ex)
            {
                reverted = $"the revert to stock also failed ({ex.Message}), run 'clear'";
            }

            throw new CliError(
                "The curve didn't change after writing, so the control-table offsets don't match this GPU "
                + $"- {reverted}. {PortingDocPointer}");
        }

        return DescribeRealizedCap(effective, plan, targetMhz);
    }

    /// <summary>Whether the effective read-back reflects a curve write (see <see cref="VerifyWriteReachedCurve"/>).</summary>
    internal enum WriteVerification
    {
        /// <summary>The reduced anchors came down as intended — the control write reached the curve.</summary>
        Confirmed,

        /// <summary>Every anchor the plan reduces still reads at stock — the write didn't land where
        /// expected.</summary>
        NotReflected,

        /// <summary>Nothing reliable to check (no bin-sized reductions to observe, or the read-back
        /// didn't include the edited anchors) — the caller proceeds without a verdict.</summary>
        Unverifiable,
    }

    /// <summary>
    /// Cross-checks a curve write against the effective read-back. Every real voltage cap flattens the
    /// anchors above the cap <em>down</em> to the cap clock, and the driver always honors a clock
    /// reduction (a raise it may clamp to its own ceiling, a reduction it doesn't), so those anchors are
    /// the reliable signal: a majority that came down confirms the write reached the curve; every one
    /// still at stock means it didn't (the control-table offsets don't fit this GPU); a moved minority
    /// is read-back wobble, not evidence either way, and yields no verdict.
    /// Matching by voltage rather than by index also ties each reduction to the exact anchor it targeted.
    /// A plan with no bin-sized reductions (a pure overclock, or a cap so shallow every reduction is
    /// sub-bin) leaves nothing this can assert without fighting the driver's clamping, so it reports
    /// <see cref="WriteVerification.Unverifiable"/>.
    /// </summary>
    internal static WriteVerification VerifyWriteReachedCurve(
        CurvePlan plan, IReadOnlyList<(int Mv, int Mhz)> effective)
    {
        // A collapsed (transitional) read-back can't be judged either way: every clock reads far below
        // stock, so each reduction would count as "moved" and a missed write would pass as landed. No
        // verdict is the honest answer. (This also skips verification for a cap flattened below the
        // boost-clock floor CurveFreqsReadable requires - a conservative trade for such extreme caps.)
        if (!CurveFreqsReadable(effective))
        {
            return WriteVerification.Unverifiable;
        }

        // Judge only reductions of at least one bin: a smaller planned reduction (near the top of a
        // real curve the anchors sit only 7-8 MHz apart) can never register as moved against the
        // bin-sized read-back noise, so counting it could only produce a false NotReflected - and a
        // spurious revert.
        var reductions = plan.Changes.Where(c => c.NewMhz <= c.OldMhz - CurveBinMhz).ToList();
        if (reductions.Count == 0)
        {
            return WriteVerification.Unverifiable;
        }

        var effectiveByMv = new Dictionary<int, int>();
        foreach (var (mv, mhz) in effective)
        {
            // Adjacent anchors can truncate to the same millivolt; keep the first. A change that
            // targeted the second of such a pair is then judged against its same-mV neighbour - close
            // enough, since the plan treats adjacent anchors near-identically and the verdict is a
            // majority vote over bin-sized reductions.
            effectiveByMv.TryAdd(mv, mhz);
        }

        int checkable = 0, moved = 0;
        foreach (CurveChange c in reductions)
        {
            if (!effectiveByMv.TryGetValue(c.Mv, out int realizedMhz))
            {
                continue; // this anchor isn't in the read-back; can't judge it
            }

            checkable++;
            if (realizedMhz <= c.OldMhz - CurveBinMhz)
            {
                moved++;
            }
        }

        if (checkable == 0)
        {
            return WriteVerification.Unverifiable;
        }

        // A true layout mismatch leaves EVERY anchor at stock (the deltas landed in reserved bytes the
        // driver ignores), so only a read-back where nothing moved convicts the write. A majority moved
        // confirms it; the mixed band in between is read-back wobble (thermal shift, bin snapping
        // between the pre-write read and this one) - no verdict beats a spurious revert of a good
        // undervolt on the strength of noise.
        if (moved == 0)
        {
            return WriteVerification.NotReflected;
        }

        return moved * 2 >= checkable ? WriteVerification.Confirmed : WriteVerification.Unverifiable;
    }

    /// <summary>The curve's effective cap point: the flat-top clock and the lowest voltage that reaches
    /// it (within one bin — where the boost pins under load). Null when the frequency column isn't a
    /// clean read, since the numbers would be meaningless. Reported after an apply
    /// (<see cref="DescribeRealizedCap"/>) and on the <c>status</c> line (<see cref="TuningSnapshot"/>).</summary>
    internal static (int Mv, int Mhz)? EffectiveCapPoint(IReadOnlyList<(int Mv, int Mhz)> effective)
    {
        if (!CurveFreqsReadable(effective))
        {
            return null;
        }

        int maxMhz = effective.Max(c => c.Mhz);
        return (effective.First(c => c.Mhz >= maxMhz - CurveBinMhz).Mv, maxMhz);
    }

    /// <summary>Reports the realized cap from the effective read-back (see
    /// <see cref="EffectiveCapPoint"/>). Flags an explicit frequency target that wasn't reached —
    /// measured against the plan's own cap clock, so a target the plan itself floored (a clock below
    /// the lowest anchor's stock clock) is reported as floored, not blamed on the driver.</summary>
    private static IReadOnlyList<string> DescribeRealizedCap(
        IReadOnlyList<(int Mv, int Mhz)> effective, CurvePlan plan, int? targetMhz)
    {
        // The write itself is already verified (see ConfirmWrite); this only reports the realized clock,
        // which lives in the live frequency column. If that column reads collapsed (usually a power-state
        // change mid-apply), the numbers would be meaningless and an explicit target would look "not
        // reached", so skip the readout rather than print a misleading cap.
        if (EffectiveCapPoint(effective) is not { } cap)
        {
            return new[] { "Curve: the clock read-back wasn't clean (usually a power-state change "
                           + "mid-apply)." };
        }

        string line = $"Confirming curve point: {cap.Mv} mV / {cap.Mhz} MHz";
        if (targetMhz is { } f)
        {
            if (Math.Abs(plan.CapMhz - f) > CurveBinMhz)
            {
                line += $" - the requested {f} MHz was floored to the curve's minimum clock";
            }
            else if (Math.Abs(cap.Mhz - plan.CapMhz) > CurveBinMhz)
            {
                line += $" - target {f} not reached (driver smoothed the flatten)";
            }
        }

        return new[] { line };
    }

    /// <summary>The per-anchor frequency deltas currently applied, index-aligned with the curve.</summary>
    internal static int[] CurveDeltasKhz(IntPtr gpu)
        => NvApi.GetCurveFreqDeltasKhz(gpu, NvApi.GetVfCurve(gpu).Count);

    // --- Pstates parsing ---

    /// <summary>The populated clock and base-voltage entries of the P0 pstate — the 3D performance
    /// state the tuning lives in — with the driver's counts clamped to the struct bounds. Both lists
    /// are empty when the info holds no P0. The one place a P0 field lookup walks the pstates.</summary>
    internal static (IReadOnlyList<Pstate20ClockEntry> Clocks, IReadOnlyList<Pstate20BaseVoltageEntry> BaseVoltages)
        P0Entries(Pstates20InfoV1 info)
    {
        int numPstates = (int)Math.Min(info.NumPstates, (uint)Pstates20InfoV1.MaxPstates);
        for (int p = 0; p < numPstates; p++)
        {
            if (info.Pstates[p].PstateId == 0)
            {
                return (
                    info.Pstates[p].Clocks
                        .Take((int)Math.Min(info.NumClocks, (uint)Pstate20.MaxClocks)).ToArray(),
                    info.Pstates[p].BaseVoltages
                        .Take((int)Math.Min(info.NumBaseVoltages, (uint)Pstate20.MaxBaseVoltages)).ToArray());
            }
        }

        return (Array.Empty<Pstate20ClockEntry>(), Array.Empty<Pstate20BaseVoltageEntry>());
    }
}
