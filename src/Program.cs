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
            case "set-reference-curve": return RunSetReferenceCommand(args, isRelayChild);
            case "watch": return RunWatchCommand(args);

            case "status": return RunStatusCommand(args);
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

    /// <summary>A write command whose only option is <c>--no-elevate</c> (install, clear);
    /// tune and set-reference-curve own richer option sets and call the overload below themselves.</summary>
    private static int RunWriteCommand(string[] args, bool isRelayChild, Action run)
        => RunWriteCommand(args, isRelayChild,
            Args.Global.WithBare("--no-elevate").Parse(args).Has("--no-elevate"), run);

    /// <summary>'set-reference-curve': saves the stock V/F curve as the tuning reference — captured
    /// from the card, or imported from a previously exported curve file — optionally exporting the
    /// result.</summary>
    private static int RunSetReferenceCommand(string[] args, bool isRelayChild)
    {
        Args.Parsed parsed = Args.Global.WithBare("--no-elevate")
            .WithValue("--in-curve-file", "--out-curve-file").Parse(args);
        string? inFile = parsed.FilePath("--in-curve-file");
        string? outFile = parsed.FilePath("--out-curve-file");
        return RunWriteCommand(args, isRelayChild, parsed.Has("--no-elevate"),
            () => OnFirstGpu(gpu => RunSetReference(gpu, inFile, outFile)));
    }

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

    /// <summary>'status', plus its optional applied-tuning export. Read-only against the GPU (the
    /// export writes a user file), so it never elevates.</summary>
    private static int RunStatusCommand(string[] args)
    {
        Args.Parsed parsed = Args.Global.WithValue("--out-tuning-file").Parse(args);
        string? outTuningFile = parsed.FilePath("--out-tuning-file");
        OnFirstGpu(gpu => RunStatus(gpu, outTuningFile));
        return 0;
    }

    private static void RunStatus(IntPtr gpu, string? outTuningFile)
    {
        // The Task Scheduler query spawns a slow child process and is independent of the GPU reads -
        // run it alongside them.
        Task<string> startupTask = Task.Run(Persistence.DescribeStartupTask);
        var tuning = TuningSnapshot.Read(gpu);
        Console.WriteLine(tuning.Name);
        Console.WriteLine($"  Core curve offset: {tuning.DescribeCoreCurve()}");
        Console.WriteLine($"  Memory clock: {tuning.DescribeMemoryClock()}");
        Console.WriteLine($"  Core voltage boost: {tuning.DescribeVoltageBoost()}");
        Console.WriteLine($"  Reference curve: {ReferenceCurve.DescribeForStatus(gpu)}");
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

        // The export comes after the report, so a file the environment refuses doesn't hide it.
        if (outTuningFile is not null)
        {
            foreach (string line in ExportAppliedTuning(gpu, outTuningFile))
            {
                Console.WriteLine($"  {line}");
            }
        }
    }

    /// <summary>Exports the applied tuning — the knobs this tool tunes (see
    /// <see cref="AppliedTuning"/>), each tuned anchor named by its voltage on the stock curve: the
    /// saved reference when it is usable for this card, else the stock curve recovered
    /// live-minus-deltas. The file re-applies with 'tune --in-tuning-file'.</summary>
    private static IReadOnlyList<string> ExportAppliedTuning(IntPtr gpu, string path)
    {
        IReadOnlyList<(int Mv, int Mhz)> live = NvApi.GetVfCurve(gpu);
        if (!GpuTuning.CurveVoltsPlausible(live))
        {
            throw GpuTuning.UnrecognizedCurveError(gpu, "export the applied tuning");
        }

        AppliedTuning applied = AppliedTuning.Read(gpu);
        IReadOnlyList<(int Mv, int Mhz)> stock = ReferenceCurve.Match(gpu).Curve
                                                 ?? GpuTuning.SubtractDeltas(live, applied.CurveDeltasKhz);

        TuningDoc doc = TuningDocuments.MakeTuningDoc(GpuIdentity.Read(gpu), stock, applied);
        var log = new List<string>();
        if (applied.IsStock)
        {
            log.Add("Note: the applied tuning is stock - the export holds no offsets.");
        }

        log.Add(TuningDocuments.WriteFile(path, TuningDocuments.Render(doc), "the applied tuning")
                + " Re-apply it with 'tune --in-tuning-file'.");
        return log;
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
            if (PersistedTuning.Remove())
            {
                Console.WriteLine("  Removed the persisted tuning file.");
            }
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

    /// <summary>'set-reference-curve' saves the stock V/F curve keyed to this GPU (see
    /// <see cref="ReferenceCurve"/>); tuning then plans from it instead of a live read, so the
    /// same command always produces the same tuning. The curve comes from a capture — an applied
    /// tuning is reset for it and restored right after (see
    /// <see cref="GpuTuning.CaptureStockForReference"/>); with the GPU writes that involves, plus
    /// the install-directory write, the command elevates like the other write commands — or, with
    /// <c>--in-curve-file</c>, from a previously exported file, validated against the live card
    /// here so a wrong file fails now rather than as a silent live-planning fallback at tune time.
    /// <c>--out-curve-file</c> additionally exports the document just stored.</summary>
    private static void RunSetReference(IntPtr gpu, string? inFile, string? outFile)
    {
        Console.WriteLine($"Setting the reference curve for {NvApi.SafeFullName(gpu)}:");
        WarnIfNotElevated();

        ReferenceCurveDoc doc = inFile is null ? CaptureReference(gpu, outFile) : ImportReference(gpu, inFile);
        Console.WriteLine("  Tuning now plans from this curve; re-run 'set-reference-curve' to refresh it.");

        if (outFile is not null)
        {
            Console.WriteLine($"  {TuningDocuments.WriteFile(outFile, TuningDocuments.Render(doc), "the reference curve")}");
        }
    }

    /// <summary>Captures the stock curve from the card and saves it as the reference.</summary>
    private static ReferenceCurveDoc CaptureReference(IntPtr gpu, string? outFile)
    {
        // Read the identity before the capture: the capture can reset the tuning and fail to put it
        // back, and a later step throwing must not be how the user finds out - it would report the
        // save or identity error alone and never mention the tuning it lost.
        GpuIdentity identity = GpuIdentity.Read(gpu);
        GpuTuning.ReferenceCapture capture = GpuTuning.CaptureStockForReference(gpu);
        PrintIndented(capture.Log);

        ReferenceCurveDoc doc;
        try
        {
            int? tempC = TryReadTemperatureC(gpu);
            doc = TuningDocuments.MakeReferenceCurveDoc(identity, capture.Stock, tempC);
            ReferenceCurve.Save(doc);
            Console.WriteLine($"  Saved {capture.Stock.Count} stock points"
                              + $"{(tempC is { } t ? $", captured at {t} C" : string.Empty)}.");
        }
        catch (Exception ex) when (capture.RestoreFailure is not null)
        {
            throw new CliError($"{ErrorReporter.Describe(ex)}\nAlso, {capture.RestoreFailure}. "
                               + "Re-run your tune command or profile shortcut.");
        }

        // The reference itself is good (it is stock data, unaffected by the restore), so it stays
        // saved - but the run must still fail loudly: the user's tuning is no longer applied. The
        // requested export is skipped; re-running the command after the re-tune produces it.
        if (capture.RestoreFailure is { } failure)
        {
            throw new CliError($"The reference was saved{(outFile is null ? string.Empty : " (but not exported)")}, "
                               + $"but {failure}.\nRe-run your tune command or profile shortcut.");
        }

        return doc;
    }

    /// <summary>Saves a previously exported curve file as this machine's reference, validated
    /// against the live card here — a usable frequency column and a match on identity and anchors —
    /// so a wrong file fails now rather than as a silent live-planning fallback at tune time.</summary>
    private static ReferenceCurveDoc ImportReference(IntPtr gpu, string inFile)
    {
        ReferenceCurveDoc doc = TuningDocuments.ReadReferenceCurveFile(inFile);
        IReadOnlyList<(int Mv, int Mhz)> points = TuningDocuments.Points(doc);
        if (!GpuTuning.CurveFreqsReadable(points))
        {
            throw new CliError($"{inFile}: the curve's frequency column isn't usable - re-export it.");
        }

        if (TuningDocuments.RequireMatchesGpu(doc.GpuName, doc.GpuPciIds, gpu,
                $"The curve file {inFile}") is { } warning)
        {
            Console.WriteLine($"  {warning}");
        }

        if (!ReferenceCurve.AnchorsMatch(points, NvApi.GetVfCurve(gpu)))
        {
            throw new CliError($"The curve file {inFile} doesn't match this GPU's curve anchors "
                               + "(driver or vBIOS change since the capture?) - re-export it.");
        }

        ReferenceCurve.Save(doc);
        Console.WriteLine($"  Reference set from {inFile} ({points.Count} stock points, "
                          + $"{TuningDocuments.DescribeCapture(doc.SavedAt)}).");
        return doc;
    }

    /// <summary>Best-effort: the temperature is a capture-condition detail recorded with the
    /// reference, not a requirement of it.</summary>
    private static int? TryReadTemperatureC(IntPtr gpu)
    {
        try
        {
            return NvApi.GetCoreTemperatureC(gpu);
        }
        catch (Exception)
        {
            return null;
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
        if (request.IsReplay)
        {
            RunReplay(gpu, request);
            return;
        }

        PrintRunHeader(gpu, request);
        if (!request.DryRun)
        {
            WarnIfNotElevated();
        }

        GpuTuning.CurvePlan? plan = null;
        int? targetMhz = null;
        IReadOnlyList<(int Mv, int Mhz)>? planStock = null;
        IReadOnlyList<(int Mv, int Mhz)>? referenceBaseline = null;
        if (request.Mv.IsSet)
        {
            // Plan from the saved reference curve when one matches this GPU - the live curve shifts
            // with temperature, so only a fixed reference makes the same command reproduce the same
            // tuning; safety still rides on live reads (Clear's recognized-curve guard, the
            // post-write confirmation). Otherwise each real run resets to stock first - before the
            // stock read, so the plan comes from a direct, clean read; a dry run stays read-only and
            // plans off the recovered stock curve. All paths refuse an unrecognized card
            // (hardware-specific buffer offsets) before writing anything.
            ReferenceCurve.MatchResult reference = ReferenceCurve.Match(gpu);
            Console.WriteLine($"  {reference.Note}");

            IReadOnlyList<(int Mv, int Mhz)> stock;
            if (reference.Curve is { } referenceStock)
            {
                stock = referenceStock;
                if (!request.DryRun)
                {
                    // A real run still starts from a fresh stock reset. The live curve that reset
                    // exposes is kept as the write-verification baseline: the plan's frequencies come
                    // from the reference, so judging the read-back against them would let the thermal
                    // shift between the two pass for a landed write (see VerifyWriteReachedCurve).
                    referenceBaseline = GpuTuning.ResetAndReadVerificationBaseline(gpu);
                }
            }
            else
            {
                stock = request.DryRun
                    ? GpuTuning.RecoverStockReadOnly(gpu)
                    : GpuTuning.ResetAndReadStock(gpu);
            }

            // Build the plan before the memory/curve writes: it needs a clean curve read, so a collapsed
            // (transitional) read fails here instead of after a partial apply.
            planStock = stock;
            int requestedCapMv;
            (requestedCapMv, targetMhz) = request.Resolve(stock);
            plan = GpuTuning.BuildCurvePlan(stock, requestedCapMv, targetMhz, request.CapPoints);
            Console.WriteLine(DescribeVoltageCap(requestedCapMv, plan.CapMv));
            Console.WriteLine(targetMhz is { } f ? $"  Frequency: {f} MHz" : "  Frequency: stock clock");
        }

        // The absolute memory clock (what the run reports) and the delta to write - the delta is
        // also what the tuning document carries.
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
                : GpuTuning.Apply(gpu, plan, planStock!, targetMhz, memory?.DeltaKhz, referenceBaseline));
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

        // The run's resolved tuning as a document - what persistence stores and --out-tuning-file
        // writes. Built once; the plan's stock curve names the tuned anchors, and a memory-only run
        // has none to name, so it needs no curve at all.
        TuningDoc? runDoc = null;
        TuningDoc RunDoc() => runDoc ??= TuningDocuments.MakeTuningDoc(GpuIdentity.Read(gpu),
            planStock ?? Array.Empty<(int Mv, int Mhz)>(),
            new AppliedTuning(plan?.DeltasKhz ?? Array.Empty<int>(), memory?.DeltaKhz ?? 0));

        if (request.DryRun)
        {
            ExportTuningFile(request, RunDoc);
            return;
        }

        // The link holding the live tuning: the one this run was started from, else the one it just
        // saved - a real save writes the exact link these settings describe, so it earns the badge
        // without any name-matching. A plain terminal run has neither and touches no links.
        if ((launchingLnk ?? savedLnk) is { } liveLnk)
        {
            PrintIndented(Shortcut.MarkActive(liveLnk));
        }

        FinishRealRun(request, RunDoc);
    }

    private static void PrintRunHeader(IntPtr gpu, TuneRequest request)
        => Console.WriteLine(request.DryRun
            ? $"Dry run for {NvApi.SafeFullName(gpu)} - nothing will be written:"
            : $"Tuning {NvApi.SafeFullName(gpu)}:");

    /// <summary>Writes the run's tuning document to <c>--out-tuning-file</c> when asked. The export
    /// mirrors the shortcut: on a dry run the file is the deliverable, after a real apply it records
    /// the exact tuning that landed.</summary>
    private static void ExportTuningFile(TuneRequest request, Func<TuningDoc> doc)
    {
        if (request.OutTuningFile is { } outFile)
        {
            Console.WriteLine(TuningDocuments.WriteFile(outFile, TuningDocuments.Render(doc()), "the tuning"));
        }
    }

    /// <summary>A real run's shared tail — persist, export, sign off — for the planned and replay
    /// paths alike. <paramref name="doc"/> is a factory so a run that neither persists nor exports
    /// never builds the document.</summary>
    private static void FinishRealRun(TuneRequest request, Func<TuningDoc> doc)
    {
        if (request.Persist)
        {
            PersistTuning(doc());
        }

        ExportTuningFile(request, doc);

        Console.WriteLine();
        Console.WriteLine(request.Persist
            ? "Done. Use 'clear' to restore stock and stop re-applying at logon."
            : "Done (not persisted). Use 'clear' to restore stock; omit --no-persist to re-apply at logon.");
    }

    /// <summary>Persists a tuning document for the logon re-apply: installs the app, stores the
    /// document as a file beside it (see <see cref="PersistedTuning"/>) and registers the logon
    /// task that applies it. A failure here must not read as a failed tuning — the caller's apply
    /// is live — so it reports as its own anticipated error with the retry guidance.</summary>
    private static void PersistTuning(TuningDoc doc)
    {
        try
        {
            if (Persistence.InstallApp())
            {
                Console.WriteLine($"Installed to {Persistence.InstallDir()}.");
            }

            PersistedTuning.Save(doc);
            Console.WriteLine($"Saved the tuning to apply at logon: {PersistedTuning.FilePath()}");
            Console.WriteLine(Persistence.RegisterLogonTask());
        }
        catch (Exception ex)
        {
            // "May": a still-registered task from an earlier persist re-applies whatever the store
            // holds now, so neither "will" nor "won't" would be honest across the partial states.
            throw new CliError(
                $"The tuning is applied and active, but persistence failed: {ErrorReporter.Describe(ex)}\n"
                + "The logon re-apply may be missing or stale. Re-run the command to retry "
                + "persistence, or pass --no-persist to apply this session only.");
        }
    }

    /// <summary>A replay run: re-applies a stored tuning document — a file the user named, or the
    /// persisted tuning file the logon task consumes — offsets as data, no planning. The document
    /// is validated against the live card first — the identity fields it names, and every tuned
    /// anchor's voltage against the live table — so a foreign or stale one fails as a named error
    /// before anything is written.</summary>
    private static void RunReplay(IntPtr gpu, TuneRequest request)
    {
        PrintRunHeader(gpu, request);

        (TuningDoc doc, string source) = request.InTuningFile is { } file
            ? (TuningDocuments.ReadTuningFile(file), $"the tuning file {file}")
            : (PersistedTuning.Load(), $"the persisted tuning ({PersistedTuning.FilePath()})");
        Console.WriteLine($"  Re-applying the tuning from {source} "
                          + $"({TuningDocuments.DescribeCapture(doc.SavedAt)}).");

        string what = $"The tuning from {source}";
        if (TuningDocuments.RequireMatchesGpu(doc.GpuName, doc.GpuPciIds, gpu, what) is { } warning)
        {
            Console.WriteLine($"  {warning}");
        }

        // The write path refuses an unrecognized card on its own; checking here puts the refusal
        // before any output that reads like progress. The live read also backs the anchor and
        // absolute-clock resolution below - the voltage column is valid at any power state.
        IReadOnlyList<(int Mv, int Mhz)> live = NvApi.GetVfCurve(gpu);
        if (!GpuTuning.CurveVoltsPlausible(live))
        {
            throw GpuTuning.UnrecognizedCurveError(gpu, "apply the tuning");
        }

        // The same plausibility bound the planned path puts on a requested memory clock - a replay
        // must not write a memory offset no plan could have produced.
        if (doc.MemoryOffset != 0)
        {
            int baseMemMhz = GpuTuning.BaseMemoryClockMhz(gpu);
            TuneRequest.RequirePlausibleMemoryClock(baseMemMhz + (long)doc.MemoryOffset, baseMemMhz);
        }

        ReferenceCurve.MatchResult reference = ReferenceCurve.Match(gpu);

        // The reference state matters to a replay only when an anchor must resolve its offset from
        // an absolute clock; the common all-offsets replay stays quiet about it.
        if (doc.Curve!.Any(e => e.Offset is null))
        {
            Console.WriteLine($"  {reference.Note}");
        }

        PrintIndented(DescribeTuningDoc(doc));

        if (request.DryRun)
        {
            // Resolving against the live table validates every anchor and, for absolute-clock
            // entries, the offset derivation - read-only, like the rest of the dry run. A real run
            // re-resolves against the clean post-reset read inside ApplyExact.
            TuningDocuments.ResolveCurveOffsetsKhz(doc, live,
                () => reference.Curve ?? GpuTuning.RecoverStockReadOnly(gpu), what);
            Console.WriteLine("  [dry run] Would reset to stock and write these offsets.");
            ExportTuningFile(request, () => doc);
            return;
        }

        WarnIfNotElevated();
        PrintIndented(GpuTuning.ApplyExact(gpu, doc, reference.Curve, live, what));
        FinishRealRun(request, () => doc);
    }

    /// <summary>The document's tuning, summarized before the apply the way status renders an
    /// applied tuning, so the user sees what the replay writes.</summary>
    private static IReadOnlyList<string> DescribeTuningDoc(TuningDoc doc)
    {
        var lines = new List<string>();
        int[] offsetsKhz = doc.Curve!.Where(e => e.Offset is not null)
            .Select(e => e.Offset!.Value * 1000).ToArray();
        if (offsetsKhz.Length > 0)
        {
            lines.Add($"Core curve offset: {TuningSnapshot.DescribeOffsetsRange(offsetsKhz)}");
        }

        int absoluteClocks = doc.Curve!.Count(e => e.Offset is null);
        if (absoluteClocks > 0)
        {
            lines.Add($"Core curve: {absoluteClocks} anchor(s) at absolute clocks, offsets resolved "
                      + "against the stock curve at apply.");
        }

        if (doc.MemoryOffset != 0)
        {
            lines.Add($"Memory clock offset: {doc.MemoryOffset:+0;-0} MHz");
        }

        if (lines.Count == 0)
        {
            lines.Add("The exported tuning is stock - replaying it resets everything to stock.");
        }

        return lines;
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
                                  --out-tuning-file <f> exports the applied tuning as JSON - the
                                  tuned curve anchors, keyed by voltage, plus the memory offset -
                                  re-appliable with 'tune --in-tuning-file'.
            watch                 Poll live core voltage/clock/temp/power, tracking the max
                                  (--interval <seconds> to change the 1s poll; Ctrl+C to stop).
            clear                 Reset all tuning to stock and remove logon re-apply.
            set-reference-curve   Save the stock V/F curve (best captured idle and cool) as the tuning
                                  reference: tuning then plans from it instead of the live curve, whose
                                  thermal shift otherwise drifts the result between runs. An applied
                                  tuning is reset for the capture and restored right after.
                                  --in-curve-file <f> sets it from an exported file instead of
                                  capturing; --out-curve-file <f> also exports it as JSON.

            'status' and 'watch' are read-only and need no elevation. Tuning, 'clear', 'install' and
            'set-reference-curve' need administrator rights; if run from a normal terminal they prompt
            for elevation.

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
              --in-tuning-file <f>
                                Re-apply an exported tuning file exactly (excludes the other tuning
                                options).
              --out-tuning-file <f>
                                Also export the run's tuning as JSON; with --dry-run, export
                                without applying.
              --no-persist      Don't persist; by default a real run re-applies at logon.
              --save-shortcut [name]
                                Drop a .lnk (specify name/path, otherwise auto-generated).
              --dry-run         Compute and print the curve changes without writing.
              --no-elevate      Disable auto-elevation ('clear', 'install' and 'set-reference-curve'
                                accept it too).

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
