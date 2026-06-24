namespace SimpleNvidiaUndervolt.E2E;

/// <summary>
/// Shared setup for the end-to-end tests, which drive the real NVIDIA driver. Created once for the whole
/// <see cref="GpuCollection"/> (so the tests never run concurrently against the one GPU). It decides
/// whether the suite can run at all — it needs an elevated host with an NVIDIA GPU — and, when it can,
/// snapshots the current tuning (core V/F curve deltas, memory clock offset, voltage boost) and restores
/// it on dispose, so a run leaves the GPU as it found it. If a restore step fails, that knob is left at
/// the driver default rather than the original value.
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

            int count = NvApi.GetVfCurve(Gpu).Count;
            _curveDeltasKhz = NvApi.GetCurveFreqDeltasKhz(Gpu, count);
            _voltageBoostPercent = NvApi.GetCoreVoltageBoostPercent(Gpu);

            GpuTuning tuning = GpuTuning.Read(Gpu);
            if (tuning.MemoryClockKhz is { Ok: true } mem && tuning.BaseMemoryClockKhz is { Ok: true, Value: > 0 } baseMem)
            {
                _memoryDeltaKhz = mem.Value - baseMem.Value;
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

        // Restore in the same order a real apply uses: memory first (it re-derives the perf table and
        // wipes curve deltas), then the curve, so the restored curve isn't clobbered.
        TryRestore(() => { if (_memoryDeltaKhz is { } d) GpuTuning.SetMemoryClockOffsetKhz(Gpu, d); });
        TryRestore(() => NvApi.SetCoreVoltageBoostPercent(Gpu, _voltageBoostPercent));
        TryRestore(() => { if (_curveDeltasKhz is { } c) NvApi.SetCurveFreqDeltasKhz(Gpu, c); });

        NvApi.Unload();
    }

    private static void TryRestore(Action restore)
    {
        try
        {
            restore();
        }
        catch (NvApiException)
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
