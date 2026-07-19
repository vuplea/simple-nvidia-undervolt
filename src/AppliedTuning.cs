namespace SimpleNvidiaUndervolt;

/// <summary>The raw applied tuning state — every knob <see cref="GpuTuning.Clear"/> resets: the
/// per-anchor curve deltas, the P0 clock/voltage offsets and the voltage boost. Snapshotted at the
/// delta level so a <c>save-reference</c> capture can reset the card to stock and put the exact
/// state back (see <see cref="GpuTuning.CaptureStockForReference"/>), round-tripping even a foreign
/// (Afterburner) tuning.</summary>
internal sealed record AppliedTuning(int[] CurveDeltasKhz, int GraphicsDeltaKhz, int MemoryDeltaKhz,
    int CoreVoltageDeltaUv, uint VoltageBoostPercent)
{
    public bool IsStock
        => CurveDeltasKhz.All(d => d == 0) && GraphicsDeltaKhz == 0 && MemoryDeltaKhz == 0
           && CoreVoltageDeltaUv == 0 && VoltageBoostPercent == 0;

    public static AppliedTuning Read(IntPtr gpu)
    {
        var (clocks, baseVoltages) = GpuTuning.P0Entries(NvApi.GetPstates20(gpu));
        int coreVoltageDeltaUv = 0;
        foreach (Pstate20BaseVoltageEntry entry in baseVoltages)
        {
            if (entry.DomainId == NvApi.VOLTAGE_DOMAIN_CORE)
            {
                coreVoltageDeltaUv = entry.ValueDeltaUv.Value;
                break;
            }
        }

        return new(
            NvApi.GetCurveFreqDeltasKhz(gpu, NvApi.MaxCurveAnchors),
            GraphicsDelta(clocks),
            MemoryDelta(gpu, clocks),
            coreVoltageDeltaUv,
            NvApi.GetCoreVoltageBoostPercent(gpu));
    }

    /// <summary>Writes the snapshot back, in the order the driver requires (see
    /// <see cref="GpuTuning.Apply"/>): the pstate offsets first — that write re-derives the perf
    /// table and wipes curve deltas — then the curve deltas, then the boost.</summary>
    public void Restore(IntPtr gpu)
    {
        NvApi.SetPstate0Offsets(gpu, GraphicsDeltaKhz, MemoryDeltaKhz, CoreVoltageDeltaUv);
        NvApi.SetCurveFreqDeltasKhz(gpu, CurveDeltasKhz);
        NvApi.SetCoreVoltageBoostPercent(gpu, VoltageBoostPercent);
    }

    private static int GraphicsDelta(IReadOnlyList<Pstate20ClockEntry> clocks)
    {
        foreach (Pstate20ClockEntry entry in clocks)
        {
            if (entry.DomainId == NvApi.CLOCK_DOMAIN_GRAPHICS)
            {
                return entry.FreqDeltaKhz.Value;
            }
        }

        return 0;
    }

    /// <summary>The applied memory offset, via the read/write asymmetry documented on
    /// <see cref="NvApi.SetPstate0Offsets"/>: the GET reports the memory clock as an absolute
    /// frequency with the offset folded in, so the delta to write back is measured against the
    /// factory base clock. The entry's own delta field is the fallback when the base is
    /// unavailable.</summary>
    private static int MemoryDelta(IntPtr gpu, IReadOnlyList<Pstate20ClockEntry> clocks)
    {
        foreach (Pstate20ClockEntry entry in clocks)
        {
            if (entry.DomainId == NvApi.CLOCK_DOMAIN_MEMORY)
            {
                uint baseKhz = NvApi.GetClockFrequencyKhz(
                    gpu, NvApi.CLOCK_FREQ_TYPE_BASE, NvApi.CLOCK_DOMAIN_MEMORY);
                return entry.Data0 > 0 && baseKhz > 0
                    ? (int)entry.Data0 - (int)baseKhz
                    : entry.FreqDeltaKhz.Value;
            }
        }

        return 0;
    }
}
