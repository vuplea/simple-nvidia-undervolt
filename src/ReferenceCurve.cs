using System.Globalization;
using Microsoft.Win32;

namespace SimpleNvidiaUndervolt;

/// <summary>What identifies the GPU a reference curve belongs to: the full name plus the PCI
/// identifiers pin the exact card model, and the board serial (when the driver reports one) pins the
/// physical unit — the stock V/F curve comes from per-chip factory binning, so another unit of the
/// same model has a different curve.</summary>
internal sealed record GpuIdentity(string Name, string PciIds, string? BoardSerial)
{
    public static GpuIdentity Read(IntPtr gpu)
    {
        var (device, subSystem, revision, extDevice) = NvApi.GetPciIdentifiers(gpu);
        return new(
            NvApi.SafeFullName(gpu),
            $"{device:X8}-{subSystem:X8}-{revision:X8}-{extDevice:X8}",
            NvApi.TryGetBoardSerial(gpu));
    }

    /// <summary>Whether both identities describe the same card. The serial is best-effort (a driver
    /// update may stop reporting it), so it only discriminates when both sides have one.</summary>
    public bool Matches(GpuIdentity other)
        => Name == other.Name && PciIds == other.PciIds
           && (BoardSerial is null || other.BoardSerial is null || BoardSerial == other.BoardSerial);
}

/// <summary>
/// The saved stock V/F reference curve tuning plans from, so the same command always produces the
/// same tuning: the live curve shifts slightly with temperature (and boost state), so planning from
/// a fresh read bakes the capture conditions into the result, while planning from a saved reference
/// is reproducible. Stored in HKLM — machine-wide like the tuning itself, and admin-only writable so
/// the elevated logon re-apply never consumes user-writable data — keyed by <see cref="GpuIdentity"/>
/// and cross-checked against the live curve's anchor voltages (which are static per card) so a
/// hardware or driver change is noticed instead of silently planning from a stale curve.
/// </summary>
internal static class ReferenceCurve
{
    /// <summary>The HKLM key holding the reference. Internal so the e2e suite can back up and restore
    /// the machine's own reference around tests that overwrite it.</summary>
    internal const string KeyPath = @"SOFTWARE\" + Product.Name + @"\ReferenceCurve";

    /// <summary>A loaded reference: the identity it was captured from, the stock curve, and the
    /// capture conditions (for display, so a stale-looking result can be traced to its capture).</summary>
    internal sealed record Saved(GpuIdentity Gpu, IReadOnlyList<(int Mv, int Mhz)> Curve,
        string SavedAt, int? TempC);

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
            State.None => new(null, "Tip: run 'save-reference' once (GPU idle and cool) to make "
                                    + "tuning reproducible across temperatures."),
            _ => new(null, $"Warning: {Complaint(state)} - using live curve read; "
                           + "re-run 'save-reference'."),
        };
    }

    /// <summary>One line for the <c>status</c> command. Read-only and never throws.</summary>
    public static string DescribeForStatus(IntPtr gpu)
    {
        var (state, saved) = Evaluate(gpu);
        return state switch
        {
            State.None => "none (run 'save-reference' for temperature-reproducible tuning)",
            State.Usable => Describe(saved!),
            _ => $"unusable - {Complaint(state)}; re-run 'save-reference'",
        };
    }

    private enum State
    {
        /// <summary>Nothing saved.</summary>
        None,

        /// <summary>Saved data exists but doesn't read back as a valid reference.</summary>
        Unreadable,

        /// <summary>The identity key doesn't match the live GPU.</summary>
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
            if (!saved.Gpu.Matches(GpuIdentity.Read(gpu)))
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
        State.DifferentHardware => "the saved reference curve is from different GPU hardware",
        State.DifferentAnchors => "the saved reference curve no longer matches this GPU's curve "
                                  + "anchors (driver or vBIOS change?)",
        _ => "the saved reference curve couldn't be verified against this GPU",
    };

    private static string Describe(Saved saved)
        => $"saved {saved.SavedAt}{(saved.TempC is { } t ? $" at {t} C" : string.Empty)}";

    /// <summary>Writes the reference, replacing any previous one. Requires administrator (HKLM);
    /// the environment refusing the write is reported as the anticipated failure it is.
    /// The curve goes in before the identity that keys it, so a write interrupted between the two
    /// leaves the previous identity guarding the new curve: on the same card that pairing is still
    /// correct, and on another one it fails the identity check and falls back to a live read. The
    /// reverse order would leave the new card's identity vouching for the old card's curve.</summary>
    public static void Save(GpuIdentity gpu, IReadOnlyList<(int Mv, int Mhz)> curve, int? tempC)
    {
        try
        {
            using RegistryKey key = Registry.LocalMachine.CreateSubKey(KeyPath, writable: true);
            key.SetValue("Points", ToPointLines(curve), RegistryValueKind.MultiString);
            SetOrDelete(key, "TempC", tempC);
            key.SetValue("SavedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            key.SetValue("AppVersion", Product.Version);
            key.SetValue("Name", gpu.Name);
            key.SetValue("PciIds", gpu.PciIds);
            SetOrDelete(key, "BoardSerial", gpu.BoardSerial);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                       or System.Security.SecurityException)
        {
            throw new CliError($@"Writing the reference to HKLM\{KeyPath} failed: {ex.Message}");
        }
    }

    /// <summary>The saved reference, or null. <paramref name="present"/> distinguishes nothing saved
    /// from saved-but-invalid data (partial write, hand-edited values): the parsed curve must also
    /// pass the same physical checks the tuning itself relies on, so registry nonsense can't reach a
    /// curve plan.</summary>
    private static Saved? TryLoad(out bool present)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(KeyPath);
        present = key is not null;
        if (key is null
            || key.GetValue("Name") is not string name
            || key.GetValue("PciIds") is not string pciIds
            || key.GetValue("SavedAt") is not string savedAt
            || key.GetValue("Points") is not string[] pointLines
            || TryParsePointLines(pointLines) is not { } curve
            || !GpuTuning.CurveVoltsPlausible(curve)
            || !GpuTuning.CurveFreqsReadable(curve))
        {
            return null;
        }

        return new Saved(new GpuIdentity(name, pciIds, key.GetValue("BoardSerial") as string),
            curve, savedAt, key.GetValue("TempC") as int?);
    }

    private static void SetOrDelete(RegistryKey key, string name, object? value)
    {
        if (value is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(name, value);
        }
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

    // The registry rendering of the curve: one "mv mhz" string per anchor, invariant.

    internal static string[] ToPointLines(IReadOnlyList<(int Mv, int Mhz)> curve)
        => curve.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Mv} {p.Mhz}")).ToArray();

    internal static IReadOnlyList<(int Mv, int Mhz)>? TryParsePointLines(string[] lines)
    {
        var curve = new List<(int Mv, int Mhz)>(lines.Length);
        foreach (string line in lines)
        {
            string[] parts = line.Split(' ');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mv)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mhz))
            {
                return null;
            }

            curve.Add((mv, mhz));
        }

        return curve;
    }
}
