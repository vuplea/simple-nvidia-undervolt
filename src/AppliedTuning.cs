namespace SimpleNvidiaUndervolt;

/// <summary>The applied tuning state, at the level of the knobs this tool sets: the per-anchor
/// curve deltas and the P0 memory offset. Snapshotted at the delta level so a
/// <c>set-reference-curve</c> capture can reset the card to stock and put the exact state back
/// (see <see cref="GpuTuning.CaptureStockForReference"/>). Knobs only a foreign tool sets — a
/// voltage boost, a P0 clock or voltage offset — are outside it: a capture's reset clears them to
/// stock and the restore leaves them there.</summary>
internal sealed record AppliedTuning(int[] CurveDeltasKhz, int MemoryDeltaKhz, int CoreDeltaKhz = 0)
{
    public bool IsStock => CurveDeltasKhz.All(d => d == 0) && MemoryDeltaKhz == 0 && CoreDeltaKhz == 0;

    public static AppliedTuning Read(IntPtr gpu)
        => new(NvApi.GetCurveFreqDeltasKhz(gpu, NvApi.MaxCurveAnchors),
            MemoryDelta(gpu, NvApi.GetPstates20(gpu)),
            GetCoreDeltaKhz(gpu, NvApi.GetPstates20(gpu)));

    /// <summary>Writes the snapshot back, in the order the driver requires (see
    /// <see cref="GpuTuning.Apply"/>): the pstate offsets first — that write re-derives the perf
    /// table and wipes curve deltas — then the curve deltas.</summary>
    public void Restore(IntPtr gpu)
    {
        NvApi.SetPstate0Offsets(gpu, graphicsDeltaKhz: CoreDeltaKhz * 1000, MemoryDeltaKhz, coreVoltageDeltaUv: 0);
        NvApi.SetCurveFreqDeltasKhz(gpu, CurveDeltasKhz);
    }

    /// <summary>The applied core clock offset (MHz), read from the P0 graphics clock delta in the
    /// pstates20 info — the same knob <see cref="NvApi.SetPstate0Offsets"/> writes for
    /// <c>--core-offset</c>. The GET reports the graphics clock as an absolute frequency with the
    /// offset folded in, so the delta is measured against the factory base clock. The entry's
    /// own delta field is the fallback when the base is unavailable.</summary>
    private static int GetCoreDeltaKhz(IntPtr gpu, Pstates20InfoV1 info)
    {
        if (GpuTuning.P0Clock(info, NvApi.CLOCK_DOMAIN_GRAPHICS) is not { } entry)
        {
            return 0;
        }

        uint baseKhz = NvApi.GetClockFrequencyKhz(
            gpu, NvApi.CLOCK_FREQ_TYPE_BASE, NvApi.CLOCK_DOMAIN_GRAPHICS);
        return entry.Data0 > 0 && baseKhz > 0
            ? (int)entry.Data0 - (int)baseKhz
            : (int)entry.FreqDeltaKhz.Value;
    }

    /// <summary>The applied memory offset, via the read/write asymmetry documented on
    /// <see cref=\"NvApi.SetPstate0Offsets\"/>: the GET reports the memory clock as an absolute
    /// frequency with the offset folded in, so the delta to write back is measured against the
    /// factory base clock. The entry's own delta field is the fallback when the base is
    /// unavailable.</summary>
    private static int MemoryDelta(IntPtr gpu, Pstates20InfoV1 info)
    {
        if (GpuTuning.P0Clock(info, NvApi.CLOCK_DOMAIN_MEMORY) is not { } entry)
        {
            return 0;
        }

        uint baseKhz = NvApi.GetClockFrequencyKhz(
            gpu, NvApi.CLOCK_FREQ_TYPE_BASE, NvApi.CLOCK_DOMAIN_MEMORY);
        return entry.Data0 > 0 && baseKhz > 0
            ? (int)entry.Data0 - (int)baseKhz
            : entry.FreqDeltaKhz.Value;
    }
}
