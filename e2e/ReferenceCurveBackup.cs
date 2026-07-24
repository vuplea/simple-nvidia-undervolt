namespace SimpleNvidiaUndervolt.E2E;

/// <summary>Backup of the machine-global state <c>set-reference-curve</c> owns: the reference curve
/// file in the install directory. It belongs to the user — a reference captured for their card —
/// and these tests both overwrite and delete it to exercise the reference and live paths
/// deterministically, so each wraps itself in one of these and calls <see cref="Restore"/> when it
/// finishes.</summary>
internal sealed class ReferenceCurveBackup
{
    private readonly string? _content;

    private ReferenceCurveBackup(string? content) => _content = content;

    /// <summary>Captures the reference file's content, or null when nothing is saved.</summary>
    public static ReferenceCurveBackup Create()
        => new(File.Exists(ReferenceCurve.FilePath()) ? File.ReadAllText(ReferenceCurve.FilePath()) : null);

    /// <summary>Removes the saved reference, if any. Guarded like <see cref="PersistedTuning.Remove"/>:
    /// on a host that never installed anything the data directory itself is missing, and a bare
    /// <see cref="File.Delete(string)"/> would throw instead of doing nothing.</summary>
    public static void Remove()
    {
        if (File.Exists(ReferenceCurve.FilePath()))
        {
            File.Delete(ReferenceCurve.FilePath());
        }
    }

    /// <summary>Puts the original reference back, or leaves no file when there was none.</summary>
    public void Restore()
    {
        Remove();
        if (_content is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReferenceCurve.FilePath())!);
            File.WriteAllText(ReferenceCurve.FilePath(), _content);
        }
    }
}
