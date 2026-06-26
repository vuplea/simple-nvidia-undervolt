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
    public static int Curve(IntPtr gpu)
    {
        var points = NvApi.GetVfCurve(gpu);

        Console.WriteLine($"V/F curve, {points.Count} points");
        foreach (var (mv, mhz) in points)
        {
            Console.WriteLine($"  {mv,4} mV -> {mhz,4} MHz");
        }

        return 0;
    }

    /// <summary>Dumps the raw 32-bit words a 2-arg GET writes, for locating unknown fields.</summary>
    public static int Raw(IntPtr gpu, string[] args)
    {
        var rest = args.SkipWhile(a => !a.Equals("raw", StringComparison.OrdinalIgnoreCase)).Skip(1).ToArray();
        if (rest.Length < 3
            || !uint.TryParse(rest[0].Replace("0x", ""), NumberStyles.HexNumber, null, out uint functionId)
            || !int.TryParse(rest[1], out int version)
            || !int.TryParse(rest[2], out int size))
        {
            Console.Error.WriteLine("Usage: simple-nvidia-undervolt raw <hexFunctionId> <version> <size> [maskWords]");
            return 2;
        }

        int maskWords = rest.Length > 3 && int.TryParse(rest[3], out int m) ? m : 0;
        // Over-allocate so a driver that writes past the claimed size overflows into our padding.
        byte[] bytes = NvApi.ReadRaw(gpu, functionId, version, size, Math.Max(size, 65536), maskWords);

        Console.WriteLine($"0x{functionId:X8} v{version} ({size} bytes)");
        for (int b = 0; b + 4 <= size; b += 4)
        {
            Console.WriteLine($"  +0x{b:X2} = {BitConverter.ToUInt32(bytes, b)}");
        }

        return 0;
    }

    /// <summary>Prints the current/base/boost public clocks for the core and memory domains. The base
    /// clock is independent of any applied offset, so it exposes the stock memory clock even while an
    /// offset is live (the pstates read only ever reports the offset-applied absolute).</summary>
    public static int Clocks(IntPtr gpu)
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

        return 0;
    }

    /// <summary>Prints a one-shot snapshot of the live core voltage, clock, temperature and power.</summary>
    public static int Voltage(IntPtr gpu)
    {
        Console.WriteLine(NvApi.SafeFullName(gpu));
        Console.WriteLine($"  {Telemetry.Sample(gpu)}");
        return 0;
    }

    /// <summary>Polls the live core voltage, clock, temperature and power, tracking the running maximum
    /// of each, until Ctrl+C. Leave it running under a sustained load: the peak voltage it reports is
    /// the effective ceiling, the highest voltage the boost algorithm actually authorizes.</summary>
    public static int Watch(IntPtr gpu)
    {
        Console.WriteLine($"{NvApi.SafeFullName(gpu)} - polling every 5s, Ctrl+C to stop");

        using var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };

        var max = Telemetry.Sample(gpu);
        while (!stop.IsSet)
        {
            var now = Telemetry.Sample(gpu);
            max = Telemetry.Max(max, now);
            Console.WriteLine($"  {now}");
            stop.Wait(5000);
        }

        Console.WriteLine($"  Peak: {max}");
        return 0;
    }

    /// <summary>Finds where a value (and its common half/double/unit re-encodings) is stored across
    /// the tuning buffers — how the memory-offset and curve fields were located.</summary>
    public static int Scan(IntPtr gpu, string[] args)
    {
        if (!TryGetIntArg(args, "scan", out int target))
        {
            Console.Error.WriteLine("Usage: simple-nvidia-undervolt scan <value>   (e.g. scan 117000)");
            return 2;
        }

        (string Label, int Centre)[] encodings =
        {
            ("x1", target), ("/2", target / 2), ("x2", target * 2),
            ("x4", target * 4), ("x8", target * 8), ("/4", target / 4), ("neg", -target),
        };
        encodings = encodings.Where(e => Math.Abs(e.Centre) >= 10_000).ToArray();
        const int tolerance = 2000;

        Console.WriteLine($"Scanning for {target} (+/-{tolerance}, with half/double/x4/x8 variants)");
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

        return 0;
    }

    /// <summary>Saves the tuning buffers so a later <see cref="Diff"/> can reveal which words a
    /// setting change moved.</summary>
    public static int Snapshot(IntPtr gpu)
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
        return 0;
    }

    /// <summary>Compares the current buffers against the last snapshot and prints changed words.</summary>
    public static int Diff(IntPtr gpu)
    {
        if (!File.Exists(SnapshotPath))
        {
            Console.Error.WriteLine("No snapshot found. Run 'simple-nvidia-undervolt snapshot' first.");
            return 2;
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

        return 0;
    }

    /// <summary>Brute-forces which (version, size) the driver accepts for a function id. Reads are
    /// over-allocated, so an accepted-but-larger struct cannot corrupt the heap.</summary>
    public static int Probe(IntPtr gpu, string[] args)
    {
        if (!TryGetHexArg(args, "probe", out uint functionId))
        {
            Console.Error.WriteLine("Usage: simple-nvidia-undervolt probe <hexFunctionId>   (e.g. probe 21537AD4)");
            return 2;
        }

        Console.WriteLine($"Probing 0x{functionId:X8} (accepted version/size pairs)");

        for (int version = 1; version <= 4; version++)
        {
            for (int size = 8; size <= 50000; size += 4)
            {
                int status = NvApi.ProbeStructVersion(gpu, functionId, version, size);
                if (status == 0)
                {
                    Console.WriteLine($"  version={version} size={size} (0x{size:X})");
                }
            }
        }

        Console.WriteLine("done");
        return 0;
    }

    /// <summary>For an accepted (version, claimed size) — as found by <see cref="Probe"/> — measures
    /// how many bytes the driver actually writes. This reveals real struct sizes that exceed the
    /// size encoded in the version word (e.g. the V3 curve status, whose word says 23308 but writes
    /// ~44 KB).</summary>
    public static int Extent(IntPtr gpu, string[] args)
    {
        var rest = args.SkipWhile(a => !a.Equals("extent", StringComparison.OrdinalIgnoreCase)).Skip(1).ToArray();
        if (rest.Length < 3
            || !uint.TryParse(rest[0].Replace("0x", ""), NumberStyles.HexNumber, null, out uint functionId)
            || !int.TryParse(rest[1], out int version)
            || !int.TryParse(rest[2], out int claimedSize))
        {
            Console.Error.WriteLine("Usage: simple-nvidia-undervolt extent <hexFunctionId> <version> <claimedSize>");
            return 2;
        }

        byte[] bytes = NvApi.ReadRaw(gpu, functionId, version, claimedSize, NvApi.ProbeAllocSize, requestMaskWords: 4);

        int written = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != 0)
            {
                written = i + 1;
            }
        }

        Console.WriteLine($"0x{functionId:X8} v{version} claimed {claimedSize} -> driver wrote {written} bytes");
        return 0;
    }

    private static string SnapshotPath => Path.Combine(Path.GetTempPath(), "simple-nvidia-undervolt.snapshot");

    private static bool TryGetIntArg(string[] args, string command, out int value)
    {
        string? arg = args.SkipWhile(a => !a.Equals(command, StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
        return int.TryParse(arg, out value);
    }

    private static bool TryGetHexArg(string[] args, string command, out uint value)
    {
        string? arg = args.SkipWhile(a => !a.Equals(command, StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
        return uint.TryParse(arg?.Replace("0x", ""), NumberStyles.HexNumber, null, out value);
    }
}

/// <summary>A single reading of the live core voltage, clock, temperature and power. Each field reads
/// independently and falls back to 0 if its NVAPI call is unavailable, so one failure does not blank
/// the rest.</summary>
internal readonly struct Telemetry
{
    public uint VoltageUv { get; private init; }
    public uint CoreMhz { get; private init; }
    public int TemperatureC { get; private init; }
    public double PowerPercent { get; private init; }

    public static Telemetry Sample(IntPtr gpu) => new()
    {
        VoltageUv = Safe(() => NvApi.GetCoreVoltageUv(gpu)),
        CoreMhz = Safe(() => NvApi.GetClockFrequencyKhz(gpu, NvApi.CLOCK_FREQ_TYPE_CURRENT, NvApi.CLOCK_DOMAIN_GRAPHICS) / 1000),
        TemperatureC = (int)Safe(() => (uint)NvApi.GetCoreTemperatureC(gpu)),
        PowerPercent = Safe(() => NvApi.GetPowerPercent(gpu)),
    };

    public static Telemetry Max(Telemetry a, Telemetry b) => new()
    {
        VoltageUv = Math.Max(a.VoltageUv, b.VoltageUv),
        CoreMhz = Math.Max(a.CoreMhz, b.CoreMhz),
        TemperatureC = Math.Max(a.TemperatureC, b.TemperatureC),
        PowerPercent = Math.Max(a.PowerPercent, b.PowerPercent),
    };

    public override string ToString()
        => $"{VoltageUv / 1000,4} mV  {CoreMhz,4} MHz  {TemperatureC,2} C  {PowerPercent,5:0.0}% TGP";

    private static T Safe<T>(Func<T> read) where T : struct
    {
        try { return read(); }
        catch (NvApiException) { return default; }
    }
}
