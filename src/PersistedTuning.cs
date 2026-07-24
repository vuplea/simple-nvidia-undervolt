namespace SimpleNvidiaUndervolt;

/// <summary>
/// The tuning document every persisting run stores for the logon task, which re-applies it with
/// <c>tune --apply-persisted</c>. Persisting the resolved offsets as data — rather than a command
/// line to re-plan — makes the logon re-apply exact: per-anchor deltas are temperature-independent,
/// so nothing is re-derived from a boot-time curve read. Stored as a JSON file in the install
/// directory's data subfolder — the same document <c>--out-tuning-file</c> exports, so it can be
/// read or copied directly — which is under Program Files and thus admin-only writable, so the
/// elevated logon re-apply never consumes user-writable data.
/// </summary>
internal static class PersistedTuning
{
    public static string FilePath() => Path.Combine(Persistence.DataDir(), "persisted-tuning.json");

    /// <summary>Stores the document, replacing any previous one. Requires administrator (the
    /// install directory); the environment refusing the write is reported as the anticipated
    /// failure it is.</summary>
    public static void Save(TuningDoc doc)
        => TuningDocuments.WriteFile(FilePath(), TuningDocuments.Render(doc), "the persisted tuning");

    /// <summary>The stored document. An <c>--apply-persisted</c> run with nothing (usable) stored
    /// must fail loudly — it is the logon re-apply, and silence would read as an applied tuning.</summary>
    public static TuningDoc Load()
    {
        if (!File.Exists(FilePath()))
        {
            throw new CliError($"No persisted tuning is stored at {FilePath()} - nothing to apply. "
                               + "Re-run your tune command to persist one.");
        }

        return TuningDocuments.ReadTuningFile(FilePath());
    }

    /// <summary>One phrase for status's logon line: the stored document's capture date when it
    /// reads, else the inconsistency — an apply-persisted task with nothing to apply fails at the
    /// next logon, and status must say so rather than "yes".</summary>
    public static string DescribeForStatus()
    {
        try
        {
            return TuningDocuments.DescribeCapture(Load().SavedAt);
        }
        catch (CliError)
        {
            return "which is missing or unreadable - re-run your tune command";
        }
    }

    /// <summary>Deletes the stored document, returning whether one existed. Throws when the delete
    /// itself fails, so <c>clear</c> doesn't report persisted state as gone when it isn't.</summary>
    public static bool Remove()
    {
        if (!File.Exists(FilePath()))
        {
            return false;
        }

        File.Delete(FilePath());
        return true;
    }
}
