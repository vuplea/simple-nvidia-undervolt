namespace SimpleNvidiaUndervolt;

/// <summary>
/// A read-back of the tuning currently applied to the GPU — the core curve offsets, the memory
/// clock and the voltage boost — and its human-readable rendering for the <c>status</c> command.
/// Each field is a <see cref="Reading{T}"/> so a failed read shows up explicitly instead of
/// masquerading as "stock".
/// </summary>
internal sealed class TuningSnapshot
{
    public required string Name { get; init; }
    public Reading<int[]> CoreCurveOffsetsKhz { get; init; }

    /// <summary>The effective curve's cap point — the flat-top clock and the lowest voltage that
    /// reaches it — or null when the frequency column didn't read cleanly. Rendered only when curve
    /// offsets are applied: at stock the curve's top point is not a cap.</summary>
    public (int Mv, int Mhz)? EffectiveCap { get; init; }

    public Reading<int> MemoryClockKhz { get; init; }
    public Reading<int> BaseMemoryClockKhz { get; init; }
    public Reading<uint> CoreVoltageBoostPercent { get; init; }

    public static TuningSnapshot Read(IntPtr gpu) => new()
    {
        Name = NvApi.SafeFullName(gpu),
        CoreCurveOffsetsKhz = Reading.Try(() => GpuTuning.CurveDeltasKhz(gpu).Where(d => d != 0).ToArray()),
        EffectiveCap = ReadCapPoint(gpu),
        MemoryClockKhz = Reading.Try(() => ReadMemoryClockKhz(NvApi.GetPstates20(gpu))),
        BaseMemoryClockKhz = Reading.Try(() => (int)NvApi.GetClockFrequencyKhz(gpu, NvApi.CLOCK_FREQ_TYPE_BASE, NvApi.CLOCK_DOMAIN_MEMORY)),
        CoreVoltageBoostPercent = Reading.Try(() => NvApi.GetCoreVoltageBoostPercent(gpu)),
    };

    /// <summary>Best-effort: the cap is a detail appended to the offsets line, so a failed or unclean
    /// read renders as no cap rather than as an error of its own.</summary>
    private static (int Mv, int Mhz)? ReadCapPoint(IntPtr gpu)
    {
        try
        {
            return GpuTuning.EffectiveCapPoint(NvApi.GetVfCurve(gpu));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The absolute P0 memory clock (kHz). Afterburner applies the memory offset here as
    /// the range frequency rather than as a delta, so this value moves with it.</summary>
    private static int ReadMemoryClockKhz(Pstates20InfoV1 info)
        // Range-type clock: Data0 is the (min == max) frequency.
        => (int)(GpuTuning.P0Clock(info, NvApi.CLOCK_DOMAIN_MEMORY)?.Data0 ?? 0);

    // --- Display ---

    public string DescribeCoreCurve()
    {
        if (CoreCurveOffsetsKhz is not { Ok: true, Value: { } offsets })
        {
            return CoreCurveOffsetsKhz.Error!;
        }

        if (offsets.Length == 0)
        {
            return "stock";
        }

        int minMhz = offsets.Min() / 1000;
        int maxMhz = offsets.Max() / 1000;
        string range = minMhz == maxMhz
            ? $"{minMhz:+0;-0} MHz on {offsets.Length} point(s)"
            : $"{minMhz:+0;-0}..{maxMhz:+0;-0} MHz across {offsets.Length} point(s)";
        return EffectiveCap is { } cap ? $"{range}, capped at {cap.Mv} mV / {cap.Mhz} MHz" : range;
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
        return BaseMemoryClockKhz is { Ok: true, Value: > 0 }
            ? DescribeMemoryClock(mhz, BaseMemoryClockKhz.Value / 1000)
            : $"{mhz} MHz (P0, includes any offset)";
    }

    /// <summary>The memory clock relative to the factory base — one rendering shared by the tune
    /// output and the status line, so the two can't drift apart.</summary>
    public static string DescribeMemoryClock(int mhz, int baseMhz)
        => mhz == baseMhz
            ? $"{mhz} MHz (stock)"
            : $"{mhz} MHz ({mhz - baseMhz:+0;-0} from {baseMhz} stock)";

    public string DescribeVoltageBoost()
        => CoreVoltageBoostPercent.Ok ? $"{CoreVoltageBoostPercent.Value}%" : CoreVoltageBoostPercent.Error!;
}
