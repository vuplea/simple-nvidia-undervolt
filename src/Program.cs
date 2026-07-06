using SimpleNvidiaUndervolt;

return Cli.Run(args);

internal static class Cli
{
    public static int Run(string[] args)
    {
        // Before any output: the installed -nocmd copy is a GUI-subsystem exe (see PeSubsystem), which
        // gets no console of its own - attach to the launching terminal's so it still prints there.
        ParentConsole.AttachIfPresent();

        // A relay child (spawned by auto-elevation) writes its console output back to the non-elevated
        // parent through pipes; taking the relay pair off the args leaves the original argv for the
        // command to parse. A top-level run without a console captures its output for a message box
        // instead (see InteractiveOutput).
        string? pipeName = ElevationRelay.TakeRelayPipeName(ref args);
        bool isRelayChild = pipeName is not null;

        // The .lnk the user double-clicked to start this run, if any: read from this process's own
        // startup info at top level, or handed across the elevation hop for a relay child (which
        // ShellExecute started, not the link). Identifies the link by its current file name, so the
        // active marking keeps working after the user renames it.
        string? launchingLnk = isRelayChild
            ? ElevationRelay.TakeLaunchingLnk(ref args)
            : Shortcut.LaunchingLnkPath();

        // One try/catch is the whole error path: a command signals failure by throwing (a CliError for
        // an anticipated problem, anything else for a bug), and this reports it once and exits 1. Setup,
        // parsing and dispatch all run inside it, so there's no second place errors are handled. Success
        // is exit 0; the elevation relay returns the elevated child's own 0/1 (its output already relayed).
        // A bare launch of the profiles zip's installer-named copy runs 'install'; its console window
        // closes with the process, so the result is forced into a message box.
        bool installerLaunch = args.Length == 0 && IsInstallerImage();

        IDisposable? childRedirect = null;
        InteractiveOutput? interactive = null;
        int exit = 1; // failure unless Dispatch completes
        try
        {
            childRedirect = pipeName is null ? null : ElevationRelay.RedirectToParent(pipeName);
            interactive = isRelayChild ? null : InteractiveOutput.Install(args, forceBox: installerLaunch);
            exit = RunTopLevel(args, isRelayChild, launchingLnk, installerLaunch);
        }
        catch (Exception ex)
        {
            // The message alone for an anticipated failure; the type and stack for a bug no path
            // anticipated (see ErrorReporter.Describe). This runs inside the tee/redirect, so it reaches
            // the message box and the relaying parent too.
            ErrorReporter.Report(ErrorReporter.Describe(ex));
        }
        finally
        {
            childRedirect?.Dispose();  // flush + close the pipes so the parent sees end-of-output
            interactive?.Complete(exit);
        }

        return exit;
    }

    /// <summary>The top-level tokens (usage, version, help) and the command dispatch. Runs inside the
    /// output capture, so without a console — the installed <c>-nocmd</c> copy double-clicked bare —
    /// the usage and version land in the message box instead of vanishing with the process.</summary>
    private static int RunTopLevel(string[] args, bool isRelayChild, string? launchingLnk,
        bool installerLaunch)
    {
        if (args.Length == 0)
        {
            if (installerLaunch)
            {
                return Dispatch(new[] { "install" }, isRelayChild, launchingLnk);
            }

            PrintUsage();
            return 0;
        }

        // Help/version are honored only as the first token: buried in a command's arguments
        // (`tune --mv 960 --version`) they would silently swallow a write command, and on a strict
        // CLI an unconsumed token is an error, not a mode switch.
        switch (args[0])
        {
            case "--version":
                Console.WriteLine($"{Product.Name} {Product.Version}");
                return 0;
            case "--help-diagnostics":
                PrintDiagnosticsHelp();
                return 0;
            case "--help" or "-h" or "/?":
                PrintUsage();
                return 0;
        }

        return Dispatch(args, isRelayChild, launchingLnk);
    }

    /// <summary>Whether this process runs from the profiles zip's installer-named copy
    /// (<c>~install-*.exe</c>): launched bare — a double-click on the extracted zip — it runs
    /// <c>install</c> instead of printing usage, so no terminal is ever needed to set the profile
    /// shortcuts up.</summary>
    private static bool IsInstallerImage()
    {
        try
        {
            return Path.GetFileName(Product.ExecutablePath())
                .StartsWith("~install", StringComparison.OrdinalIgnoreCase);
        }
        catch (CliError)
        {
            return false; // no known image path, no magic behavior
        }
    }

    private static int Dispatch(string[] args, bool isRelayChild, string? launchingLnk)
    {
        // The command is the first token; everything after it is flags and their values. A leading
        // flag means the command word was omitted - that implies 'tune', the primary verb, so
        // `simple-nvidia-undervolt --mv 960` just works.
        string command = args[0];
        if (command.StartsWith('-'))
        {
            args = args.Prepend("tune").ToArray();
            command = "tune";
        }

        // Every command validates its arguments before any elevation or driver work: a mistyped command
        // or flag is reported without needing NVAPI (or an NVIDIA GPU) at all, and never after a UAC
        // prompt. The write commands (tune, clear, install) relaunch elevated when they must (see
        // RunWriteCommand); everything else is read-only and never elevates.
        switch (command.ToLowerInvariant())
        {
            case "tune": return RunTuneCommand(args, isRelayChild, launchingLnk);

            case "install": return RunInstallCommand(args, isRelayChild);
            case "clear": return RunClearCommand(args, isRelayChild);
            case "watch": return RunWatchCommand(args);

            case "status": return RunGpuCommand(args, RunStatus);
            case "snapshot": return RunGpuCommand(args, Diagnostics.Snapshot);
            case "diff": return RunGpuCommand(args, Diagnostics.Diff);
            case "curve": return RunGpuCommand(args, Diagnostics.Curve);
            case "layout": return RunGpuCommand(args, Diagnostics.Layout);
            case "voltage": return RunGpuCommand(args, Diagnostics.Voltage);
            case "clocks": return RunGpuCommand(args, Diagnostics.Clocks);

            // The diagnostics that take free-form positional arguments (`scan 117000`,
            // `raw <id> <ver> <size>`).
            case "scan": return RunPositional(args, Diagnostics.ParseScanTarget, Diagnostics.Scan);
            case "probe": return RunPositional(args, Diagnostics.ParseProbeFunctionId, Diagnostics.Probe);
            case "extent": return RunPositional(args, Diagnostics.ParseExtentRequest, Diagnostics.Extent);
            case "raw": return RunPositional(args, Diagnostics.ParseRawRequest, Diagnostics.Raw);

            default:
                PrintUsage();
                throw new CliError($"Unknown command '{command}'.");
        }
    }

    private static int RunTuneCommand(string[] args, bool isRelayChild, string? launchingLnk)
    {
        TuneRequest request = TuneRequest.Parse(args);
        return RunWriteCommand(args, isRelayChild, request.NoElevate, () =>
        {
            OnFirstGpu(gpu => RunTune(gpu, request, launchingLnk));
        }, needsElevation: !request.DryRun);
    }

    /// <summary>'install' copies the app into Program Files without touching the GPU or registering
    /// anything — what pre-made profile shortcuts (which target the installed copy) need once. A
    /// persisting tune does the same as a side effect; this is the standalone form.</summary>
    private static int RunInstallCommand(string[] args, bool isRelayChild)
        => RunWriteCommand(args, isRelayChild, () =>
        {
            WarnIfNotElevated();
            Console.WriteLine(Persistence.InstallApp()
                ? $"Installed to {Persistence.InstallDir()}."
                : $"Already installed at {Persistence.InstallDir()}.");
            Console.WriteLine("Shortcuts targeting the installed copy (saved or profile .lnks) now work.");
        });

    private static int RunClearCommand(string[] args, bool isRelayChild)
    {
        // Not GPU-bound (no RunGpuCommand): it must remove the persisted re-apply even when the
        // NVIDIA driver or GPU is gone (see RunClear).
        return RunWriteCommand(args, isRelayChild, RunClear);
    }

    /// <summary>A write command whose only option is <c>--no-elevate</c> (install, clear); tune owns a
    /// richer option set and calls the overload below itself.</summary>
    private static int RunWriteCommand(string[] args, bool isRelayChild, Action run)
        => RunWriteCommand(args, isRelayChild,
            Args.Global.WithBare("--no-elevate").Parse(args).Has("--no-elevate"), run);

    private static int RunWatchCommand(string[] args)
    {
        Args.Parsed parsed = Args.Global.WithValue("--interval").Parse(args);

        // --silent holds all output back until the run ends, but watch IS its live output: a silent
        // watch would poll invisibly until Ctrl+C and then discard everything. Reject the combination
        // rather than run a display command that displays nothing.
        if (parsed.Has("--silent"))
        {
            throw new CliError("watch is a live display and doesn't support --silent.");
        }

        int intervalMs = Diagnostics.ParseIntervalMs(parsed);

        // Same reasoning without a console: run windowless (the -nocmd copy from a shortcut), the
        // live display has nowhere to stream - it would poll invisibly forever, with the captured
        // output only ever reaching a message box the run never ends to show.
        if (!ParentConsole.OutputVisible())
        {
            throw new CliError("watch is a live display and needs a console - run it from a terminal.");
        }

        OnFirstGpu(gpu => Diagnostics.Watch(gpu, intervalMs));
        return 0;
    }

    private static int RunWriteCommand(string[] args, bool isRelayChild, bool noElevate, Action run,
        bool needsElevation = true)
    {
        // Relaunch elevated unless this instance already runs elevated (or is the relay child of one),
        // or --no-elevate asked to run in place - the privileged action then likely fails, with a
        // warning where one applies.
        if (needsElevation && !isRelayChild && !noElevate && !Elevation.IsElevated())
        {
            return ElevationRelay.Elevate(args);
        }

        run();
        return 0;
    }

    /// <summary>A GPU-bound command that validates its arguments before touching NVAPI.</summary>
    private static int RunGpuCommand(string[] args, Action<IntPtr> run)
    {
        Args.Global.Parse(args);
        OnFirstGpu(run);
        return 0;
    }

    /// <summary>A read-only diagnostics command with free-form positional arguments: the flags are
    /// validated against the global set, then the command-specific positional arguments are parsed
    /// before NVAPI starts.</summary>
    private static int RunPositional<T>(string[] args, Func<string[], T> parse, Action<IntPtr, T> run)
    {
        string[] rest = Args.Global.Positionals(args);
        T value = parse(rest);
        OnFirstGpu(gpu => run(gpu, value));
        return 0;
    }

    /// <summary>Initializes NVAPI, runs <paramref name="run"/> against the first NVIDIA GPU (a multi-GPU
    /// selector isn't worth the surface here) and unloads. An unavailable driver or an empty enumeration
    /// throws a <see cref="CliError"/> that Run's catch reports.</summary>
    private static void OnFirstGpu(Action<IntPtr> run)
    {
        try
        {
            NvApi.Initialize();
        }
        catch (Exception ex)
        {
            throw new CliError(
                $"Could not initialize NVAPI: {ex.Message}\nAn NVIDIA driver and GPU are required.");
        }

        try
        {
            IntPtr[] gpus = NvApi.EnumeratePhysicalGpus();
            if (gpus.Length == 0)
            {
                throw new CliError("No NVIDIA GPUs found.");
            }

            run(gpus[0]);
        }
        finally
        {
            // Errors propagate to Run's catch-all, which reports the message; only unload here.
            NvApi.Unload();
        }
    }

    private static void RunStatus(IntPtr gpu)
    {
        // The Task Scheduler query spawns a slow child process and is independent of the GPU reads -
        // run it alongside them.
        Task<string> startupTask = Task.Run(Persistence.DescribeStartupTask);
        var tuning = TuningSnapshot.Read(gpu);
        Console.WriteLine(tuning.Name);
        Console.WriteLine($"  Core curve offset: {tuning.DescribeCoreCurve()}");
        Console.WriteLine($"  Memory clock: {tuning.DescribeMemoryClock()}");
        Console.WriteLine($"  Core voltage boost: {tuning.DescribeVoltageBoost()}");
        Console.WriteLine($"  Re-applies at logon: {startupTask.Result}");

        // Validate the read the same way the write path does: if the V/F curve doesn't decode as a
        // recognized table, the tuning-buffer offsets likely don't fit this GPU and the readings above
        // may be wrong. Warn (don't fail - status is read-only) and dump the detected layout for a report.
        try
        {
            if (!GpuTuning.CurveVoltsPlausible(NvApi.GetVfCurve(gpu)))
            {
                Console.WriteLine($"  Warning: {GpuTuning.UnrecognizedCurvePhrase} - the tuning-buffer "
                                  + "offsets may not match this GPU, so the readings may be wrong.");
                PrintIndented(GpuTuning.DetectedLayoutReport(gpu));
            }
        }
        catch (Exception)
        {
            // Couldn't read the curve at all; the offset readings above already reflect that.
        }
    }

    /// <summary>'clear' resets the GPU and removes the persisted re-apply, as two independent halves:
    /// an unreachable driver/GPU (the card may simply be gone) or a reset the driver rejects doesn't stop
    /// persistence being removed — the likely reason for a clear is wanting the undervolt gone, and
    /// leaving the task would just bring it back at the next logon. Both halves run; if either failed,
    /// their messages are collected and thrown together so Run reports them and exits non-zero.</summary>
    private static void RunClear()
    {
        WarnIfNotElevated();

        var failures = new List<string>();
        try
        {
            OnFirstGpu(gpu =>
            {
                Console.WriteLine($"Resetting {NvApi.SafeFullName(gpu)} to stock "
                                  + "(core V/F curve, memory offset and voltage boost).");
                PrintIndented(GpuTuning.Clear(gpu));
            });
        }
        catch (Exception ex)
        {
            failures.Add($"GPU reset failed: {ErrorReporter.Describe(ex)}\n"
                         + "Continuing to remove the logon re-apply.");
        }

        try
        {
            Console.WriteLine($"  {Persistence.RemoveLogonTask()}");
        }
        catch (Exception ex)
        {
            failures.Add($"Removing the persisted re-apply failed: {ErrorReporter.Describe(ex)}");
        }

        if (failures.Count > 0)
        {
            throw new CliError(string.Join("\n", failures));
        }
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

    private static void RunTune(IntPtr gpu, TuneRequest request, string? launchingLnk)
    {
        Console.WriteLine(request.DryRun
            ? $"Dry run for {NvApi.SafeFullName(gpu)} - nothing will be written:"
            : $"Tuning {NvApi.SafeFullName(gpu)}:");

        if (!request.DryRun)
        {
            WarnIfNotElevated();
        }

        GpuTuning.CurvePlan? plan = null;
        int? targetMhz = null;
        if (request.Mv.IsSet)
        {
            // Each real run resets to stock first - before the stock read, so the plan comes from a
            // direct, clean read; a dry run stays read-only and plans off the recovered stock curve.
            // Both refuse an unrecognized card (hardware-specific buffer offsets) before writing anything.
            IReadOnlyList<(int Mv, int Mhz)> stock = request.DryRun
                ? GpuTuning.RecoverStockReadOnly(gpu)
                : GpuTuning.ResetAndReadStock(gpu);

            // Build the plan before the memory/curve writes: it needs a clean curve read, so a collapsed
            // (transitional) read fails here instead of after a partial apply.
            int requestedCapMv;
            (requestedCapMv, targetMhz) = request.Resolve(stock);
            plan = GpuTuning.BuildCurvePlan(stock, requestedCapMv, targetMhz, request.CapPoints);
            Console.WriteLine(DescribeVoltageCap(requestedCapMv, plan.CapMv));
            Console.WriteLine(targetMhz is { } f ? $"  Frequency: {f} MHz" : "  Frequency: stock clock");
        }

        // The absolute memory clock and the delta to write. The absolute clock is also what the logon
        // task re-applies: its reference (the factory base clock) is static, so unlike the curve
        // clock it can't drift between runs (see ToPersistedArgs).
        (int TargetMhz, int DeltaKhz)? memory = null;
        if (request.Mem.IsSet)
        {
            int baseMemMhz = GpuTuning.BaseMemoryClockMhz(gpu);
            var (memMhz, memDelta) = request.ResolveMemory(baseMemMhz);
            memory = (memMhz, memDelta);
            Console.WriteLine($"  Memory: {TuningSnapshot.DescribeMemoryClock(memMhz, baseMemMhz)}");
        }

        // A real run resets to stock then writes memory-then-curve inside Apply (the order the driver
        // requires) - or, with no cap requested, just the memory offset onto the fresh reset. A dry
        // run only describes the change.
        if (plan is not null)
        {
            PrintIndented(request.DryRun
                ? GpuTuning.DescribePlan(plan, targetMhz)
                : GpuTuning.Apply(gpu, plan, targetMhz, memory?.DeltaKhz));
        }
        else if (memory is { } mem)
        {
            PrintIndented(request.DryRun
                ? new[] { $"[dry run] Would reset to stock and set the memory clock to {mem.TargetMhz} MHz." }
                : GpuTuning.ApplyMemoryOnly(gpu, mem.DeltaKhz));
        }

        // Save the shortcut only now the request has resolved and validated (and, on a real run, the
        // apply succeeded): a failed run must not leave a link behind that re-runs a command which can
        // only fail again. On a dry run the .lnk is the deliverable, so a failed save fails the run;
        // after a real apply the save and the active marking are cosmetic, so a failed .lnk write is
        // only a warning and doesn't stop the persistence below - the applied undervolt is live.
        string? savedLnk = null;
        if (request.SaveShortcut)
        {
            try
            {
                (savedLnk, string message) = Shortcut.SaveUndervolt(request);
                Console.WriteLine(message);
            }
            catch (Exception ex)
            {
                string message = $"Could not save the shortcut: {ErrorReporter.Describe(ex)}";
                if (request.DryRun)
                {
                    throw new CliError(message);
                }

                ErrorReporter.Report(message);
            }
        }

        if (request.DryRun)
        {
            return;
        }

        // The link holding the live tuning: the one this run was started from, else the one it just
        // saved - a real save writes the exact link these settings describe, so it earns the badge
        // without any name-matching. A plain terminal run has neither and touches no links.
        if ((launchingLnk ?? savedLnk) is { } liveLnk)
        {
            PrintIndented(Shortcut.MarkActive(liveLnk));
        }

        if (request.Persist)
        {
            try
            {
                if (Persistence.InstallApp())
                {
                    Console.WriteLine($"Installed to {Persistence.InstallDir()}.");
                }

                Console.WriteLine(Persistence.RegisterLogonTask(request.ToPersistedArgs(
                    plan?.CapMv, targetMhz is null ? null : plan?.CapDeltaMhz, memory?.TargetMhz)));
            }
            catch (Exception ex)
            {
                throw new CliError(
                    $"The undervolt is applied and active, but persistence failed: {ErrorReporter.Describe(ex)}\n"
                    + "It will not re-apply at logon. Re-run the command to retry persistence, or pass "
                    + "--no-persist to apply this session only.");
            }
        }

        Console.WriteLine();
        Console.WriteLine(request.Persist
            ? "Done. Use 'clear' to restore stock and stop re-applying at logon."
            : "Done (not persisted). Use 'clear' to restore stock; omit --no-persist to re-apply at logon.");
    }

    private static string DescribeVoltageCap(int requestedMv, int actualMv)
        => requestedMv == actualMv
            ? $"  Voltage cap: {actualMv} mV"
            : $"  Voltage cap: {actualMv} mV (nearest anchor to {requestedMv} mV)";

    private static void PrintIndented(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            Console.WriteLine($"  {line}");
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            simple-nvidia-undervolt - undervolt an NVIDIA GPU: talks to the driver directly - no
            background process - and caps voltage by flattening the V/F curve.

            Usage: simple-nvidia-undervolt [command] [options]

            [options]             By not specifying a verb, it is implicitly a 'tune' command: you can
                                  cap voltage (flatten the curve), set the clock at the cap, and offset
                                  the memory clock.
            install               Copy the app to Program Files, so saved or pre-made profile shortcuts
                                  work. (A persisting tune installs too; this is the standalone form.)
            status                Show curve offset, memory clock, voltage boost, and logon re-apply.
            watch                 Poll live core voltage/clock/temp/power, tracking the max
                                  (--interval <seconds> to change the 1s poll; Ctrl+C to stop).
            clear                 Reset all tuning to stock and remove logon re-apply.

            'status' and 'watch' are read-only and need no elevation. Tuning, 'clear' and 'install' need
            administrator rights; if run from a normal terminal they prompt for elevation.

            Tuning options:
            Voltage cap, pick preferred syntax (required to provide, unless tuning just the memory clock):
              --mv <n>          n mV.
              --mv-offset <n>   peak_mV + n           (n < 0).
              --mv-pct <n>      peak_mV * (1 + n/100) (n < 0).
            Clock at the cap, pick preferred syntax (omit = stock clock there):
              --mhz <n>         n MHz.
              --mhz-offset <n>  peak_MHz + n.
              --mhz-pct <n>     peak_MHz * (1 + n/100).
            Peak voltage reference, required for --mv-offset/pct and --mhz-offset/pct (read it from
            'watch' under a sustained load):
              --peak-mv <n>     Peak voltage under load (mV).
            Memory clock (optional, works alone too):
              --mem <n>         n MHz.
              --mem-offset <n>  base_MHz + n.
              --mem-pct <n>     base_MHz * (1 + n/100).
            Other:
              --cap-points <n>  Curve anchors holding the cap's offset, counting down from the cap.
                                1 = only the cap point (default 25).
              --no-persist      Don't persist; by default a real run re-applies at logon.
              --save-shortcut [name]
                                Drop a .lnk (specify name/path, otherwise auto-generated).
              --dry-run         Compute and print the curve changes without writing.
              --no-elevate      Disable auto-elevation ('clear' and 'install' accept it too).

            Options:
              --silent          No output - and no message box - unless the run fails.
                                (Not accepted by 'watch', which is a live display.)
              --version         Print the version and exit.
              -h, --help        Show this help.

            Each real run resets to stock first, then reads the curve back to confirm the write landed.
            Run without a console (a double-clicked link, the logon task), the result is shown in a
            message box. On a multi-GPU system, commands operate on the first NVIDIA GPU enumerated.
            Exit codes: 0 on success, 1 on any failure.

            Run 'simple-nvidia-undervolt --help-diagnostics' for the NVAPI inspection commands.
            """);
    }

    private static void PrintDiagnosticsHelp()
    {
        Console.WriteLine("""
            simple-nvidia-undervolt - diagnostics for inspecting NVAPI structs.
            All are read-only against the GPU.

              curve            Dump the full live V/F curve (voltage -> frequency).
              layout           Check the V/F curve buffer offsets against this card (for porting).
              voltage          Snapshot the live core voltage, clock, temperature and power.
              clocks           Show current/base/boost clocks for the core and memory domains.
              scan <value>     Find where a value is stored across the tuning buffers.
              snapshot / diff  Capture the buffers, then show which words a change moved.
              probe <hexId>    Find which (version, size) the driver accepts for a function.
              extent <hexId> <ver> <size>
                               Measure the real struct size the driver writes for a function.
              raw <hexId> <ver> <size> [maskWords]
                               Dump the raw 32-bit words a GET writes.
            """);
    }
}
