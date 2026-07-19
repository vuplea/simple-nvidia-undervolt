namespace SimpleNvidiaUndervolt.E2E;

/// <summary>
/// Shared setup for the end-to-end tests, which drive the real NVIDIA driver. Created once for the whole
/// <see cref="GpuCollection"/> (so the tests never run concurrently against the one GPU). It decides
/// whether the suite can run at all — it needs an elevated host with an NVIDIA GPU — and, when it can,
/// snapshots the current tuning (core V/F curve deltas, the P0 graphics/memory clock and core-voltage
/// offsets, voltage boost) and restores it on dispose, so a run leaves the GPU as it found it. If a
/// restore step fails, that knob is left at the driver default rather than the original value.
/// </summary>
public sealed class GpuFixture : IDisposable
{
    public bool Available { get; }
    public string SkipReason { get; } = string.Empty;
    public IntPtr Gpu { get; }

    private readonly bool _initialized;
    private readonly int[]? _curveDeltasKhz;
    private readonly uint _voltageBoostPercent;
    private readonly int? _memoryDeltaKhz;
    private readonly int _graphicsDeltaKhz;
    private readonly int _coreVoltageDeltaUv;

    public GpuFixture()
    {
        if (!Elevation.IsElevated())
        {
            SkipReason = "the test host is not elevated - run 'dotnet test e2e' from an Administrator shell.";
            return;
        }

        try
        {
            NvApi.Initialize();
            _initialized = true;

            IntPtr[] gpus = NvApi.EnumeratePhysicalGpus();
            if (gpus.Length == 0)
            {
                SkipReason = "no NVIDIA GPU found.";
                return;
            }

            Gpu = gpus[0];

            _curveDeltasKhz = GpuTuning.CurveDeltasKhz(Gpu);
            _voltageBoostPercent = NvApi.GetCoreVoltageBoostPercent(Gpu);

            TuningSnapshot tuning = TuningSnapshot.Read(Gpu);
            if (tuning.MemoryClockKhz is { Ok: true } mem && tuning.BaseMemoryClockKhz is { Ok: true, Value: > 0 } baseMem)
            {
                _memoryDeltaKhz = mem.Value - baseMem.Value;
            }

            // The tests' clear/undervolt runs zero every P0 offset (SetPstate0Offsets writes all
            // three), so a pre-existing graphics-clock or core-voltage offset must be captured too.
            // Best-effort like the memory delta: unreadable reads restore as the driver default.
            try
            {
                (_graphicsDeltaKhz, _coreVoltageDeltaUv) = ReadPstate0Deltas(NvApi.GetPstates20(Gpu));
            }
            catch (Exception)
            {
            }

            Available = true;
        }
        catch (Exception ex)
        {
            SkipReason = $"NVAPI is unavailable: {ex.Message}";
        }
    }

    public void Dispose()
    {
        if (!_initialized)
        {
            return;
        }

        // Restore in the same order a real apply uses: the P0 offsets first (SetPstate0Offsets
        // re-derives the perf table and wipes curve deltas), then the curve, so the restored curve
        // isn't clobbered. All three P0 knobs go back in one write; one the snapshot couldn't read
        // restores as 0, the driver default.
        TryRestore(() => NvApi.SetPstate0Offsets(Gpu, _graphicsDeltaKhz, _memoryDeltaKhz ?? 0, _coreVoltageDeltaUv));
        TryRestore(() => NvApi.SetCoreVoltageBoostPercent(Gpu, _voltageBoostPercent));
        TryRestore(() => { if (_curveDeltasKhz is { } c) NvApi.SetCurveFreqDeltasKhz(Gpu, c); });

        NvApi.Unload();
    }

    /// <summary>The P0 graphics-clock delta (kHz) and core base-voltage delta (uV) — the two
    /// <see cref="NvApi.SetPstate0Offsets"/> knobs the memory-delta snapshot doesn't cover.</summary>
    private static (int GraphicsDeltaKhz, int CoreVoltageDeltaUv) ReadPstate0Deltas(Pstates20InfoV1 info)
        => (GpuTuning.P0Clock(info, NvApi.CLOCK_DOMAIN_GRAPHICS)?.FreqDeltaKhz.Value ?? 0,
            GpuTuning.P0BaseVoltage(info, NvApi.VOLTAGE_DOMAIN_CORE)?.ValueDeltaUv.Value ?? 0);

    private static void TryRestore(Action restore)
    {
        try
        {
            restore();
        }
        catch (Exception)
        {
            // Best-effort: leave that knob at the driver default rather than abort the rest of the restore.
        }
    }
}

[CollectionDefinition(Name)]
public sealed class GpuCollection : ICollectionFixture<GpuFixture>
{
    public const string Name = "gpu";
}
