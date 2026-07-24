namespace SimpleNvidiaUndervolt;

/// <summary>
/// The saved stock V/F reference curve tuning plans from, so the same command always produces the
/// same tuning: the live curve shifts slightly with temperature (and boost state), so planning from
/// a fresh read bakes the capture conditions into the result, while planning from a saved reference
/// is reproducible. Stored as a <see cref="ReferenceCurveDoc"/> JSON file in the install
/// directory's data subfolder — the same document <c>set-reference-curve</c> exports and imports,
/// machine-wide like the tuning itself, and admin-only writable (Program Files) so nothing elevated
/// ever consumes user-writable data — keyed by <see cref="GpuIdentity"/> and cross-checked against
/// the live curve's anchor voltages (which are static per card) so a hardware or driver change is
/// noticed instead of silently planning from a stale curve.
/// </summary>
internal static class ReferenceCurve
{
    public static string FilePath() => Path.Combine(Persistence.DataDir(), "reference-curve.json");

    /// <summary>A loaded reference: the identity it was captured from (a hand-built import may name
    /// little or none of it), the stock curve, and the capture conditions (for display, so a
    /// stale-looking result can be traced to its capture).</summary>
    internal sealed record Saved(string? GpuName, string? GpuPciIds,
        IReadOnlyList<(int Mv, int Mhz)> Curve, string? SavedAt, int? TempC);

    /// <summary>The outcome of <see cref="Match"/>: the curve to plan from when the reference is
    /// usable (with a note saying so), or null with the tip/warning to print instead.</summary>
    internal sealed record MatchResult(IReadOnlyList<(int Mv, int Mhz)>? Curve, string Note);

    /// <summary>Resolves the saved reference against the live GPU for a tuning run. Never throws:
    /// any unusable state degrades to a note and a null curve, and the caller plans from a live
    /// read exactly as if nothing were saved.</summary>
    public static MatchResult Match(IntPtr gpu)
    {
        var (state, saved) = Evaluate(gpu);
        return state switch
        {
            State.Usable => new(saved!.Curve, $"Planning from the reference curve ({Describe(saved)})."),
            State.None => new(null, "Tip: run 'set-reference-curve' once (GPU idle and cool) to make "
                                    + "tuning reproducible across temperatures."),
            _ => new(null, $"Warning: {Complaint(state)} - using live curve read; "
                           + "re-run 'set-reference-curve'."),
        };
    }

    /// <summary>One line for the <c>status</c> command. Read-only and never throws.</summary>
    public static string DescribeForStatus(IntPtr gpu)
    {
        var (state, saved) = Evaluate(gpu);
        return state switch
        {
            State.None => "none (run 'set-reference-curve' for temperature-reproducible tuning)",
            State.Usable => Describe(saved!),
            _ => $"unusable - {Complaint(state)}; re-run 'set-reference-curve'",
        };
    }

    private enum State
    {
        /// <summary>Nothing saved.</summary>
        None,

        /// <summary>Saved data exists but doesn't read back as a valid reference.</summary>
        Unreadable,

        /// <summary>The identity key doesn't match the live GPU — another card.</summary>
        DifferentHardware,

        /// <summary>The identity matches but the live curve's anchor voltages moved — a driver or
        /// vBIOS change reshaped the table, so the saved frequencies no longer apply to it.</summary>
        DifferentAnchors,

        /// <summary>The live identity/curve reads failed, so the reference can't be validated.</summary>
        Unverifiable,

        Usable,
    }

    private static (State State, Saved? Saved) Evaluate(IntPtr gpu)
    {
        Saved? saved;
        try
        {
            saved = TryLoad(out bool present);
            if (saved is null)
            {
                return (present ? State.Unreadable : State.None, null);
            }
        }
        catch (Exception)
        {
            return (State.Unreadable, null);
        }

        try
        {
            // The identity fields the reference names must match; a hand-built import may name
            // little or none (the import already warned), and the anchor cross-check below guards
            // it regardless.
            if (!TuningDocuments.MatchesLive(saved.GpuName, saved.GpuPciIds, GpuIdentity.Read(gpu)))
            {
                return (State.DifferentHardware, saved);
            }

            if (!AnchorsMatch(saved.Curve, NvApi.GetVfCurve(gpu)))
            {
                return (State.DifferentAnchors, saved);
            }
        }
        catch (Exception)
        {
            return (State.Unverifiable, saved);
        }

        return (State.Usable, saved);
    }

    private static string Complaint(State state) => state switch
    {
        State.Unreadable => "the saved reference curve is unreadable",
        State.DifferentHardware => "the saved reference curve doesn't match this GPU's identity",
        State.DifferentAnchors => "the saved reference curve no longer matches this GPU's curve "
                                  + "anchors (driver or vBIOS change?)",
        _ => "the saved reference curve couldn't be verified against this GPU",
    };

    private static string Describe(Saved saved)
        => $"{TuningDocuments.DescribeCapture(saved.SavedAt)}{(saved.TempC is { } t ? $" at {t} C" : string.Empty)}";

    /// <summary>Writes the reference document, replacing any previous one. The document is a single
    /// file, so there is no torn state between the curve and the identity that keys it. Requires
    /// administrator (the install directory); the environment refusing the write is reported as the
    /// anticipated failure it is.</summary>
    public static void Save(ReferenceCurveDoc doc)
        => TuningDocuments.WriteFile(FilePath(), TuningDocuments.Render(doc), "the reference curve");

    /// <summary>The saved reference, or null. <paramref name="present"/> distinguishes nothing saved
    /// from saved-but-unusable data (a partial write, hand-edited values, another build's format):
    /// the parsed curve must also pass the same physical checks the tuning itself relies on, so
    /// file nonsense can't reach a curve plan.</summary>
    private static Saved? TryLoad(out bool present)
    {
        string json;
        try
        {
            present = File.Exists(FilePath());
            if (!present)
            {
                return null;
            }

            json = File.ReadAllText(FilePath());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
            present = true; // reached only when the file exists but can't be read
            return null;
        }

        ReferenceCurveDoc doc;
        try
        {
            doc = TuningDocuments.ParseReferenceCurveDoc(json, "the saved reference");
        }
        catch (CliError)
        {
            return null;
        }

        IReadOnlyList<(int Mv, int Mhz)> curve = TuningDocuments.Points(doc);
        if (!GpuTuning.CurveFreqsReadable(curve))
        {
            return null;
        }

        return new Saved(doc.GpuName, doc.GpuPciIds, curve, doc.SavedAt, doc.TempC);
    }

    /// <summary>Whether the reference still describes the live curve's table: same anchor count and
    /// the same voltage at every anchor. The voltage column is static per card and independent of
    /// power state and applied tuning, so on the matching card this holds at any moment; a driver
    /// or hardware change that reshaped the table fails it.</summary>
    internal static bool AnchorsMatch(IReadOnlyList<(int Mv, int Mhz)> reference,
        IReadOnlyList<(int Mv, int Mhz)> live)
    {
        if (reference.Count != live.Count)
        {
            return false;
        }

        for (int i = 0; i < reference.Count; i++)
        {
            if (reference[i].Mv != live[i].Mv)
            {
                return false;
            }
        }

        return true;
    }
}
