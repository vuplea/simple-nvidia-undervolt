using System.Globalization;

namespace SimpleNvidiaUndervolt;

/// <summary>One tuning target given as at most one of an absolute value, an offset or a percentage
/// (the relative forms resolve against a reference the caller supplies).</summary>
internal readonly record struct ValueSpec(double? Absolute, double? Offset, double? Percent)
{
    public bool IsSet => Absolute is not null || Offset is not null || Percent is not null;

    /// <summary>Whether the value needs a reference to resolve against.</summary>
    public bool IsRelative => Offset is not null || Percent is not null;

    /// <summary>Resolves to an absolute value: the raw value if given, else the offset added to (or the
    /// percentage taken of) <paramref name="reference"/>.</summary>
    public double Resolve(double? reference)
    {
        if (Absolute is { } absolute)
        {
            return absolute;
        }

        double r = reference ?? throw new InvalidOperationException("A relative value needs a reference.");
        if (Offset is { } offset)
        {
            return r + offset;
        }

        if (Percent is { } percent)
        {
            return r * (1 + percent / 100);
        }

        throw new InvalidOperationException("No value was specified.");
    }
}

/// <summary>
/// The parsed inputs for the <c>tune</c> command. At least one of the voltage cap and the memory
/// clock is required; the clock at the cap rides only with a cap. Each is given as a raw absolute
/// value, an offset, or a percentage. Core offsets/percentages are relative to the peak operating
/// point captured under load with <c>watch</c> (<c>--peak-mv</c> is required, the peak frequency
/// is read off the curve); memory offsets/percentages are relative to the factory base memory
/// clock.
/// </summary>
internal sealed class TuneRequest
{
    /// <summary>Voltage cap (required unless only the memory clock is tuned).</summary>
    public ValueSpec Mv { get; private init; }

    /// <summary>Core frequency target at the cap voltage (optional).</summary>
    public ValueSpec Mhz { get; private init; }

    /// <summary>Memory clock target (optional; works alone too). Offset/percentage are relative to
    /// the factory base memory clock (a static value), so the memory write needs no curve read.</summary>
    public ValueSpec Mem { get; private init; }

    /// <summary>Reference peak voltage under load, required only for the offset/percentage forms.</summary>
    public double? PeakMv { get; private init; }

    /// <summary>How many curve anchors, counting down from the cap (the cap point included), hold the
    /// cap's frequency offset — a wider band keeps the clock if the boost settles below the cap.</summary>
    public int CapPoints { get; private init; }

    /// <summary>Install the app and register a logon task that re-applies this undervolt at startup.
    /// On by default for a real run; <c>--no-persist</c> turns it off.</summary>
    public bool Persist { get; private init; }

    /// <summary>Write a <c>.lnk</c> that re-runs this undervolt — at <c>--save-shortcut</c>'s
    /// name/path when given, else a settings-named link in the current directory.</summary>
    public bool SaveShortcut { get; private init; }

    /// <summary>An explicit name for the shortcut/active link, from <c>--save-shortcut &lt;name&gt;</c>.
    /// Null falls back to the settings-derived name.</summary>
    public string? ShortcutNameOverride { get; private init; }

    public bool DryRun { get; private init; }

    /// <summary>Run in place without auto-elevating (<c>--no-elevate</c>).</summary>
    public bool NoElevate { get; private init; }

    /// <summary>Whether the run is <c>--silent</c> (no output and no result box unless it fails).
    /// Re-emitted into a saved shortcut's command line (see <see cref="ToShortcutArgs"/>).</summary>
    private bool Silent { get; init; }

    /// <summary>Default width of the cap band (anchors holding the cap's offset, cap included).</summary>
    public const int DefaultCapPoints = 25;

    private static readonly Args.Options Options = Args.Global
        .WithBare("--no-elevate", "--no-persist", "--dry-run")
        .WithValue(
            "--mv", "--mv-offset", "--mv-pct",
            "--mhz", "--mhz-offset", "--mhz-pct",
            "--mem", "--mem-offset", "--mem-pct",
            "--peak-mv", "--cap-points")
        .WithOptionalValue("--save-shortcut");

    public static TuneRequest Parse(string[] args)
    {
        Args.Parsed parsed = Options.Parse(args);

        var request = new TuneRequest
        {
            Mv = Spec(parsed, "--mv"),
            Mhz = Spec(parsed, "--mhz"),
            Mem = Spec(parsed, "--mem"),
            PeakMv = parsed.Number("--peak-mv"),
            CapPoints = parsed.Integer("--cap-points") ?? DefaultCapPoints,
            Persist = !parsed.Has("--no-persist"),
            SaveShortcut = parsed.Has("--save-shortcut"),
            ShortcutNameOverride = parsed.Value("--save-shortcut"),
            DryRun = parsed.Has("--dry-run"),
            NoElevate = parsed.Has("--no-elevate"),
            Silent = parsed.Has("--silent"),
        };

        if (!request.Mv.IsSet && !request.Mem.IsSet)
        {
            throw new CliError("tune needs a voltage cap (--mv/--mv-offset/--mv-pct) "
                               + "and/or a memory clock (--mem/--mem-offset/--mem-pct).");
        }

        // The core clock is set at the cap voltage, so it can't stand alone.
        if (request.Mhz.IsSet && !request.Mv.IsSet)
        {
            throw new CliError("A core clock target needs a voltage cap - the clock is set at the "
                               + "cap voltage. Add one of --mv, --mv-offset, --mv-pct.");
        }

        // ...and so the cap band has nothing to shape without a cap - on this CLI an unconsumed
        // option is an error, not a hint.
        if (!request.Mv.IsSet && parsed.Has("--cap-points"))
        {
            throw new CliError("--cap-points shapes the voltage-cap band; without a voltage cap it "
                               + "has no effect - remove it.");
        }

        if (request.CapPoints < 1)
        {
            throw new CliError("--cap-points must be at least 1 (the cap point itself).");
        }

        if (request.Mv.Offset is >= 0)
        {
            throw new CliError("--mv-offset must be negative (a voltage decrease).");
        }

        if (request.Mv.Percent is >= 0)
        {
            throw new CliError("--mv-pct must be negative (a voltage decrease).");
        }

        // Offset/percentage forms are relative to the peak operating point; the peak frequency is
        // read off the V/F curve at that voltage.
        if ((request.Mv.IsRelative || request.Mhz.IsRelative) && request.PeakMv is null)
        {
            throw new CliError("Offset/percentage forms need a reference point: pass --peak-mv.");
        }

        // ...and only those forms consume it. A peak given alongside absolute values would be accepted
        // and change nothing - on this CLI an unconsumed option is an error, not a hint.
        if (request.PeakMv is not null && !request.Mv.IsRelative && !request.Mhz.IsRelative)
        {
            throw new CliError("--peak-mv is a reference point for the offset/percentage "
                                + "forms; with absolute values it has no effect - remove it.");
        }

        return request;
    }

    /// <summary>Resolves the requested memory clock against the factory base into an absolute target and
    /// the kHz offset to write. Offsets and percentages are relative to <paramref name="baseMemMhz"/>.
    /// Only call when <see cref="Mem"/> is set.</summary>
    public (int TargetMhz, int DeltaKhz) ResolveMemory(int baseMemMhz)
    {
        int targetMhz = (int)Math.Round(Mem.Resolve(baseMemMhz));
        if (targetMhz < baseMemMhz * 3 / 4 || targetMhz > baseMemMhz * 5 / 4)
        {
            throw new CliError($"Resolved memory clock {targetMhz} MHz is implausible "
                                + $"(more than 25% from the {baseMemMhz} MHz base).");
        }

        return (targetMhz, (targetMhz - baseMemMhz) * 1000);
    }

    /// <summary>Resolves the request against the stock curve into an absolute cap voltage and an
    /// optional frequency target, deriving the peak frequency from the curve as needed.</summary>
    public (int CapMv, int? TargetMhz) Resolve(IReadOnlyList<(int Mv, int Mhz)> stock)
    {
        double? peakMhz = null;
        if (Mhz.IsRelative)
        {
            // Deriving the peak frequency reads it off the curve, which needs a clean read.
            if (!GpuTuning.CurveFreqsReadable(stock))
            {
                throw new CliError($"The V/F curve {GpuTuning.TransientReadMarker}, so the peak "
                    + "frequency can't be derived - retry in a moment, or pass an absolute --mhz.");
            }

            peakMhz = GpuTuning.FreqAtVoltage(stock, PeakMv!.Value);
        }

        int targetMv = (int)Math.Round(Mv.Resolve(PeakMv));
        if (targetMv is < 400 or > 1200)
        {
            throw new CliError($"Resolved cap voltage {targetMv} mV is outside the plausible 400-1200 mV range.");
        }

        if (PeakMv is { } peak && targetMv > peak)
        {
            throw new CliError($"Resolved cap voltage {targetMv} mV is above the peak {(int)peak} mV - that is not a cap.");
        }

        // The plausible-range check only catches physically absurd voltages. The curve's own anchors are
        // the voltages the driver can actually target, so a cap outside that span would be silently
        // snapped into a different request.
        if (targetMv < stock[0].Mv)
        {
            throw new CliError($"Resolved cap voltage {targetMv} mV is below the curve's lowest anchor "
                                + $"({stock[0].Mv} mV) - this GPU cannot cap that low.");
        }

        if (targetMv > stock[^1].Mv)
        {
            throw new CliError($"Resolved cap voltage {targetMv} mV is above the curve's highest anchor "
                                + $"({stock[^1].Mv} mV) - that is not a cap.");
        }

        int? targetMhz = Mhz.IsSet ? (int)Math.Round(Mhz.Resolve(peakMhz)) : null;

        // Range-check the resolved clock like the voltage/memory targets above.
        if (targetMhz is { } tMhz && tMhz is < 200 or > 4000)
        {
            throw new CliError(
                $"Resolved core clock {tMhz} MHz is outside the plausible 200-4000 MHz range.");
        }

        return (targetMv, targetMhz);
    }

    /// <summary>The fully-resolved command line to persist for the logon re-apply: the absolute values
    /// actually applied, so the task reproduces this exact tuning without re-reading the (idle) curve
    /// or depending on a peak reference. Every offset/percentage and peak flag collapses into a plain
    /// <c>--mv</c>/<c>--mhz</c>/<c>--mem</c> here (<paramref name="capMv"/> is null on a memory-only
    /// run); <c>--cap-points</c> rides along only when non-default. No command word: a leading option
    /// implies <c>tune</c>, so the generated line is exactly what a user would type.</summary>
    public string[] ToAbsoluteArgs(int? capMv, int? targetMhz, int? memMhz)
    {
        var args = new List<string>();
        if (capMv is { } mv)
        {
            args.Add("--mv");
            args.Add(Invariant(mv));
        }

        if (targetMhz is { } f)
        {
            args.Add("--mhz");
            args.Add(Invariant(f));
        }

        if (memMhz is { } m)
        {
            args.Add("--mem");
            args.Add(Invariant(m));
        }

        AddCapPoints(args);
        return args.ToArray();
    }

    /// <summary>The command line a saved shortcut re-runs: this request's own settings re-emitted (the
    /// relative forms and peak reference intact — the link reproduces the command, not one resolution
    /// of it), minus the flags that must not ride along (<c>--dry-run</c>, <c>--save-shortcut</c>, and
    /// <c>--no-elevate</c> — a double-click must elevate, or every click fails against the driver).
    /// A click shows its result in a message box automatically (the <c>-nocmd</c> copy has no
    /// console), so no output flag is baked; <c>--silent</c> rides along when this run had it. The
    /// link's own identity is deliberately not baked in either: a click finds the launching link
    /// through the process startup info, so the user can rename the file freely.</summary>
    public string[] ToShortcutArgs()
    {
        var args = new List<string>(); // no command word - a leading option implies 'tune'
        AddSpec(args, "--mv", Mv);
        AddSpec(args, "--mhz", Mhz);
        AddSpec(args, "--mem", Mem);
        AddNumber(args, "--peak-mv", PeakMv);
        AddCapPoints(args);
        if (!Persist)
        {
            args.Add("--no-persist");
        }

        if (Silent)
        {
            args.Add("--silent");
        }

        return args.ToArray();
    }

    private void AddCapPoints(List<string> args)
    {
        if (CapPoints != DefaultCapPoints)
        {
            args.Add("--cap-points");
            args.Add(Invariant(CapPoints));
        }
    }

    private static void AddSpec(List<string> args, string flag, ValueSpec spec)
    {
        AddNumber(args, flag, spec.Absolute);
        AddNumber(args, $"{flag}-offset", spec.Offset);
        AddNumber(args, $"{flag}-pct", spec.Percent);
    }

    private static void AddNumber(List<string> args, string flag, double? value)
    {
        if (value is { } v)
        {
            args.Add(flag);
            args.Add(v.ToString("R", CultureInfo.InvariantCulture)); // "R" round-trips what the user typed
        }
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads one absolute/offset/percentage triplet (e.g. <c>--mv</c> / <c>--mv-offset</c> /
    /// <c>--mv-pct</c>), rejecting more than one form of the same value.</summary>
    private static ValueSpec Spec(Args.Parsed args, string flag)
    {
        var spec = new ValueSpec(args.Number(flag), args.Number($"{flag}-offset"), args.Number($"{flag}-pct"));
        int forms = (spec.Absolute is null ? 0 : 1)
                    + (spec.Offset is null ? 0 : 1)
                    + (spec.Percent is null ? 0 : 1);
        if (forms > 1)
        {
            throw new CliError($"Specify only one of {flag}, {flag}-offset, {flag}-pct.");
        }

        return spec;
    }
}
