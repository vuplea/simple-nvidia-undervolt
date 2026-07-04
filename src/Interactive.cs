using System.Runtime.InteropServices;
using System.Text;

namespace SimpleNvidiaUndervolt;

/// <summary>
/// Decides where the run's outcome is shown, from where the output can actually be seen. With a console
/// (or redirected output) the run prints normally. Without one — the windowless <c>-nocmd</c> copy
/// launched from a shortcut or the logon task — everything written to the console is captured and shown
/// in a message box when the run ends, so the result doesn't vanish with the process. <c>--silent</c>
/// suppresses all of it unless the run fails: nothing on success, and on failure the captured output
/// prints (console) or boxes (no console) — what the unattended logon task wants.
/// </summary>
internal sealed class InteractiveOutput
{
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;
    private readonly StringBuilder _buffer;
    private readonly bool _outputVisible;
    private readonly bool _silent;

    private InteractiveOutput(TextWriter originalOut, TextWriter originalError, StringBuilder buffer,
        bool outputVisible, bool silent)
    {
        _originalOut = originalOut;
        _originalError = originalError;
        _buffer = buffer;
        _outputVisible = outputVisible;
        _silent = silent;
    }

    /// <summary>Starts capturing the console when the run's outcome needs it — no console to see the
    /// output on (box it at the end), or a silent run (hold it back unless the run fails). A non-silent
    /// run with a visible console needs nothing and returns null — unless <paramref name="forceBox"/>
    /// demands the box anyway: a double-clicked installer copy gets a console, but that console closes
    /// with the process, so its result would vanish unread. Reads <c>--silent</c> from the raw
    /// argv: the capture must install before validation, so parse errors reach the message box too.
    /// The raw token scan can't disagree with a successful parse, because the parser never consumes a
    /// token spelled like a known flag as another flag's value (see <see cref="Args"/>); on a run that
    /// fails validation, silent shows the failure anyway.</summary>
    public static InteractiveOutput? Install(string[] args, bool forceBox)
    {
        bool silent = args.Contains("--silent");
        bool visible = ParentConsole.OutputVisible() && !forceBox;
        if (visible && !silent)
        {
            return null;
        }

        var buffer = new StringBuilder();
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        // A silent run holds output back (write-through would print on success); without a console the
        // write-through goes nowhere anyway, so capture-only serves both.
        Console.SetOut(new CaptureWriter(originalOut, buffer, writeThrough: !silent));
        Console.SetError(new CaptureWriter(originalError, buffer, writeThrough: !silent));
        return new InteractiveOutput(originalOut, originalError, buffer, visible, silent);
    }

    /// <summary>Stops capturing and shows the run's outcome where it belongs: nothing for a successful
    /// silent run; the captured output on the console when one is visible (a failed silent run); else a
    /// message box that reads as an error when the run failed.</summary>
    public void Complete(int exitCode)
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);

        if (_silent && exitCode == 0)
        {
            return;
        }

        string text = _buffer.ToString().TrimEnd();
        if (text.Length == 0)
        {
            text = exitCode == 0 ? "Done." : "Failed.";
        }

        if (_outputVisible)
        {
            (exitCode == 0 ? _originalOut : _originalError).WriteLine(text);
            return;
        }

        // SETFOREGROUND + TOPMOST, or the box opens buried: this process has no window to own it and
        // — fresh off the UAC consent hop — no foreground-activation right, so without the flags the
        // one thing this box exists for (being seen) is exactly what fails.
        MessageBoxW(IntPtr.Zero, text, Product.Name,
            MB_OK | MB_SETFOREGROUND | MB_TOPMOST
            | (exitCode == 0 ? MB_ICONINFORMATION : MB_ICONERROR));
    }

    /// <summary>Accumulates console output into a buffer, optionally also writing it through. The
    /// stdout and stderr writers share one buffer (the elevation relay pumps them from two tasks), so
    /// the buffer itself is the lock.</summary>
    private sealed class CaptureWriter : TextWriter
    {
        private readonly TextWriter _target;
        private readonly StringBuilder _buffer;
        private readonly bool _writeThrough;

        public CaptureWriter(TextWriter target, StringBuilder buffer, bool writeThrough)
        {
            _target = target;
            _buffer = buffer;
            _writeThrough = writeThrough;
        }

        public override Encoding Encoding => _target.Encoding;

        public override void Write(char value)
        {
            lock (_buffer)
            {
                if (_writeThrough)
                {
                    _target.Write(value);
                }

                _buffer.Append(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_buffer)
            {
                if (_writeThrough)
                {
                    _target.Write(value);
                }

                _buffer.Append(value);
            }
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_buffer)
            {
                if (_writeThrough)
                {
                    _target.Write(buffer, index, count);
                }

                _buffer.Append(buffer, index, count);
            }
        }

        public override void Flush() => _target.Flush();
    }

    private const uint MB_OK = 0x0;
    private const uint MB_ICONERROR = 0x10;
    private const uint MB_ICONINFORMATION = 0x40;
    private const uint MB_SETFOREGROUND = 0x10000;
    private const uint MB_TOPMOST = 0x40000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}

/// <summary>
/// Attaches the process to its parent's console when it has none of its own. The installed
/// <c>-nocmd</c> copy is patched to the GUI subsystem (see <see cref="PeSubsystem"/>), so without this
/// it would print nothing when run manually from a terminal. Launched with no console around it — the logon task, a
/// double-clicked shortcut — there is no parent console either; the attach quietly fails and output
/// stays discarded, which is what the unattended task wants (its failures surface through the
/// message box).
/// </summary>
internal static class ParentConsole
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    /// <summary>Whether this process's console output is going somewhere the user can see: a console
    /// window (its own, or the parent's after <see cref="AttachIfPresent"/>) or redirected std handles
    /// (a file or pipe). When false — the windowless <c>-nocmd</c> copy launched from a shortcut or the
    /// logon task — the run's outcome needs a message box instead (see <see cref="InteractiveOutput"/>).</summary>
    public static bool OutputVisible()
        => GetConsoleWindow() != IntPtr.Zero
           || IsRealHandle(GetStdHandle(StdOutputHandle))
           || IsRealHandle(GetStdHandle(StdErrorHandle));

    public static void AttachIfPresent()
    {
        // Nothing to do when output is already visible: a console of our own (the shipped
        // console-subsystem build), or redirected handles - `... > log.txt` must keep writing to the
        // file, and attaching would re-point the std handles at the console.
        if (OutputVisible())
        {
            return;
        }

        AttachConsole(AttachParentProcess);
    }

    /// <summary>Whether GetStdHandle returned an actual handle: NULL means there is none and
    /// INVALID_HANDLE_VALUE (-1) that the lookup failed — neither is a redirect worth preserving.</summary>
    private static bool IsRealHandle(IntPtr handle) => handle != IntPtr.Zero && handle != new IntPtr(-1);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);
}
