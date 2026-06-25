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

        // A relay child (spawned by auto-elevation) writes its console output back to the non-elevated
        // parent through this pipe; a top-level --interactive run tees its output into a message box.
        string? pipeName = ArgValue(args, "--pipe-name");
        bool isRelayChild = pipeName is not null;
        IDisposable? childRedirect = isRelayChild ? ElevationRelay.RedirectToParent(pipeName!) : null;
        InteractiveOutput? interactive =
            InteractiveOutput.Install(isRelayChild ? InteractiveMode.Off : InteractiveOutput.ParseMode(args));

        int exit = 1;
        try
        {
            exit = Dispatch(command, args, isRelayChild);
        }
        finally
        {
            childRedirect?.Dispose();  // flush + close the pipe so the parent sees end-of-output
            interactive?.Complete(exit);
        }

        return exit;
    }

    private static int Dispatch(string command, string[] args, bool isRelayChild)
    {
        string lower = command.ToLowerInvariant();

        UndervoltRequest? request = null;
        if (lower == "undervolt")
        {
            try
            {
                request = UndervoltRequest.Parse(args);
            }
            catch (NvApiException ex)
            {
                ErrorReporter.Report(ex.Message);
                return 2;
            }

            // Saving the shortcut needs neither the GPU nor elevation, and belongs in the original
            // (non-elevated) working directory - so do it here, before any hand-off to an elevated child.
            if (request.SaveShortcut && !isRelayChild)
            {
                try
                {
                    Console.WriteLine(Shortcut.SaveUndervolt(args, request));
                }
                catch (NvApiException ex)
                {
                    ErrorReporter.Report($"Could not save the shortcut: {ex.Message}");
                    return 1;
                }
            }
        }

        // The write commands need administrator rights, as does removing the logon task ('unpersist');
        // a dry run writes nothing. When not already elevated, relaunch elevated and relay that
        // instance's output back here - unless --no-elevate, which runs in place (the privileged action
        // then likely fails, with a warning where one applies).
        bool needsElevation = lower is "clear" or "unpersist" || (lower == "undervolt" && !request!.DryRun);
        if (needsElevation && !isRelayChild && !args.Contains("--no-elevate") && !Elevation.IsElevated())
        {
            return ElevationRelay.Elevate(args);
        }

        // Disabling persistence is task/file cleanup (no GPU or driver needed); it sits after the
        // elevation gate because removing an elevated logon task can itself require admin.
        if (lower == "unpersist")
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

            return lower switch
            {
                "status" => RunStatus(gpu),
                "undervolt" => RunUndervolt(gpu, args, request!),
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

    private static string? ArgValue(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag)
            {
                return args[i + 1];
            }
        }

        return null;
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
        WarnIfNotElevated();

        try
        {
            foreach (string line in GpuTuning.Clear(gpu))
            {
                Console.WriteLine($"  {line}");
            }
        }
        catch (NvApiException ex)
        {
            ErrorReporter.Report($"Clear failed: {ex.Message}");
            return 1;
        }

        return 0;
    }

    /// <summary>A write command only reaches here non-elevated when --no-elevate suppressed the
    /// auto-elevation relay; the driver will likely reject the write, so flag it.</summary>
    private static void WarnIfNotElevated()
    {
        if (!Elevation.IsElevated())
        {
            Console.WriteLine("Warning: not running as Administrator (--no-elevate) - "
                              + "the driver may reject the changes.");
        }
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

    private static int RunUndervolt(IntPtr gpu, string[] args, UndervoltRequest request)
    {
        Console.WriteLine(request.DryRun
            ? $"Dry run for {SafeName(gpu)} - nothing will be written:"
            : $"Undervolting {SafeName(gpu)}:");

        if (!request.DryRun)
        {
            WarnIfNotElevated();
        }

        try
        {
            // Read and validate the curve before writing anything. The tuning-buffer byte offsets are
            // hardware-specific (verified only on RTX 5090 / Blackwell); on a card they don't fit, the
            // status buffer decodes as garbage, so writing would land in the wrong fields. The voltage
            // axis stays valid on a supported card even at idle, so this rejects only genuinely
            // unrecognized cards - and running it before the reset leaves such a card untouched.
            IReadOnlyList<(int Mv, int Mhz)> stock = GpuTuning.StockCurve(gpu);
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

            int? memoryDeltaKhz = null;
            if (request.UsesMemory)
            {
                int baseMemMhz = GpuTuning.BaseMemoryClockMhz(gpu);
                var (memMhz, memDelta) = request.ResolveMemory(baseMemMhz);
                memoryDeltaKhz = memDelta;
                Console.WriteLine(memDelta == 0
                    ? $"  Memory: {memMhz} MHz (stock)"
                    : $"  Memory: {memMhz} MHz ({memDelta / 1000:+0;-0} from {baseMemMhz} base)");
            }

            // Build the plan before any write: it needs a readable (3D-clocked) curve, so an idle GPU
            // fails here - before the reset/memory/curve writes - instead of after a partial apply. A
            // real run resets to stock then writes memory-then-curve inside Apply (the order the driver
            // requires); a dry run only describes the change.
            GpuTuning.CurvePlan plan = GpuTuning.BuildCurvePlan(stock, capMv, targetMhz, request.CapPoints);
            IReadOnlyList<string> log = request.DryRun
                ? GpuTuning.DescribePlan(plan, targetMhz)
                : GpuTuning.Apply(gpu, plan, targetMhz, memoryDeltaKhz);
            foreach (string line in log)
            {
                Console.WriteLine($"  {line}");
            }
        }
        catch (NvApiException ex)
        {
            ErrorReporter.Report($"Undervolt failed: {ex.Message}");
            return 1;
        }

        if (!request.DryRun && request.RenameShortcut)
        {
            foreach (string line in Shortcut.MarkActive(Shortcut.ResolveName(request)))
            {
                Console.WriteLine($"  {line}");
            }
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
                : "Done (not persisted). Use 'clear' to restore stock; omit --no-persist to re-apply at logon.");
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
                                                        (removes the logon task).

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
              --no-persist       Don't persist. By default a real undervolt installs to LocalAppData and
                                 registers a Task Scheduler logon task that re-applies it at startup (a
                                 failure shows a message box, so it isn't a silent no-op). 'unpersist'
                                 removes the task.
              --save-shortcut [name]
                                 Write a .lnk that re-applies this undervolt (with --interactive). <name>
                                 may be a name or a path (.lnk appended if missing); default is the
                                 current directory, named for these settings.
              --no-shortcut-rename
                                 Don't touch the .lnk files. By default a successful apply renames the
                                 matching link in the current directory to "[ACTIVE] ..." and clears the
                                 marker from the other marked links there.
              --no-elevate       Don't auto-elevate; run in place even without admin (the write then
                                 likely fails).
              --dry-run          Compute and print the curve changes without writing.
            Each real run resets the GPU to stock first, then applies the cap.

            Options:
              --interactive [errors]  Show the run's output in a message box when it ends (useful when
                                      launched from a shortcut, where the console closes on exit). Add
                                      'errors' to show the box only if the run fails.
              -h, --help              Show this help.

            Notes:
              * 'undervolt' and 'clear' write to the driver and 'unpersist' edits the logon task; these
                need administrator rights and prompt for elevation if run from a normal terminal.
                'status', 'watch' and the diagnostics are read-only and never elevate.
              * After applying, 'undervolt' reads the effective curve back and reports whether the
                clock at the cap was reached or smoothed/limited by the driver.
              * A real 'undervolt' persists by default: it re-applies at logon, so it survives a reboot
                unless you pass --no-persist. Writes change live driver state only; they never touch your
                saved Afterburner profiles.

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

    /// <summary>Install the app and register a logon task that re-applies this undervolt at startup.
    /// On by default for a real run; <c>--no-persist</c> turns it off.</summary>
    public bool Persist { get; private init; }

    /// <summary>Drop a <c>.lnk</c> in the current directory that re-runs this undervolt interactively.</summary>
    public bool SaveShortcut { get; private init; }

    /// <summary>An explicit name for the shortcut/active link, from <c>--save-shortcut &lt;name&gt;</c> (when
    /// saving) or the hidden <c>--shortcut-name</c> baked into a saved link (when launched from one).
    /// Null falls back to the settings-derived name.</summary>
    public string? ShortcutNameOverride { get; private init; }

    /// <summary>After a successful apply, rename the matching <c>.lnk</c> in the current directory to
    /// <c>[ACTIVE] …</c> and clear the marker from the others. On by default; <c>--no-shortcut-rename</c>
    /// turns it off.</summary>
    public bool RenameShortcut { get; private init; }

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
            Persist = !args.Contains("--no-persist"),
            SaveShortcut = args.Contains("--save-shortcut"),
            ShortcutNameOverride = OptionalValue(args, "--save-shortcut") ?? OptionalValue(args, "--shortcut-name"),
            RenameShortcut = !args.Contains("--no-shortcut-rename"),
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

        // Range-check the resolved clock like the voltage/memory targets above (a NaN/Infinity from the
        // parser collapses to 0/int.MinValue here, so this rejects those too).
        if (targetMhz is { } tMhz && tMhz is < 200 or > 4000)
        {
            throw new NvApiException(
                $"Resolved core clock {tMhz} MHz is outside the plausible 200-4000 MHz range.");
        }

        return (targetMv, targetMhz);
    }

    private static int Count(params double?[] values) => values.Count(v => v is not null);

    /// <summary>The token following <paramref name="flag"/> when it is present and not itself another
    /// flag (i.e. an optional value), else null.</summary>
    private static string? OptionalValue(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        if (i < 0 || i + 1 >= args.Length || args[i + 1].StartsWith('-'))
        {
            return null;
        }

        return args[i + 1];
    }

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
