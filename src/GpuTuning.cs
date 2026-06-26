namespace SimpleNvidiaUndervolt;

/// <summary>
/// High-level read and reset of the GPU tuning that MSI Afterburner programs into the driver.
///
/// On Ada/Blackwell the tuning lives in several places: the core V/F curve offset in the
/// ClkVfPoints control table, the memory offset baked into the absolute P0 memory clock in
/// pstates 2.0, and the core voltage boost percentage. Each read is surfaced as a
/// <see cref="Reading{T}"/> so a failure shows up explicitly instead of masquerading as "stock".
/// </summary>
internal sealed class GpuTuning
{
    public required string Name { get; init; }
    public Reading<int[]> CoreCurveOffsetsKhz { get; init; }
    public Reading<int> MemoryClockKhz { get; init; }
    public Reading<int> BaseMemoryClockKhz { get; init; }
    public Reading<uint> CoreVoltageBoostPercent { get; init; }

    public static GpuTuning Read(IntPtr gpu) => new()
    {
        Name = NvApi.SafeFullName(gpu),
        CoreCurveOffsetsKhz = Reading.Try(() => NonZeroCoreOffsets(gpu)),
        MemoryClockKhz = Reading.Try(() => ReadMemoryClockKhz(NvApi.GetPstates20(gpu))),
        BaseMemoryClockKhz = Reading.Try(() => (int)NvApi.GetClockFrequencyKhz(gpu, NvApi.CLOCK_FREQ_TYPE_BASE, NvApi.CLOCK_DOMAIN_MEMORY)),
        CoreVoltageBoostPercent = Reading.Try(() => NvApi.GetCoreVoltageBoostPercent(gpu)),
    };

    /// <summary>Resets tuning to stock, returning a human-readable line per action. A step the driver
    /// rejects (e.g. when not elevated) throws rather than being logged as "skipped", so a partial reset
    /// surfaces as a failure the caller can report instead of masquerading as success.</summary>
    public static IReadOnlyList<string> Clear(IntPtr gpu)
    {
        int cleared = ResetCoreCurve(gpu);
        NvApi.SetPstate0Offsets(gpu, graphicsDeltaKhz: 0, memoryDeltaKhz: 0, coreVoltageDeltaUv: 0);
        NvApi.SetCoreVoltageBoostPercent(gpu, 0);

        return new[]
        {
            cleared > 0 ? $"Core V/F curve: cleared {cleared} offset point(s)." : "Core V/F curve: already stock.",
            "Memory & core clock offsets: reset to 0.",
            "Core voltage boost: reset to 0%.",
        };
    }

    // --- Undervolt / overclock ---

    /// <summary>The factory base memory clock (MHz) — the reference a memory offset applies to. It is a
    /// static factory value, so unlike the core V/F curve it is readable regardless of power state.</summary>
    public static int BaseMemoryClockMhz(IntPtr gpu)
    {
        uint khz = NvApi.GetClockFrequencyKhz(gpu, NvApi.CLOCK_FREQ_TYPE_BASE, NvApi.CLOCK_DOMAIN_MEMORY);
        if (khz == 0)
        {
            throw new NvApiException("the base memory clock is unavailable.");
        }

        return (int)(khz / 1000);
    }

    /// <summary>Sets the P0 memory clock as a kHz offset from the factory base. The driver tracks memory
    /// as an offset (the GET reports the offset-applied absolute), so this writes the delta — writing 0
    /// returns the clock to stock, which is what <see cref="Clear"/> does.</summary>
    public static void SetMemoryClockOffsetKhz(IntPtr gpu, int deltaKhz)
        => NvApi.SetPstate0Offsets(gpu, graphicsDeltaKhz: 0, memoryDeltaKhz: deltaKhz, coreVoltageDeltaUv: 0);

    /// <summary>The stock V/F curve as ordered (mV, MHz) points: the effective curve with any applied
    /// per-point delta removed, so it is the factory baseline regardless of what is currently set.
    /// (Callers reset to stock before applying, so in practice the deltas are already zero here.) The
    /// peak operating point captured under load lies on this curve, so one of its coordinates can be
    /// recovered from the other.</summary>
    public static IReadOnlyList<(int Mv, int Mhz)> StockCurve(IntPtr gpu)
    {
        // The status curve is the *live* curve and reads back collapsed outside a 3D power state (garbage
        // or dips in the steep idle->boost region), which would corrupt any frequency-dependent
        // computation. Poll for up to 3 s for a clean, monotonic read - long enough to ride out a
        // power-state transition, or to let the user start a 3D workload just after launching. If it
        // stays unreadable, BuildCurvePlan rejects it.
        const int pollIntervalMs = 50;
        const int maxWaitMs = 3000;
        IReadOnlyList<(int Mv, int Mhz)> stock = ReadStockOnce(gpu);
        for (int waited = 0; waited < maxWaitMs && !CurveFreqsReadable(stock); waited += pollIntervalMs)
        {
            System.Threading.Thread.Sleep(pollIntervalMs);
            stock = ReadStockOnce(gpu);
        }

        return stock;
    }

    private static IReadOnlyList<(int Mv, int Mhz)> ReadStockOnce(IntPtr gpu)
    {
        IReadOnlyList<(int Mv, int Mhz)> curve = NvApi.GetVfCurve(gpu);
        int[] deltas = NvApi.GetCurveFreqDeltasKhz(gpu, curve.Count);
        var stock = new List<(int Mv, int Mhz)>(curve.Count);
        for (int i = 0; i < curve.Count; i++)
        {
            stock.Add((curve[i].Mv, curve[i].Mhz - deltas[i] / 1000));
        }

        return stock;
    }

    /// <summary>
    /// Writes the undervolt in the order the driver requires: reset to stock, then the memory offset
    /// (<see cref="NvApi.SetPstate0Offsets"/> re-derives the perf table and wipes curve deltas), then the
    /// curve flatten that caps the voltage. A flat top makes the boost algorithm hold the voltage at the
    /// cap; the band built into <paramref name="plan"/> cushions a voltage undershoot. Ends by reading the
    /// effective curve back to confirm. Any step the driver rejects throws — so the caller persists and
    /// reports "done" only on a real, applied undervolt. The caller builds <paramref name="plan"/> first
    /// (which requires a readable curve), so an idle or unrecognized curve fails before anything is written.
    /// </summary>
    public static IReadOnlyList<string> Apply(IntPtr gpu, CurvePlan plan, int? targetMhz, int? memoryDeltaKhz)
    {
        Clear(gpu); // reset to a clean stock baseline; an apply doesn't surface the reset's own log

        if (memoryDeltaKhz is { } delta)
        {
            SetMemoryClockOffsetKhz(gpu, delta);
        }

        NvApi.SetCurveFreqDeltasKhz(gpu, plan.DeltasKhz);
        return Confirm(gpu, targetMhz);
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
        // The stock curve and the control table enumerate the same anchors in the same order, so the
        // per-point deltas align by index with the curve points.
        int n = stock.Count;
        if (n == 0)
        {
            throw new NvApiException("V/F curve is empty; cannot change it.");
        }

        var mv = new int[n];
        var stockMhz = new int[n];
        for (int i = 0; i < n; i++)
        {
            mv[i] = stock[i].Mv;
            stockMhz[i] = stock[i].Mhz;
        }

        // Flattening reads the curve's per-anchor frequencies (the flat top's deltas are measured from
        // them), and those only appear in a 3D power state. So every cap needs a readable curve, not just
        // one that also sets a clock - a cap computed from a collapsed idle read would write wrong deltas.
        if (!CurveFreqsReadable(stock))
        {
            throw new NvApiException("the GPU is idle, so the V/F curve isn't readable - put it under load "
                + "(e.g. leave 'watch' running on a 3D workload) and retry.");
        }

        int k = 0;
        for (int i = 1; i < n; i++)
        {
            if (Math.Abs(mv[i] - capMv) < Math.Abs(mv[k] - capMv))
            {
                k = i;
            }
        }

        // Flatten the cap anchor and above to F (a flat top caps the voltage). The band of `capPoints`
        // anchors ending at the cap also takes the cap's offset: under load the boost can settle a bin
        // or two below the cap, and on a steep (overclocked) curve that would drop the clock sharply, so
        // holding the offset across the band keeps the frequency up there instead of falling to stock.
        int f = targetMhz ?? stockMhz[k];
        int capDeltaMhz = f - stockMhz[k];
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
                newMhz[i] = stockMhz[i] + capDeltaMhz;
            }
            else
            {
                newMhz[i] = stockMhz[i];
            }
        }

        // Keep the curve non-decreasing (the driver requires it): clamp each lower point down to its
        // right neighbour (handles a cap below stock), then a final upward sweep for safety.
        for (int i = k - 1; i >= 0; i--)
        {
            newMhz[i] = Math.Min(newMhz[i], newMhz[i + 1]);
        }

        for (int i = 1; i < n; i++)
        {
            if (newMhz[i] < newMhz[i - 1])
            {
                newMhz[i] = newMhz[i - 1];
            }
        }

        var deltasKhz = new int[n];
        var changes = new List<CurveChange>();
        for (int i = 0; i < n; i++)
        {
            int deltaKhz = (newMhz[i] - stockMhz[i]) * 1000;
            deltasKhz[i] = deltaKhz;
            if (deltaKhz != 0)
            {
                changes.Add(new CurveChange(mv[i], stockMhz[i], newMhz[i], deltaKhz));
            }
        }

        return new CurvePlan(mv[k], newMhz[k], changes, deltasKhz);
    }

    // --- V/F curve interpolation (shared with the CLI's reference-point resolution) ---

    /// <summary>Whether the curve's frequency column is a clean, usable read. In a transitional power
    /// state the live status collapses — sometimes wholesale (every clock tiny), sometimes only in the
    /// steep idle->boost region (dips/garbage there). A usable read is a full, monotonic, plausible
    /// curve that actually reaches a boost clock; anything else means the card isn't ready to read.</summary>
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

            if (i > 0 && curve[i].Mhz < curve[i - 1].Mhz - 5)
            {
                return false; // not monotonic - a corrupt read
            }

            max = Math.Max(max, curve[i].Mhz);
        }

        return max >= 1500; // the card is in a real power state
    }

    /// <summary>Whether the curve's <em>voltage</em> axis looks like a real NVIDIA V/F table: a long,
    /// strictly ascending run of plausible core voltages. The voltage column is power-state
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

        // Strictly ascending in voltage — each anchor a distinct, higher voltage. A garbage read off
        // an unrecognized layout won't hold this across the whole run. (Checking it here also makes
        // the span below well-defined rather than assuming the caller pre-sorted.)
        for (int i = 1; i < curve.Count; i++)
        {
            if (curve[i].Mv <= curve[i - 1].Mv)
            {
                return false;
            }
        }

        // ...and it spans up into real boost-voltage territory.
        int span = curve[^1].Mv - curve[0].Mv;
        return span >= 200 && curve[^1].Mv >= 900;
    }

    /// <summary>When <see cref="CurveVoltsPlausible"/> rejects a read, re-detects the status buffer's
    /// actual layout and describes it next to the offsets this build compiled in — diagnostic detail to
    /// drop into a bug report when a card's tuning-buffer layout isn't the one we expect. Best-effort: any
    /// read failure is reported as a line rather than thrown.</summary>
    public static IReadOnlyList<string> DetectedLayoutReport(IntPtr gpu)
    {
        try
        {
            return CurveLayout.TryDetect(NvApi.ReadVfCurveStatusRaw(gpu), out CurveLayout d)
                ? new[] { $"detected: {d.DescribeColumns(NvApi.StatusEntryBase)}", $"compiled: {CurveLayout.DescribeCompiled()}" }
                : new[] { "detected: no V/F curve found in the status buffer", $"compiled: {CurveLayout.DescribeCompiled()}" };
        }
        catch (NvApiException ex)
        {
            return new[] { $"detected: layout read failed ({ex.Message})" };
        }
    }

    /// <summary>The stock frequency (MHz) at a given voltage, linearly interpolated over the curve.</summary>
    public static double FreqAtVoltage(IReadOnlyList<(int Mv, int Mhz)> curve, double mv)
        => Interpolate(curve, mv, p => p.Mv, p => p.Mhz);

    /// <summary>The lowest stock voltage (mV) that reaches a given frequency, linearly interpolated.</summary>
    public static double VoltageAtFreq(IReadOnlyList<(int Mv, int Mhz)> curve, double mhz)
        => Interpolate(curve, mhz, p => p.Mhz, p => p.Mv);

    /// <summary>Linear interpolation over the ascending curve: finds <paramref name="x"/> on the
    /// <paramref name="key"/> axis and returns the matching <paramref name="value"/> axis, clamping at
    /// the ends.</summary>
    private static double Interpolate(IReadOnlyList<(int Mv, int Mhz)> curve, double x,
        Func<(int Mv, int Mhz), int> key, Func<(int Mv, int Mhz), int> value)
    {
        if (curve.Count == 0)
        {
            throw new NvApiException("V/F curve is empty; cannot interpolate.");
        }

        if (x <= key(curve[0]))
        {
            return value(curve[0]);
        }

        if (x >= key(curve[^1]))
        {
            return value(curve[^1]);
        }

        for (int i = 1; i < curve.Count; i++)
        {
            int x1 = key(curve[i]);
            if (x1 < x)
            {
                continue;
            }

            int x0 = key(curve[i - 1]);
            double t = x1 == x0 ? 0 : (x - x0) / (double)(x1 - x0);
            return value(curve[i - 1]) + t * (value(curve[i]) - value(curve[i - 1]));
        }

        return value(curve[^1]);
    }

    /// <summary>Reads the effective V/F curve back and reports the actual cap it produced: the flat top
    /// clock and the lowest voltage that reaches it (where the boost will pin). The effective curve (the
    /// status buffer) is the source of truth. Flags an explicit frequency target the driver smoothed
    /// short of.</summary>
    private static IReadOnlyList<string> Confirm(IntPtr gpu, int? targetMhz)
    {
        var lines = new List<string>();

        try
        {
            IReadOnlyList<(int Mv, int Mhz)> curve = NvApi.GetVfCurve(gpu);
            if (curve.Count == 0)
            {
                lines.Add("Curve: empty read-back.");
                return lines;
            }

            const int toleranceMhz = 20; // ignore the curve's bin granularity
            int maxMhz = curve.Max(c => c.Mhz);
            int capAtMv = curve.First(c => c.Mhz >= maxMhz - toleranceMhz).Mv; // lowest voltage at the flat top
            string line = $"Confirming curve point: {capAtMv} mV / {maxMhz} MHz";
            if (targetMhz is { } f && Math.Abs(maxMhz - f) > toleranceMhz)
            {
                line += $" - target {f} not reached (driver smoothed the flatten)";
            }

            lines.Add(line + "; verify under load with 'watch'.");
        }
        catch (NvApiException ex)
        {
            lines.Add($"Curve: unreadable ({ex.Message}).");
        }

        return lines;
    }

    // --- Reset operations ---

    /// <summary>Zeroes every per-point core-clock offset across the whole curve, resetting it to
    /// stock (equivalent to the driver's own reset).</summary>
    private static int ResetCoreCurve(IntPtr gpu)
    {
        int count = NvApi.GetVfCurve(gpu).Count;
        int cleared = NvApi.GetCurveFreqDeltasKhz(gpu, count).Count(d => d != 0);
        NvApi.SetCurveFreqDeltasKhz(gpu, new int[count]);
        return cleared;
    }

    // --- Parsing helpers ---

    private static int[] NonZeroCoreOffsets(IntPtr gpu)
    {
        int count = NvApi.GetVfCurve(gpu).Count;
        return NvApi.GetCurveFreqDeltasKhz(gpu, count).Where(d => d != 0).ToArray();
    }

    /// <summary>The absolute P0 memory clock (kHz). Afterburner applies the memory offset here as
    /// the range frequency rather than as a delta, so this value moves with it.</summary>
    private static int ReadMemoryClockKhz(Pstates20InfoV1 info)
    {
        int numPstates = (int)Math.Min(info.NumPstates, (uint)Pstates20InfoV1.MaxPstates);
        int numClocks = (int)Math.Min(info.NumClocks, (uint)Pstate20.MaxClocks);

        for (int p = 0; p < numPstates; p++)
        {
            if (info.Pstates[p].PstateId != 0)
            {
                continue;
            }

            for (int c = 0; c < numClocks; c++)
            {
                Pstate20ClockEntry entry = info.Pstates[p].Clocks[c];
                if (entry.DomainId == NvApi.CLOCK_DOMAIN_MEMORY)
                {
                    return (int)entry.Data0; // range-type clock: Data0 is the (min == max) frequency
                }
            }
        }

        return 0;
    }


    // --- Display ---

    public string DescribeCoreCurve()
    {
        if (!CoreCurveOffsetsKhz.Ok)
        {
            return CoreCurveOffsetsKhz.Error!;
        }

        int[] offsets = CoreCurveOffsetsKhz.Value!;
        if (offsets.Length == 0)
        {
            return "stock";
        }

        int minMhz = offsets.Min() / 1000;
        int maxMhz = offsets.Max() / 1000;
        return minMhz == maxMhz
            ? $"{minMhz:+0;-0} MHz on {offsets.Length} point(s)"
            : $"{minMhz:+0;-0}..{maxMhz:+0;-0} MHz across {offsets.Length} point(s)";
    }

    public string DescribeMemoryClock()
    {
        if (!MemoryClockKhz.Ok)
        {
            return MemoryClockKhz.Error!;
        }

        if (MemoryClockKhz.Value == 0)
        {
            return "unavailable";
        }

        int mhz = MemoryClockKhz.Value / 1000;
        if (BaseMemoryClockKhz is { Ok: true, Value: > 0 })
        {
            int baseMhz = BaseMemoryClockKhz.Value / 1000;
            int offset = mhz - baseMhz;
            return offset == 0 ? $"{mhz} MHz (stock)" : $"{mhz} MHz ({offset:+0;-0} from {baseMhz} stock)";
        }

        return $"{mhz} MHz (P0, includes any offset)";
    }

    public string DescribeVoltageBoost()
        => CoreVoltageBoostPercent.Ok ? $"{CoreVoltageBoostPercent.Value}%" : CoreVoltageBoostPercent.Error!;
}

/// <summary>Result of a single NVAPI read: either a value or the error that prevented it.</summary>
internal readonly struct Reading<T>
{
    public bool Ok { get; private init; }
    public T? Value { get; private init; }
    public string? Error { get; private init; }

    public static Reading<T> Success(T value) => new() { Ok = true, Value = value };
    public static Reading<T> Failure(string error) => new() { Ok = false, Error = error };
}

internal static class Reading
{
    public static Reading<T> Try<T>(Func<T> read)
    {
        try
        {
            return Reading<T>.Success(read());
        }
        catch (NvApiException ex)
        {
            return Reading<T>.Failure($"unavailable ({ex.Message})");
        }
    }
}
