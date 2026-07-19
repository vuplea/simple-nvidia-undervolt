using Microsoft.Win32;

namespace SimpleNvidiaUndervolt.E2E;

/// <summary>Backup of the machine-global state <c>save-reference</c> owns: the HKLM key holding the
/// saved reference curve. It belongs to the user — a reference captured for their card — and these
/// tests both overwrite and delete it to exercise the reference and live paths deterministically, so
/// each wraps itself in one of these and calls <see cref="Restore"/> when it finishes.</summary>
internal sealed class ReferenceCurveBackup
{
    private readonly (string Name, object Value, RegistryValueKind Kind)[]? _values;

    private ReferenceCurveBackup((string, object, RegistryValueKind)[]? values) => _values = values;

    /// <summary>Captures every value under the key, or null when nothing is saved.</summary>
    public static ReferenceCurveBackup Create()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(ReferenceCurve.KeyPath);
        return new(key?.GetValueNames()
            .Select(name => (name, key.GetValue(name)!, key.GetValueKind(name)))
            .ToArray());
    }

    public static void Remove()
        => Registry.LocalMachine.DeleteSubKeyTree(ReferenceCurve.KeyPath, throwOnMissingSubKey: false);

    /// <summary>Puts the original reference back, or leaves no key when there was none — always from a
    /// clean slate, so a value the test added can't survive under a restored key.</summary>
    public void Restore()
    {
        Remove();
        if (_values is null)
        {
            return;
        }

        using RegistryKey key = Registry.LocalMachine.CreateSubKey(ReferenceCurve.KeyPath, writable: true);
        foreach (var (name, value, kind) in _values)
        {
            key.SetValue(name, value, kind);
        }
    }
}
