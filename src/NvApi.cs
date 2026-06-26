using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace SimpleNvidiaUndervolt;

/// <summary>
/// Minimal interop layer over NVIDIA's NVAPI, exposing what is needed to read and reset the
/// clock/voltage tuning that MSI Afterburner (and similar tools) program into the driver.
///
/// NVAPI exposes a single export, <c>nvapi_QueryInterface</c>, which maps a function id to the
/// real entry point. The ids and packed-struct layouts here are not part of the public SDK; they
/// are the long-stable definitions used by NVIDIA overclocking tools. Each
/// tuning struct begins with a <c>version</c> word encoding
/// <c>sizeof(struct) | (versionNumber &lt;&lt; 16)</c>, which the driver validates.
/// </summary>
internal static class NvApi
{
    // --- Function ids (arguments to nvapi_QueryInterface) ---
    private const uint ID_Initialize = 0x0150E828;
    private const uint ID_Unload = 0xD22BDD7E;
    private const uint ID_GetErrorMessage = 0x6C2D048C;
    private const uint ID_EnumPhysicalGPUs = 0xE5AC921F;
    private const uint ID_GPU_GetFullName = 0xCEEE8E9F;

    private const uint ID_GPU_GetPstates20 = 0x6FF81213;
    private const uint ID_GPU_SetPstates20 = 0x0F4DAE6B;
    private const uint ID_GPU_GetClockBoostLock = 0xE440B867; // PerfClientLimitsGetStatus (diagnostics only)
    private const uint ID_GPU_GetClockBoostTable = 0x23F1B133; // a.k.a. ClkVfPointsGetControl
    private const uint ID_GPU_SetClockBoostTable = 0x0733E009; // a.k.a. ClkVfPointsSetControl
    private const uint ID_GPU_GetCoreVoltageBoostPercent = 0x9DF23CA1;
    private const uint ID_GPU_SetCoreVoltageBoostPercent = 0xB9306D9B;

    /// <summary>ClockClientClkVfPointsGetStatus — the live, full V/F curve. Read via diagnostics.</summary>
    public const uint ID_GPU_GetVfCurveStatus = 0x21537AD4;

    /// <summary>ClientVoltRailsGetStatus — the live core rail voltage. Read via diagnostics.</summary>
    public const uint ID_GPU_GetVoltRailsStatus = 0x465F9BCF;

    /// <summary>NvAPI_GPU_GetAllClockFrequencies — current/base/boost public clocks. Read via diagnostics.</summary>
    public const uint ID_GPU_GetAllClockFrequencies = 0xDCB616C3;

    /// <summary>NvAPI_GPU_GetThermalSettings — current temperatures. Takes a sensor-index arg.</summary>
    private const uint ID_GPU_GetThermalSettings = 0xE3640A56;

    /// <summary>ClientPowerTopologyGetStatus — live board power draw (per-mille of percent of TGP).</summary>
    private const uint ID_GPU_GetPowerTopologyStatus = 0xEDCF624E;

    public const uint CLOCK_FREQ_TYPE_CURRENT = 0;
    public const uint CLOCK_FREQ_TYPE_BASE = 1;
    public const uint CLOCK_FREQ_TYPE_BOOST = 2;

    private const int NVAPI_MAX_PHYSICAL_GPUS = 64;
    private const int NVAPI_SHORT_STRING_MAX = 64;

    public const uint CLOCK_DOMAIN_GRAPHICS = 0;
    public const uint CLOCK_DOMAIN_MEMORY = 4;
    public const uint VOLTAGE_DOMAIN_CORE = 0;

    [DllImport("nvapi64", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr NvAPI_QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NoArgDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumPhysicalGpusDelegate([Out] IntPtr[] handles, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate int GetErrorMessageDelegate(int status, StringBuilder message);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate int GetFullNameDelegate(IntPtr gpu, StringBuilder name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GpuStructDelegate(IntPtr gpu, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GpuThermalDelegate(IntPtr gpu, uint sensorIndex, IntPtr data);

    private static T GetDelegate<T>(uint id) where T : Delegate
    {
        IntPtr address = NvAPI_QueryInterface(id);
        if (address == IntPtr.Zero)
        {
            throw new NvApiException($"NVAPI function 0x{id:X8} is not available in this driver.");
        }

        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static int MakeVersion(int structSize, int versionNumber)
        => structSize | (versionNumber << 16);

    private static void Check(int status, string action)
    {
        if (status != 0)
        {
            throw new NvApiException($"{action} failed: {DescribeStatus(status)}");
        }
    }

    private static string DescribeStatus(int status)
    {
        try
        {
            var message = new StringBuilder(NVAPI_SHORT_STRING_MAX);
            if (GetDelegate<GetErrorMessageDelegate>(ID_GetErrorMessage)(status, message) == 0)
            {
                return $"{message} ({status})";
            }
        }
        catch
        {
            // Fall through to the raw code.
        }

        return $"NVAPI status {status}";
    }

    public static void Initialize()
        => Check(GetDelegate<NoArgDelegate>(ID_Initialize)(), "NvAPI_Initialize");

    public static void Unload()
    {
        try
        {
            GetDelegate<NoArgDelegate>(ID_Unload)();
        }
        catch
        {
            // Unload is best-effort cleanup.
        }
    }

    public static IntPtr[] EnumeratePhysicalGpus()
    {
        var handles = new IntPtr[NVAPI_MAX_PHYSICAL_GPUS];
        Check(GetDelegate<EnumPhysicalGpusDelegate>(ID_EnumPhysicalGPUs)(handles, out int count),
            "NvAPI_EnumPhysicalGPUs");
        return handles.Take(count).ToArray();
    }

    public static string GetFullName(IntPtr gpu)
    {
        var name = new StringBuilder(NVAPI_SHORT_STRING_MAX);
        Check(GetDelegate<GetFullNameDelegate>(ID_GPU_GetFullName)(gpu, name), "NvAPI_GPU_GetFullName");
        return name.ToString();
    }

    /// <summary>The GPU's full name, or <c>&lt;unknown&gt;</c> if the driver won't report it — for the
    /// human-facing headers that shouldn't fail a command just because the name read did.</summary>
    public static string SafeFullName(IntPtr gpu)
    {
        try
        {
            return GetFullName(gpu);
        }
        catch (NvApiException)
        {
            return "<unknown>";
        }
    }

    // --- Generic versioned-struct get/set ---

    private static T GetStruct<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
        IntPtr gpu, uint functionId, int versionNumber, string action)
        where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size); // zero-fill
            Marshal.WriteInt32(buffer, MakeVersion(size, versionNumber));
            Check(GetDelegate<GpuStructDelegate>(functionId)(gpu, buffer), action);
            return Marshal.PtrToStructure<T>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void SetStruct<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
        IntPtr gpu, uint functionId, T value, int versionNumber, string action)
        where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, buffer, false);
            // Always stamp the version word: a freshly built struct has it at 0, which the driver
            // rejects with NVAPI_INCOMPATIBLE_STRUCT_VERSION.
            Marshal.WriteInt32(buffer, MakeVersion(size, versionNumber));
            Check(GetDelegate<GpuStructDelegate>(functionId)(gpu, buffer), action);
        }
        finally
        {
            Marshal.DestroyStructure<T>(buffer);
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Like <see cref="GetStruct{T}"/>, but marshals <paramref name="input"/> into the buffer
    /// first so caller-supplied input fields (e.g. a clock-type selector) survive into the call. The
    /// version word is always re-stamped over whatever the struct carried.</summary>
    private static T GetStructInOut<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
        IntPtr gpu, uint functionId, T input, int versionNumber, string action)
        where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(input, buffer, false);
            Marshal.WriteInt32(buffer, MakeVersion(size, versionNumber));
            Check(GetDelegate<GpuStructDelegate>(functionId)(gpu, buffer), action);
            return Marshal.PtrToStructure<T>(buffer);
        }
        finally
        {
            Marshal.DestroyStructure<T>(buffer);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void SetRequestMask(IntPtr buffer, int maskWords)
    {
        for (int i = 0; i < maskWords; i++)
        {
            Marshal.WriteInt32(buffer, 4 + i * 4, unchecked((int)0xFFFFFFFF));
        }
    }

    // --- Performance states (memory / core clock offsets, base voltage) ---

    public static Pstates20InfoV1 GetPstates20(IntPtr gpu)
        => GetStruct<Pstates20InfoV1>(gpu, ID_GPU_GetPstates20, 1, "NvAPI_GPU_GetPstates20");

    /// <summary>Writes the P0 graphics/memory clock and core-voltage offsets. The memory offset is
    /// applied here (it is absent from the curve control table), so this is what resets memory.
    ///
    /// Note the read/write asymmetry: <see cref="GetPstates20"/> reports the memory clock as an
    /// absolute frequency (Afterburner's offset already folded in), but the driver still tracks that
    /// offset internally as a P0 delta. Writing the delta as 0 here therefore returns the absolute
    /// clock to its stock base — e.g. a +440 MHz offset reading 14441 MHz drops back to 14001 MHz.</summary>
    public static void SetPstate0Offsets(IntPtr gpu, int graphicsDeltaKhz, int memoryDeltaKhz, int coreVoltageDeltaUv)
    {
        var info = NewPstates20();
        info.NumPstates = 1;
        info.NumClocks = 2;
        info.NumBaseVoltages = 1;

        ref Pstate20 p0 = ref info.Pstates[0];
        p0.PstateId = 0; // P0 - the 3D performance state Afterburner edits
        p0.Clocks[0].DomainId = CLOCK_DOMAIN_GRAPHICS;
        p0.Clocks[0].FreqDeltaKhz.Value = graphicsDeltaKhz;
        p0.Clocks[1].DomainId = CLOCK_DOMAIN_MEMORY;
        p0.Clocks[1].FreqDeltaKhz.Value = memoryDeltaKhz;
        p0.BaseVoltages[0].DomainId = VOLTAGE_DOMAIN_CORE;
        p0.BaseVoltages[0].ValueDeltaUv.Value = coreVoltageDeltaUv;

        SetStruct(gpu, ID_GPU_SetPstates20, info, 1, "NvAPI_GPU_SetPstates20");
    }

    // --- Public clock frequencies (current / base / boost) ---

    /// <summary>Reads a public clock domain's frequency (kHz) for the given clock type. The base type
    /// reports the factory clock independent of any applied offset; returns 0 if not populated.</summary>
    public static uint GetClockFrequencyKhz(IntPtr gpu, uint clockType, uint domain)
    {
        var input = new ClockFrequenciesV2
        {
            ClockType = clockType,
            Domains = new ClockFrequencyDomain[ClockFrequenciesV2.MaxDomains],
        };
        var result = GetStructInOut(gpu, ID_GPU_GetAllClockFrequencies, input, 2, "NvAPI_GPU_GetAllClockFrequencies");

        ClockFrequencyDomain entry = result.Domains[domain];
        return (entry.IsPresent & 1) != 0 ? entry.FrequencyKhz : 0;
    }

    // --- Live telemetry (voltage / temperature / power) ---

    /// <summary>The live core-rail voltage (uV). In ClientVoltRailsGetStatus the value sits at +0x28.</summary>
    public static uint GetCoreVoltageUv(IntPtr gpu)
    {
        byte[] bytes = ReadRaw(gpu, ID_GPU_GetVoltRailsStatus, 1, 76, 256, requestMaskWords: 0);
        return BitConverter.ToUInt32(bytes, 0x28);
    }

    /// <summary>The live GPU core temperature (degrees C), from sensor 0 of GetThermalSettings (V2).</summary>
    public static int GetCoreTemperatureC(IntPtr gpu)
    {
        const int size = 68; // NV_GPU_THERMAL_SETTINGS_V2: version, count, sensor[3] x 20 bytes
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteInt32(buffer, MakeVersion(size, 2));
            Check(GetDelegate<GpuThermalDelegate>(ID_GPU_GetThermalSettings)(gpu, 0, buffer),
                "NvAPI_GPU_GetThermalSettings");
            return Marshal.ReadInt32(buffer, 0x14); // sensor[0].currentTemp
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The live board power draw as a percentage of the total power limit (TGP). The power
    /// topology reports channel 0 (total) in thousandths of a percent, so the value is scaled by 1000.</summary>
    public static double GetPowerPercent(IntPtr gpu)
    {
        byte[] bytes = ReadRaw(gpu, ID_GPU_GetPowerTopologyStatus, 1, 72, 256, requestMaskWords: 0);
        return BitConverter.ToUInt32(bytes, 0x10) / 1000.0; // entry[0].value
    }

    // --- V/F curve: status (effective curve) and control (per-point frequency deltas) ---
    //
    // Both buffers are flat arrays of per-point entries that align by index (one entry per voltage
    // anchor, ~127 of them). The byte offsets below were derived against a known
    // Afterburner curve; the typed-struct layout the SDK headers imply does not match the driver, so
    // these are handled as raw bytes. The status buffer reports the *effective* curve (it reflects an
    // applied offset); the control buffer holds only the editable freq deltas (0 at stock).
    private const int CONTROL_TABLE_SIZE = 9248; // ClkVfPointsGetControl / SetControl, version 1
    private const int CtrlEntryBase = 0x64;
    private const int CtrlEntryStride = 36;
    private const int CtrlDeltaOffset = 0x18; // signed kHz frequency delta within an entry

    private const int StatusCurveSize = 7208; // ClkVfPointsGetStatus, version 1
    private const int StatusEntryBase = 0x40;
    private const int StatusEntryStride = 28;
    private const int StatusFreqOffset = 0x08; // kHz
    private const int StatusVoltOffset = 0x0C; // uV

    /// <summary>Reads the V/F curve as ordered (millivolt, megahertz) points. The voltage of each
    /// anchor is stable, but the frequency column reflects the <em>live</em> curve, so at deep idle it
    /// collapses to a low value (the card has to be in a 3D power state for the real per-point clocks to
    /// appear). The point ordering aligns by index with the control-table deltas. Entries are walked by
    /// ascending voltage, independent of the frequency, so the voltage map survives an idle read.</summary>
    public static IReadOnlyList<(int Mv, int Mhz)> GetVfCurve(IntPtr gpu)
    {
        byte[] bytes = ReadRaw(gpu, ID_GPU_GetVfCurveStatus, 1, StatusCurveSize, StatusCurveSize,
            requestMaskWords: 4);

        var points = new List<(int Mv, int Mhz)>();
        int lastVoltUv = 0;
        for (int e = StatusEntryBase; e + StatusEntryStride <= bytes.Length; e += StatusEntryStride)
        {
            int freq = BitConverter.ToInt32(bytes, e + StatusFreqOffset);
            int volt = BitConverter.ToInt32(bytes, e + StatusVoltOffset);
            if (volt is < 300_000 or > 1_300_000)
            {
                break; // past the populated entries (trailing zeros / wrap sentinel)
            }

            // Compare raw microvolts, not the truncated mV: a sub-mV decrease still marks the end of the
            // real array, and truncating first would let such an entry slip through.
            if (points.Count > 0 && volt <= lastVoltUv)
            {
                break; // no longer ascending - end of the real array
            }

            lastVoltUv = volt;
            points.Add((volt / 1000, Math.Max(0, freq) / 1000));
        }

        return points;
    }

    // The control entry at index j drives the effective frequency of the NEXT curve anchor (status
    // index j+1) — an off-by-one verified empirically (poking control entry j moves status anchor
    // j+1 by the written amount). So the delta for status/curve anchor i lives at control index i-1;
    // anchor 0 (the lowest voltage) has no control entry and cannot be moved. These helpers present
    // deltas already aligned to the curve index so callers can ignore the shift.

    /// <summary>Reads the per-point frequency deltas (kHz), index-aligned with <see cref="GetVfCurve"/>
    /// (the delta of anchor i is read from control entry i-1).</summary>
    public static int[] GetCurveFreqDeltasKhz(IntPtr gpu, int count)
    {
        byte[] bytes = ReadRaw(gpu, ID_GPU_GetClockBoostTable, 1, CONTROL_TABLE_SIZE, CONTROL_TABLE_SIZE,
            requestMaskWords: 4);

        var deltas = new int[count];
        for (int i = 1; i < count; i++)
        {
            deltas[i] = BitConverter.ToInt32(bytes, CtrlEntryBase + (i - 1) * CtrlEntryStride + CtrlDeltaOffset);
        }

        return deltas;
    }

    /// <summary>Writes per-point frequency deltas (kHz) via read-modify-write, so reserved fields
    /// round-trip. <paramref name="deltasKhz"/> is index-aligned with the curve (the delta of anchor i
    /// is written to control entry i-1; anchor 0 has no control entry and is ignored).</summary>
    public static void SetCurveFreqDeltasKhz(IntPtr gpu, int[] deltasKhz)
    {
        IntPtr buffer = Marshal.AllocHGlobal(CONTROL_TABLE_SIZE);
        try
        {
            Marshal.Copy(new byte[CONTROL_TABLE_SIZE], 0, buffer, CONTROL_TABLE_SIZE);
            Marshal.WriteInt32(buffer, MakeVersion(CONTROL_TABLE_SIZE, 1));
            SetRequestMask(buffer, 4);
            Check(GetDelegate<GpuStructDelegate>(ID_GPU_GetClockBoostTable)(gpu, buffer),
                "NvAPI_GPU_GetClockBoostTable");

            for (int i = 1; i < deltasKhz.Length; i++)
            {
                Marshal.WriteInt32(buffer, CtrlEntryBase + (i - 1) * CtrlEntryStride + CtrlDeltaOffset, deltasKhz[i]);
            }

            Marshal.WriteInt32(buffer, MakeVersion(CONTROL_TABLE_SIZE, 1));
            SetRequestMask(buffer, 4);
            Check(GetDelegate<GpuStructDelegate>(ID_GPU_SetClockBoostTable)(gpu, buffer),
                "NvAPI_GPU_SetClockBoostTable");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // --- Core voltage boost percentage (Afterburner "Core Voltage (%)") ---

    public static uint GetCoreVoltageBoostPercent(IntPtr gpu)
        => GetStruct<VoltageBoostPercentV1>(gpu, ID_GPU_GetCoreVoltageBoostPercent, 1,
            "NvAPI_GPU_GetCoreVoltageBoostPercent").Percent;

    public static void SetCoreVoltageBoostPercent(IntPtr gpu, uint percent)
    {
        // Read first so the opaque trailing words round-trip unchanged.
        var value = GetStruct<VoltageBoostPercentV1>(gpu, ID_GPU_GetCoreVoltageBoostPercent, 1,
            "NvAPI_GPU_GetCoreVoltageBoostPercent");
        value.Percent = percent;
        SetStruct(gpu, ID_GPU_SetCoreVoltageBoostPercent, value, 1, "NvAPI_GPU_SetCoreVoltageBoostPercent");
    }

    private static Pstates20InfoV1 NewPstates20()
    {
        var info = new Pstates20InfoV1 { Pstates = new Pstate20[Pstates20InfoV1.MaxPstates] };
        for (int i = 0; i < info.Pstates.Length; i++)
        {
            info.Pstates[i].Clocks = new Pstate20ClockEntry[Pstate20.MaxClocks];
            info.Pstates[i].BaseVoltages = new Pstate20BaseVoltageEntry[Pstate20.MaxBaseVoltages];
        }

        return info;
    }

    // -----------------------------------------------------------------------------------------
    // Diagnostics
    //
    // Raw-buffer helpers for inspecting NVAPI tuning structs (used by the
    // scan / snapshot / diff / probe / extent / curve commands). They deliberately bypass the
    // typed structs above so unknown layouts can be explored safely.
    // -----------------------------------------------------------------------------------------

    /// <summary>The tuning buffers worth scanning/diffing, with the (version, size, request-mask)
    /// each one needs. Sizes that have no managed struct are given as literals.</summary>
    public static IReadOnlyList<(string Name, byte[] Bytes)> ReadRawTuningBuffers(IntPtr gpu)
    {
        (string Name, uint Id, int Version, int Size, int MaskWords)[] specs =
        {
            ("pstates20", ID_GPU_GetPstates20, 1, Marshal.SizeOf<Pstates20InfoV1>(), 0),
            ("curveControl", ID_GPU_GetClockBoostTable, 1, CONTROL_TABLE_SIZE, 4),
            ("curveStatusV1", ID_GPU_GetVfCurveStatus, 1, StatusCurveSize, 4), // same buffer GetVfCurve decodes
            ("voltageLock", ID_GPU_GetClockBoostLock, 2, 780, 0),
            ("clkDomainsInfo", 0x64B43A6A, 1, 2344, 0),
            ("clkVfPointsInfo", 0x507B4B59, 1, 6188, 4),
        };

        var result = new List<(string, byte[])>();
        foreach (var spec in specs)
        {
            try
            {
                result.Add((spec.Name, ReadRaw(gpu, spec.Id, spec.Version, spec.Size, spec.Size, spec.MaskWords)));
            }
            catch (NvApiException)
            {
                // Skip structures this driver/GPU does not support.
            }
        }

        return result;
    }

    /// <summary>Calls a GET with a given (version, claimed size) and returns the raw NVAPI status
    /// without throwing — used to discover which struct size/version the driver accepts. The buffer
    /// is over-allocated so an accepted-but-larger struct cannot overflow into the heap.</summary>
    /// <summary>Over-allocation for raw reads whose real struct size is unknown (probe / extent): large
    /// enough to contain anything the driver writes, so an oversized struct can't corrupt the heap.</summary>
    public const int ProbeAllocSize = 262144;

    public static int ProbeStructVersion(IntPtr gpu, uint functionId, int versionNumber, int claimedSize)
    {
        const int allocSize = ProbeAllocSize;
        IntPtr buffer = Marshal.AllocHGlobal(allocSize);
        try
        {
            Marshal.Copy(new byte[allocSize], 0, buffer, allocSize);
            Marshal.WriteInt32(buffer, MakeVersion(claimedSize, versionNumber));
            return GetDelegate<GpuStructDelegate>(functionId)(gpu, buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Reads a raw buffer for a function. <paramref name="allocSize"/> may exceed
    /// <paramref name="claimedSize"/>: the driver validates the version word against the claimed
    /// size but may write the (larger) real struct, so the extra allocation keeps any overflow in
    /// our own padding instead of corrupting the heap. Returns the full allocation.</summary>
    public static byte[] ReadRaw(IntPtr gpu, uint functionId, int versionNumber, int claimedSize,
        int allocSize, int requestMaskWords)
    {
        IntPtr buffer = Marshal.AllocHGlobal(allocSize);
        try
        {
            Marshal.Copy(new byte[allocSize], 0, buffer, allocSize);
            Marshal.WriteInt32(buffer, MakeVersion(claimedSize, versionNumber));
            SetRequestMask(buffer, requestMaskWords);
            Check(GetDelegate<GpuStructDelegate>(functionId)(gpu, buffer), $"raw read 0x{functionId:X8}");
            var bytes = new byte[allocSize];
            Marshal.Copy(buffer, bytes, 0, allocSize);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

internal sealed class NvApiException : Exception
{
    public NvApiException(string message) : base(message)
    {
    }
}
