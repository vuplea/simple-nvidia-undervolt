using SimpleNvidiaUndervolt;

return Cli.Run(args);

internal static class Cli
{
    public static int Run(string[] args)
    {
        if (args.Contains("--help-diagnostics"))
        {
            PrintDiagnosticsHelp();
            return 0;
        }

        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
        {
            PrintUsage();
            return 0;
        }

        string command = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "status";
        ErrorReporter.UseMessageBox = args.Contains("--msgbox");

        // Disabling persistence is just task/file cleanup - no GPU or driver needed.
        if (command.Equals("unpersist", StringComparison.OrdinalIgnoreCase))
        {
            return RunUnpersist();
        }

        try
        {
            NvApi.Initialize();
        }
        catch (Exception ex)
        {
            ErrorReporter.Report($"Could not initialize NVAPI: {ex.Message}\nAn NVIDIA driver and GPU are required.");
            return 2;
        }

        try
        {
            IntPtr[] gpus = NvApi.EnumeratePhysicalGpus();
            if (gpus.Length == 0)
            {
                ErrorReporter.Report("No NVIDIA GPUs found.");
                return 2;
            }

            // Operate on the first NVIDIA GPU; a multi-GPU selector isn't worth the surface here.
            IntPtr gpu = gpus[0];

            return command.ToLowerInvariant() switch
            {
                "status" => RunStatus(gpu),
                "undervolt" => RunUndervolt(gpu, args),
                "clear" => RunClear(gpu),
                "scan" => Diagnostics.Scan(gpu, args),
                "snapshot" => Diagnostics.Snapshot(gpu),
                "diff" => Diagnostics.Diff(gpu),
                "probe" => Diagnostics.Probe(gpu, args),
                "extent" => Diagnostics.Extent(gpu, args),
                "curve" => Diagnostics.Curve(gpu),
                "voltage" or "volt" => args.Contains("--watch")
                    ? Diagnostics.Watch(gpu)
                    : Diagnostics.Voltage(gpu),
                "watch" => Diagnostics.Watch(gpu),
                "clocks" => Diagnostics.Clocks(gpu),
                "raw" => Diagnostics.Raw(gpu, args),
                _ => UnknownCommand(command),
            };
        }
        catch (NvApiException ex)
        {
            ErrorReporter.Report($"NVAPI error: {ex.Message}");
            return 1;
        }
        finally
        {
            NvApi.Unload();
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 2;
    }

    public static string SafeName(IntPtr gpu)
    {
        try
        {
            return NvApi.GetFullName(gpu);
        }
        catch (NvApiException)
        {
            return "<unknown>";
        }
    }

    private static int RunStatus(IntPtr gpu)
    {
        var tuning = GpuTuning.Read(gpu);
        Console.WriteLine(tuning.Name);
        Console.WriteLine($"  Core curve offset: {tuning.DescribeCoreCurve()}");
        Console.WriteLine($"  Memory clock: {tuning.DescribeMemoryClock()}");
        Console.WriteLine($"  Core voltage boost: {tuning.DescribeVoltageBoost()}");
        return 0;
    }

    private static int RunClear(IntPtr gpu)
    {
        Console.WriteLine($"Resetting {SafeName(gpu)} to stock "
                          + "(core V/F curve, memory offset and voltage boost).");

        if (!Elevation.IsElevated())
        {
            Console.WriteLine("Warning: not running as Administrator - NVAPI may reject the changes.");
        }

        GpuTuning.Clear(gpu);
        return 0;
    }

    private static int RunUnpersist()
    {
        try
        {
            Console.WriteLine("Disabling startup persistence:");
            foreach (string line in Persistence.Uninstall())
            {
                Console.WriteLine($"  {line}");
            }

            return 0;
        }
        catch (NvApiException ex)
        {
            ErrorReporter.Report($"Unpersist failed: {ex.Message}");
            return 1;
        }
    }

    private static int RunUndervolt(IntPtr gpu, string[] args)
    {
        UndervoltRequest request;
        try
        {
            request = UndervoltRequest.Parse(args);
        }
        catch (NvApiException ex)
        {
            ErrorReporter.Report(ex.Message);
            return 2;
        }

        Console.WriteLine(request.DryRun
            ? $"Dry run for {SafeName(gpu)} - nothing will be written:"
            : $"Undervolting {SafeName(gpu)}:");

        if (request.Persist && request.DryRun)
        {
            Console.WriteLine("  (--persist is ignored on a dry run.)");
        }

        if (!request.DryRun && !Elevation.IsElevated())
        {
            Console.WriteLine("Warning: not running as Administrator - NVAPI may reject the changes.");
        }

        try
        {
            // Reset to stock first (silently) so offset/percentage forms and the missing peak coordinate
            // resolve against the factory curve, and the cap is applied from a clean baseline.
            if (!request.DryRun)
            {
                GpuTuning.Clear(gpu);
            }

            IReadOnlyList<(int Mv, int Mhz)> stock = GpuTuning.StockCurve(gpu);

            // Refuse to write on a GPU whose curve doesn't read back as a recognized NVIDIA table.
            // The tuning-buffer byte offsets are hardware-specific (verified only on RTX 5090 /
            // Blackwell); on a card they don't fit, the status buffer decodes as garbage, so writing
            // the memory or curve deltas would land in the wrong fields. The voltage axis stays valid
            // on a supported card even at idle, so this rejects only genuinely unrecognized cards.
            if (!request.DryRun && !GpuTuning.CurveVoltsPlausible(stock))
            {
                throw new NvApiException(
                    "the V/F curve didn't read as a recognized NVIDIA table on this GPU, so the "
                    + "tuning-buffer offsets likely don't match this hardware - refusing to write. "
                    + "See DEVELOPMENT.md (Confirming another card).");
            }

            var (capMv, targetMhz) = request.Resolve(stock);
            Console.WriteLine($"  Voltage cap: {capMv} mV");
            Console.WriteLine(targetMhz is { } f ? $"  Frequency: {f} MHz" : "  Frequency: stock clock");

            // Set memory before the curve: writing the memory clock goes through SetPstates20, which
            // makes the driver re-derive the perf table and clears the V/F-curve deltas. Applying the
            // curve last keeps it from being wiped by the memory write.
            if (request.UsesMemory)
            {
                int baseMemMhz = GpuTuning.BaseMemoryClockMhz(gpu);
                var (memMhz, memDeltaKhz) = request.ResolveMemory(baseMemMhz);
                Console.WriteLine(memDeltaKhz == 0
                    ? $"  Memory: {memMhz} MHz (stock)"
                    : $"  Memory: {memMhz} MHz ({memDeltaKhz / 1000:+0;-0} from {baseMemMhz} base)");
                if (!request.DryRun)
                {
                    GpuTuning.SetMemoryClockOffsetKhz(gpu, memDeltaKhz);
                }
            }

            foreach (string line in GpuTuning.Undervolt(gpu, stock, capMv, targetMhz, request.CapPoints, request.DryRun))
            {
                Console.WriteLine($"  {line}");
            }
        }
        catch (NvApiException ex)
        {
            ErrorReporter.Report($"Undervolt failed: {ex.Message}");
            return 1;
        }

        if (request.Persist && !request.DryRun)
        {
            try
            {
                Console.WriteLine("Persisting:");
                foreach (string line in Persistence.Install(args))
                {
                    Console.WriteLine($"  {line}");
                }
            }
            catch (NvApiException ex)
            {
                ErrorReporter.Report($"Persist failed: {ex.Message}");
                return 1;
            }
        }

        if (!request.DryRun)
        {
            Console.WriteLine(request.Persist
                ? "Done. Use 'clear' to restore stock, or 'unpersist' to stop re-applying at logon."
                : "Done. Use 'clear' to restore stock, or --persist to re-apply it at logon.");
        }

        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            simple-nvidia-undervolt - undervolt/overclock NVIDIA GPUs (and read/clear the tuning an
            MSI Afterburner profile applies) directly through NVAPI.

            Usage:
              simple-nvidia-undervolt undervolt [opts]  Cap the core voltage (by flattening the V/F
                                                        curve) and optionally set the clock at the cap.
              simple-nvidia-undervolt status            Show the core curve offset, memory clock
                                                        and voltage boost (read-only).
              simple-nvidia-undervolt watch             Poll live core voltage/clock/temp/power,
                                                        tracking the running max (Ctrl+C to stop).
              simple-nvidia-undervolt clear             Reset all tuning to the driver default.
              simple-nvidia-undervolt unpersist         Stop re-applying an undervolt at startup
                                                        (removes the --persist logon task).

            undervolt - voltage cap (required, choose one):
              --mv <n>           Cap voltage at this absolute value (mV).
              --mv-offset <n>    Cap at peak_mV + n   (n must be negative).
              --mv-pct <n>       Cap at peak_mV * (1 + n/100)  (n must be negative).
            undervolt - clock at the cap voltage (optional, choose one):
              --mhz <n>          Set this absolute frequency (MHz) at the cap.
              --mhz-offset <n>   Set peak_MHz + n.
              --mhz-pct <n>      Set peak_MHz * (1 + n/100).
              (omit to hold the stock clock at the cap voltage.)
            undervolt - memory clock (optional, choose one; relative to the factory base clock):
              --mem <n>          Set this absolute memory clock (MHz).
              --mem-offset <n>   Set base_MHz + n.
              --mem-pct <n>      Set base_MHz * (1 + n/100).
            undervolt - reference point for the offset/percentage forms (from 'watch'; one is enough,
                        the other is read off the curve):
              --peak-mv <n>      Peak core voltage under load (mV).
              --peak-mhz <n>     Peak core frequency under load (MHz).
            undervolt - other:
              --cap-points <n>   Curve anchors holding the cap's offset, counting down from the cap
                                 (cap included; default 10). A wider band keeps the clock if the boost
                                 settles a bin or two below the cap; 1 = only the cap point.
              --persist          Install to LocalAppData and re-apply this undervolt at logon, via a
                                 Task Scheduler task (which runs with --msgbox so a startup failure
                                 shows). Undo with the 'unpersist' command.
              --dry-run          Compute and print the curve changes without writing.
            Each real run resets the GPU to stock first, then applies the cap.

            Options:
              --msgbox         Also show errors in a Windows message box.
              -h, --help       Show this help.

            Notes:
              * 'undervolt' and 'clear' write to the driver and must run from an Administrator
                terminal; 'status', 'watch' and the diagnostics are read-only and do not.
              * After applying, 'undervolt' reads the effective curve back and reports whether the
                clock at the cap was reached or smoothed/limited by the driver.
              * Writes affect the live driver state only; they do not delete saved profiles, and
                everything reverts on reboot or when Afterburner re-applies a profile.

            Run 'simple-nvidia-undervolt --help-diagnostics' for the NVAPI inspection commands.
            """);
    }

    private static void PrintDiagnosticsHelp()
    {
        Console.WriteLine("""
            simple-nvidia-undervolt - diagnostics for inspecting NVAPI structs.
            All are read-only.

              curve            Dump the full live V/F curve (voltage -> frequency).
              voltage          Snapshot the live core voltage, clock, temperature and power.
              clocks           Show current/base/boost clocks for the core and memory domains.
              scan <value>     Find where a value is stored across the tuning buffers.
              snapshot / diff  Capture the buffers, then show which words a change moved.
              probe <hexId>    Find which (version, size) the driver accepts for a function.
              extent <hexId>   Measure the real struct size the driver writes for a function.
              raw <hexId> <ver> <size>  Dump the raw 32-bit words a GET writes.
            """);
    }
}

internal static class Elevation
{
    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// The parsed inputs for the <c>undervolt</c> command. The voltage cap is required; the clock at the
/// cap and the memory clock are optional. Each is given as a raw absolute value, an offset, or a
/// percentage. Core offsets/percentages are relative to the peak operating point captured under load
/// with <c>watch</c> (one of <c>--peak-mv</c> / <c>--peak-mhz</c> is required, the other read off the
/// curve); memory offsets/percentages are relative to the factory base memory clock.
/// </summary>
internal sealed class UndervoltRequest
{
    // Voltage cap (exactly one of these).
    public double? Mv { get; private init; }
    public double? MvOffset { get; private init; }
    public double? MvPct { get; private init; }

    // Core frequency target at the cap (at most one of these).
    public double? Mhz { get; private init; }
    public double? MhzOffset { get; private init; }
    public double? MhzPct { get; private init; }

    // Memory clock target (at most one of these). Offset/percentage are relative to the factory base
    // memory clock (a static value). The memory write itself needs no curve read, but it rides along
    // with the required voltage cap, which does.
    public double? Mem { get; private init; }
    public double? MemOffset { get; private init; }
    public double? MemPct { get; private init; }

    // Reference operating point (peak under load), required only for the offset/percentage forms.
    public double? PeakMv { get; private init; }
    public double? PeakMhz { get; private init; }

    /// <summary>How many curve anchors, counting down from the cap (the cap point included), hold the
    /// cap's frequency offset — a wider band keeps the clock if the boost settles below the cap.</summary>
    public int CapPoints { get; private init; }

    /// <summary>Install the app and register a logon task that re-applies this undervolt at startup.</summary>
    public bool Persist { get; private init; }

    public bool DryRun { get; private init; }

    /// <summary>Default width of the cap band (anchors holding the cap's offset, cap included).</summary>
    public const int DefaultCapPoints = 10;

    public static UndervoltRequest Parse(string[] args)
    {
        var request = new UndervoltRequest
        {
            Mv = Number(args, "--mv"),
            MvOffset = Number(args, "--mv-offset"),
            MvPct = Number(args, "--mv-pct"),
            Mhz = Number(args, "--mhz"),
            MhzOffset = Number(args, "--mhz-offset"),
            MhzPct = Number(args, "--mhz-pct"),
            Mem = Number(args, "--mem"),
            MemOffset = Number(args, "--mem-offset"),
            MemPct = Number(args, "--mem-pct"),
            PeakMv = Number(args, "--peak-mv"),
            PeakMhz = Number(args, "--peak-mhz"),
            CapPoints = Number(args, "--cap-points") is { } cp ? (int)Math.Round(cp) : DefaultCapPoints,
            Persist = args.Contains("--persist"),
            DryRun = args.Contains("--dry-run"),
        };

        int voltageForms = Count(request.Mv, request.MvOffset, request.MvPct);
        if (voltageForms == 0)
        {
            throw new NvApiException("undervolt needs a voltage cap: one of --mv, --mv-offset, --mv-pct.");
        }

        if (voltageForms > 1)
        {
            throw new NvApiException("Specify only one of --mv, --mv-offset, --mv-pct.");
        }

        if (Count(request.Mhz, request.MhzOffset, request.MhzPct) > 1)
        {
            throw new NvApiException("Specify only one of --mhz, --mhz-offset, --mhz-pct.");
        }

        if (Count(request.Mem, request.MemOffset, request.MemPct) > 1)
        {
            throw new NvApiException("Specify only one of --mem, --mem-offset, --mem-pct.");
        }

        if (request.CapPoints < 1)
        {
            throw new NvApiException("--cap-points must be at least 1 (the cap point itself).");
        }

        if (request.MvOffset is >= 0)
        {
            throw new NvApiException("--mv-offset must be negative (a voltage decrease).");
        }

        if (request.MvPct is >= 0)
        {
            throw new NvApiException("--mv-pct must be negative (a voltage decrease).");
        }

        // Offset/percentage forms are relative to the peak operating point. Only one coordinate is
        // required: the other is read off the V/F curve.
        if ((request.UsesRelativeVoltage || request.UsesRelativeFrequency)
            && request.PeakMv is null && request.PeakMhz is null)
        {
            throw new NvApiException("offset/percentage forms need a reference point: pass --peak-mv "
                                     + "or --peak-mhz (the other is read from the curve).");
        }

        return request;
    }

    private bool UsesRelativeVoltage => MvOffset is not null || MvPct is not null;
    private bool UsesRelativeFrequency => MhzOffset is not null || MhzPct is not null;

    public bool UsesMemory => Mem is not null || MemOffset is not null || MemPct is not null;

    /// <summary>Resolves the requested memory clock against the factory base into an absolute target and
    /// the kHz offset to write. Offsets and percentages are relative to <paramref name="baseMemMhz"/>.
    /// Only call when <see cref="UsesMemory"/> is true.</summary>
    public (int TargetMhz, int DeltaKhz) ResolveMemory(int baseMemMhz)
    {
        double target = Mem
            ?? (MemOffset is { } offset ? baseMemMhz + offset : baseMemMhz * (1 + MemPct!.Value / 100));
        int targetMhz = (int)Math.Round(target);
        if (targetMhz < baseMemMhz / 2 || targetMhz > baseMemMhz * 2)
        {
            throw new NvApiException(
                $"Resolved memory clock {targetMhz} MHz is implausible (base {baseMemMhz} MHz).");
        }

        return (targetMhz, (targetMhz - baseMemMhz) * 1000);
    }

    /// <summary>Resolves the request against the stock curve into an absolute cap voltage and an
    /// optional frequency target, deriving the missing peak coordinate from the curve as needed.</summary>
    public (int CapMv, int? TargetMhz) Resolve(IReadOnlyList<(int Mv, int Mhz)> stock)
    {
        double? peakMv = PeakMv;
        double? peakMhz = PeakMhz;
        if ((UsesRelativeVoltage && peakMv is null) || (UsesRelativeFrequency && peakMhz is null))
        {
            // Deriving the missing peak coordinate reads it off the curve, which needs live clocks.
            if (!GpuTuning.CurveFreqsReadable(stock))
            {
                throw new NvApiException("the GPU is idle, so the missing peak coordinate can't be read "
                    + "from the curve - pass both --peak-mv and --peak-mhz, or load the GPU and retry.");
            }

            peakMv ??= GpuTuning.VoltageAtFreq(stock, peakMhz!.Value);
            peakMhz ??= GpuTuning.FreqAtVoltage(stock, peakMv!.Value);
        }

        double mv = Mv
                    ?? (MvOffset is { } offset ? peakMv!.Value + offset : peakMv!.Value * (1 + MvPct!.Value / 100));
        int targetMv = (int)Math.Round(mv);
        if (targetMv is < 400 or > 1200)
        {
            throw new NvApiException($"Resolved max voltage {targetMv} mV is outside the plausible 400-1200 mV range.");
        }

        if (peakMv is { } pk && targetMv > pk)
        {
            throw new NvApiException($"Resolved cap voltage {targetMv} mV is above the peak {(int)pk} mV - that is not a cap.");
        }

        int? targetMhz = null;
        if (Mhz is { } rawF)
        {
            targetMhz = (int)Math.Round(rawF);
        }
        else if (MhzOffset is { } offsetF)
        {
            targetMhz = (int)Math.Round(peakMhz!.Value + offsetF);
        }
        else if (MhzPct is { } pctF)
        {
            targetMhz = (int)Math.Round(peakMhz!.Value * (1 + pctF / 100));
        }

        return (targetMv, targetMhz);
    }

    private static int Count(params double?[] values) => values.Count(v => v is not null);

    private static double? Number(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag)
            {
                if (!double.TryParse(args[i + 1], out double value))
                {
                    throw new NvApiException($"{flag} requires a numeric value.");
                }

                return value;
            }
        }

        return null;
    }
}
