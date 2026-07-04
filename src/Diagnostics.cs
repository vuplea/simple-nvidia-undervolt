using System.Globalization;

namespace SimpleNvidiaUndervolt;

/// <summary>
/// Tools for inspecting the undocumented NVAPI tuning structs. They work
/// on raw buffers rather than the typed layouts so unknown or larger-than-expected structures can
/// be explored safely (reads are over-allocated to contain any driver overflow).
/// </summary>
internal static class Diagnostics
{
    /// <summary>Dumps the full live V/F curve (every voltage -> frequency point).</summary>
    public static void Curve(IntPtr gpu)
    {
        var points = NvApi.GetVfCurve(gpu);

        Console.WriteLine($"V/F curve, {points.Count} points");
        foreach (var (mv, mhz) in points)
        {
            Console.WriteLine($"  {mv,4} mV -> {mhz,4} MHz");
        }
    }

    /// <summary>Auto-detects the status (effective curve) buffer's record layout from a live read and
    /// prints it next to the compiled offsets, flagging whether they match - read-only confirmation that
    /// this build's read offsets fit the card (the same detection the status/undervolt warnings use, run
    /// on demand). The control (write) buffer carries no voltage to detect, so it points at snapshot/diff.</summary>
    public static void Layout(IntPtr gpu)
    {
        if (!CurveLayout.TryDetect(NvApi.ReadVfCurveStatusRaw(gpu), out CurveLayout d))
        {
            throw new CliError(
                "Could not find a V/F curve in the status buffer - its function id or size may differ on "
                + "this card. Use 'probe'/'extent' to find them, then 'raw' to inspect the words.");
        }

        Console.WriteLine(GpuTuning.BuildLine);
        Console.WriteLine("Status (effective curve) buffer:");
        Console.WriteLine($"  {GpuTuning.DetectedLine(d)}");
        Console.WriteLine($"  {GpuTuning.CompiledLine}");
        if (!d.MatchesCompiled())
        {
            Console.WriteLine("  -> differs from the build; update the Status* offsets in src/NvApi.cs.");
        }
        else if (d.FreqColumn < 0)
        {
            Console.WriteLine("  -> stride and voltage match; re-run under a 3D load to confirm the freq column.");
        }
        else
        {
            Console.WriteLine("  -> matches the build; the read path fits this card.");
        }

        Console.WriteLine($"""

            Control (editable deltas) buffer carries no voltage, so derive it with a write:
              'snapshot', nudge one curve point in Afterburner, then 'diff' - the changed curveControl
              words give the stride (their spacing) and the delta offset; the run starts at control
              entry i-1 for the lowest moved status anchor i.
              Compiled: stride {NvApi.CtrlEntryStride}  delta +0x{NvApi.CtrlDeltaOffset:X2}.
            """);
    }

    internal readonly record struct RawRequest(uint FunctionId, int Version, int Size, int MaskWords);
    internal readonly record struct ExtentRequest(uint FunctionId, int Version, int ClaimedSize);

    /// <summary>Dumps the raw 32-bit words a 2-arg GET writes, for locating unknown fields.</summary>
    public static void Raw(IntPtr gpu, RawRequest request)
    {
        // Over-allocate to the probe bound: the function id is arbitrary here, and a driver that
        // accepts the claimed size may still write its real (larger) struct - see NvApi.ReadRaw.
        byte[] bytes = NvApi.ReadRaw(gpu, request.FunctionId, request.Version, request.Size,
            NvApi.ProbeAllocSize, request.MaskWords);

        Console.WriteLine($"0x{request.FunctionId:X8} v{request.Version} ({request.Size} bytes)");
        for (int b = 0; b + 4 <= request.Size; b += 4)
        {
            Console.WriteLine($"  +0x{b:X2} = {BitConverter.ToUInt32(bytes, b)}");
        }
    }

    internal static RawRequest ParseRawRequest(string[] rest)
    {
        int maskWords = 0;
        if (rest.Length is < 3 or > 4
            || !TryParseRawRead(rest, out uint functionId, out int version, out int size)
            || (rest.Length == 4 && (!TryParseInt(rest[3], out maskWords) || maskWords < 0)))
        {
            throw new CliError("Usage: simple-nvidia-undervolt raw <hexFunctionId> <version> <size> [maskWords]\n"
                               + $"  version >= 1, size 4..{NvApi.MaxClaimedSize}, maskWords >= 0.");
        }

        return new RawRequest(functionId, version, size, maskWords);
    }

    /// <summary>Prints the current/base/boost public clocks for the core and memory domains. The base
    /// clock is independent of any applied offset, so it exposes the stock memory clock even while an
    /// offset is live (the pstates read only ever reports the offset-applied absolute).</summary>
    public static void Clocks(IntPtr gpu)
    {
        (string Label, uint Domain)[] domains = { ("core", NvApi.CLOCK_DOMAIN_GRAPHICS), ("memory", NvApi.CLOCK_DOMAIN_MEMORY) };
        (string Label, uint Type)[] types =
        {
            ("current", NvApi.CLOCK_FREQ_TYPE_CURRENT),
            ("base", NvApi.CLOCK_FREQ_TYPE_BASE),
            ("boost", NvApi.CLOCK_FREQ_TYPE_BOOST),
        };

        Console.WriteLine("Public clocks (MHz)");
        foreach (var (domainLabel, domain) in domains)
        {
            string cells = string.Join("  ", types.Select(t =>
            {
                uint khz = NvApi.GetClockFrequencyKhz(gpu, t.Type, domain);
                return $"{t.Label} {(khz == 0 ? "-" : (khz / 1000).ToString())}";
            }));
            Console.WriteLine($"  {domainLabel}: {cells}");
        }
    }

    /// <summary>Prints a one-shot snapshot of the live core voltage, clock, temperature and power.</summary>
    public static void Voltage(IntPtr gpu)
    {
        Console.WriteLine(NvApi.SafeFullName(gpu));
        Console.WriteLine($"  {Telemetry.Sample(gpu)}");
    }

    /// <summary>Polls the live core voltage, clock, temperature and power, tracking the running maximum
    /// of each, until Ctrl+C. Leave it running under a sustained load: the peak voltage it reports is
    /// the effective ceiling, the highest voltage the boost algorithm actually authorizes. The poll
    /// interval is 1 s, or <c>--interval &lt;seconds&gt;</c>.</summary>
    public static void Watch(IntPtr gpu, int intervalMs)
    {
        Console.WriteLine(
            $"{NvApi.SafeFullName(gpu)} - polling every {intervalMs / 1000.0:0.###}s, Ctrl+C to stop");

        using var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };

        var max = Telemetry.Sample(gpu);
        while (!stop.IsSet)
        {
            var now = Telemetry.Sample(gpu);
            max = Telemetry.Max(max, now);
            Console.WriteLine($"  {now}");
            stop.Wait(intervalMs);
        }

        Console.WriteLine($"  Peak: {max}");
    }

    /// <summary>The watch poll interval from <c>--interval</c> (seconds, fractions allowed; default 1).
    /// Bounded so a typo can't hammer the driver or read as a hang.</summary>
    internal static int ParseIntervalMs(Args.Parsed args)
    {
        if (args.Number("--interval") is not { } seconds)
        {
            return 1000;
        }

        if (seconds is < 0.1 or > 3600)
        {
            throw new CliError("--interval must be a number of seconds between 0.1 and 3600.");
        }

        return (int)Math.Round(seconds * 1000);
    }

    /// <summary>Finds where a value (and its common half/double/unit re-encodings) is stored across
    /// the tuning buffers — how the memory-offset and curve fields were located.</summary>
    public static void Scan(IntPtr gpu, int target)
    {
        (string Label, int Centre)[] encodings =
        {
            ("x1", target), ("/2", target / 2), ("x2", target * 2),
            ("x4", target * 4), ("x8", target * 8), ("/4", target / 4), ("neg", -target),
        };
        const int tolerance = 2000;
        encodings = encodings.Where(e => Math.Abs(e.Centre) >= 10_000).ToArray();
        if (encodings.Length == 0)
        {
            // With nothing left to scan, "no matches" would read as a genuine miss.
            throw new CliError($"scan: {target} is too small to scan reliably - matches allow "
                               + $"+/-{tolerance}, so only values of 10000 or more stand out.");
        }

        Console.WriteLine($"Scanning for {target} (+/-{tolerance}, as "
                          + $"{string.Join(' ', encodings.Select(e => e.Label))})");
        int hits = 0;
        foreach (var (name, bytes) in NvApi.ReadRawTuningBuffers(gpu))
        {
            for (int offset = 0; offset + 4 <= bytes.Length; offset += 4)
            {
                int word = BitConverter.ToInt32(bytes, offset);
                var match = encodings.FirstOrDefault(e => Math.Abs(word - e.Centre) <= tolerance);
                if (match.Label is not null)
                {
                    hits++;
                    Console.WriteLine($"  {name,-14} +0x{offset:X4} = {word}  [{match.Label}]");
                }
            }
        }

        if (hits == 0)
        {
            Console.WriteLine("  no matches in any tuning buffer");
        }
    }

    internal static int ParseScanTarget(string[] rest)
    {
        if (rest.Length != 1 || !TryParseInt(rest[0], out int target))
        {
            throw new CliError("Usage: simple-nvidia-undervolt scan <value>   (e.g. scan 117000)");
        }

        return target;
    }

    /// <summary>Saves the tuning buffers so a later <see cref="Diff"/> can reveal which words a
    /// setting change moved.</summary>
    public static void Snapshot(IntPtr gpu)
    {
        var buffers = NvApi.ReadRawTuningBuffers(gpu);

        using var writer = new BinaryWriter(File.Create(SnapshotPath));
        writer.Write(buffers.Count);
        foreach (var (name, bytes) in buffers)
        {
            writer.Write(name);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        Console.WriteLine($"Snapshot ({buffers.Count} buffers) written to {SnapshotPath}");
        Console.WriteLine("Now change a setting in Afterburner, click Apply, then run: simple-nvidia-undervolt diff");
    }

    /// <summary>Compares the current buffers against the last snapshot and prints changed words.</summary>
    public static void Diff(IntPtr gpu)
    {
        if (!File.Exists(SnapshotPath))
        {
            throw new CliError("No snapshot found. Run 'simple-nvidia-undervolt snapshot' first.");
        }

        var baseline = new Dictionary<string, byte[]>();
        using (var reader = new BinaryReader(File.OpenRead(SnapshotPath)))
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string name = reader.ReadString();
                int length = reader.ReadInt32();
                baseline[name] = reader.ReadBytes(length);
            }
        }

        Console.WriteLine("Changed words vs snapshot");

        int total = 0;
        foreach (var (name, current) in NvApi.ReadRawTuningBuffers(gpu))
        {
            if (!baseline.TryGetValue(name, out byte[]? old) || old.Length != current.Length)
            {
                continue;
            }

            for (int offset = 0; offset + 4 <= current.Length; offset += 4)
            {
                int before = BitConverter.ToInt32(old, offset);
                int after = BitConverter.ToInt32(current, offset);
                if (before != after)
                {
                    total++;
                    Console.WriteLine($"  {name,-14} +0x{offset:X4}: {before} -> {after}  (delta {after - before})");
                }
            }
        }

        if (total == 0)
        {
            Console.WriteLine("  no differences (the setting may live in a buffer not captured here)");
        }
    }

    /// <summary>Brute-forces which (version, size) the driver accepts for a function id. Reads are
    /// over-allocated, so an accepted-but-larger struct cannot corrupt the heap.</summary>
    public static void Probe(IntPtr gpu, uint functionId)
    {
        Console.WriteLine($"Probing 0x{functionId:X8} (accepted version/size pairs)");

        for (int version = 1; version <= 4; version++)
        {
            foreach (int size in NvApi.ProbeAcceptedSizes(gpu, functionId, version, minSize: 8, maxSize: 50000, step: 4))
            {
                Console.WriteLine($"  version={version} size={size} (0x{size:X})");
            }
        }

        Console.WriteLine("done");
    }

    internal static uint ParseProbeFunctionId(string[] rest)
    {
        if (rest.Length != 1 || !TryParseHex(rest[0], out uint functionId))
        {
            throw new CliError("Usage: simple-nvidia-undervolt probe <hexFunctionId>   (e.g. probe 21537AD4)");
        }

        return functionId;
    }

    /// <summary>For an accepted (version, claimed size) — as found by <see cref="Probe"/> — measures
    /// how many bytes the driver actually writes. This reveals real struct sizes that exceed the
    /// size encoded in the version word (e.g. the V3 curve status, whose word says 23308 but writes
    /// ~44 KB).</summary>
    public static void Extent(IntPtr gpu, ExtentRequest request)
    {
        byte[] bytes = NvApi.ReadRaw(gpu, request.FunctionId, request.Version, request.ClaimedSize,
            NvApi.ProbeAllocSize, requestMaskWords: 4);

        int written = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != 0)
            {
                written = i + 1;
            }
        }

        Console.WriteLine($"0x{request.FunctionId:X8} v{request.Version} claimed {request.ClaimedSize} "
                          + $"-> driver wrote {written} bytes");
    }

    internal static ExtentRequest ParseExtentRequest(string[] rest)
    {
        if (rest.Length != 3 || !TryParseRawRead(rest, out uint functionId, out int version, out int claimedSize))
        {
            throw new CliError("Usage: simple-nvidia-undervolt extent <hexFunctionId> <version> <claimedSize>\n"
                               + $"  version >= 1, claimedSize 4..{NvApi.MaxClaimedSize}.");
        }

        return new ExtentRequest(functionId, version, claimedSize);
    }

    private static string SnapshotPath => Path.Combine(Path.GetTempPath(), "simple-nvidia-undervolt.snapshot");

    /// <summary>Parses the <c>&lt;hexFunctionId&gt; &lt;version&gt; &lt;size&gt;</c> triple the
    /// <c>raw</c> and <c>extent</c> commands share, with the bounds a raw read accepts (version at
    /// least 1, size within what the version word can claim). The caller checks <c>rest</c> holds
    /// three tokens.</summary>
    private static bool TryParseRawRead(string[] rest, out uint functionId, out int version, out int size)
    {
        version = 0;
        size = 0;
        return TryParseHex(rest[0], out functionId)
               && TryParseInt(rest[1], out version) && version >= 1
               && TryParseInt(rest[2], out size) && size >= 4 && size <= NvApi.MaxClaimedSize;
    }

    /// <summary>Parses an integer invariantly, like every other numeric argument on this CLI
    /// (see <see cref="Args.Parsed.Number"/>) — a diagnostic argument mustn't depend on the machine's
    /// regional settings either.</summary>
    private static bool TryParseInt(string token, out int value)
        => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>Parses a hex function id, tolerating an optional leading <c>0x</c>.</summary>
    internal static bool TryParseHex(string? token, out uint value)
    {
        string? digits = token?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) is true
            ? token[2..]
            : token;
        return uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>A single reading of the live core voltage, clock, temperature and power. Each field reads
/// independently and is null if its NVAPI call is unavailable (shown as "n/a" rather than a misleading
/// 0), so one failure does not blank the rest.</summary>
internal readonly struct Telemetry
{
    public uint? VoltageUv { get; private init; }
    public uint? CoreMhz { get; private init; }
    public int? TemperatureC { get; private init; }
    public double? PowerPercent { get; private init; }

    public static Telemetry Sample(IntPtr gpu) => new()
    {
        VoltageUv = Safe(() => NvApi.GetCoreVoltageUv(gpu)),
        CoreMhz = Safe(() => NvApi.GetClockFrequencyKhz(gpu, NvApi.CLOCK_FREQ_TYPE_CURRENT, NvApi.CLOCK_DOMAIN_GRAPHICS) / 1000),
        TemperatureC = Safe(() => NvApi.GetCoreTemperatureC(gpu)),
        PowerPercent = Safe(() => NvApi.GetPowerPercent(gpu)),
    };

    public static Telemetry Max(Telemetry a, Telemetry b) => new()
    {
        VoltageUv = MaxN(a.VoltageUv, b.VoltageUv),
        CoreMhz = MaxN(a.CoreMhz, b.CoreMhz),
        TemperatureC = MaxN(a.TemperatureC, b.TemperatureC),
        PowerPercent = MaxN(a.PowerPercent, b.PowerPercent),
    };

    public override string ToString()
        => $"{Col(VoltageUv / 1000, 4)} mV  {Col(CoreMhz, 4)} MHz  {Col(TemperatureC, 2)} C  "
           + $"{Col(PowerPercent, 5, "0.0")}% TGP";

    /// <summary>A right-aligned column of the value, or "n/a" (also right-aligned) when it is unavailable.</summary>
    private static string Col<T>(T? value, int width, string? format = null) where T : struct, IFormattable
        => (value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) : "n/a").PadLeft(width);

    /// <summary>The larger of two optional readings, ignoring an unavailable side (null if both are).</summary>
    private static T? MaxN<T>(T? a, T? b) where T : struct, IComparable<T>
        => a is { } x ? (b is { } y ? (x.CompareTo(y) >= 0 ? a : b) : a) : b;

    private static T? Safe<T>(Func<T> read) where T : struct
    {
        try { return read(); }
        catch (Exception) { return null; }
    }
}
