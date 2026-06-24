using System.Runtime.InteropServices;
using System.Text;

namespace SimpleNvidiaUndervolt;

/// <summary>When to pop the <c>--interactive</c> message box.</summary>
internal enum InteractiveMode
{
    /// <summary>No box (the flag was not given).</summary>
    Off,

    /// <summary>Always show the box at the end of the run.</summary>
    Always,

    /// <summary>Show the box only when the run fails (a non-zero exit code).</summary>
    ErrorsOnly,
}

/// <summary>
/// <c>--interactive</c>: tee everything written to the console into a buffer and, when the run ends,
/// show it in a message box. Useful when the app is launched from a shortcut (double-click), where the
/// console window disappears the moment the process exits — the box keeps the result on screen. With the
/// <c>errors</c> mode the box shows only on failure, which is what the unattended logon task wants.
/// </summary>
internal sealed class InteractiveOutput
{
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;
    private readonly StringBuilder _buffer;
    private readonly InteractiveMode _mode;

    private InteractiveOutput(TextWriter originalOut, TextWriter originalError, StringBuilder buffer,
        InteractiveMode mode)
    {
        _originalOut = originalOut;
        _originalError = originalError;
        _buffer = buffer;
        _mode = mode;
    }

    /// <summary>Reads the mode from the args: absent → Off, <c>--interactive errors</c> → ErrorsOnly,
    /// a bare <c>--interactive</c> → Always.</summary>
    public static InteractiveMode ParseMode(string[] args)
    {
        int i = Array.IndexOf(args, "--interactive");
        if (i < 0)
        {
            return InteractiveMode.Off;
        }

        return i + 1 < args.Length && args[i + 1] == "errors" ? InteractiveMode.ErrorsOnly : InteractiveMode.Always;
    }

    /// <summary>Starts capturing the console, or returns null when the mode is Off.</summary>
    public static InteractiveOutput? Install(InteractiveMode mode)
    {
        if (mode == InteractiveMode.Off)
        {
            return null;
        }

        var buffer = new StringBuilder();
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        Console.SetOut(new TeeWriter(originalOut, buffer));
        Console.SetError(new TeeWriter(originalError, buffer));
        return new InteractiveOutput(originalOut, originalError, buffer, mode);
    }

    /// <summary>Stops capturing and, unless the run succeeded in ErrorsOnly mode, shows the buffered
    /// output in a message box that reads as an error when the run failed.</summary>
    public void Complete(int exitCode)
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);

        if (_mode == InteractiveMode.ErrorsOnly && exitCode == 0)
        {
            return;
        }

        string text = _buffer.ToString().TrimEnd();
        if (text.Length == 0)
        {
            text = exitCode == 0 ? "Done." : "Failed.";
        }

        MessageBoxW(IntPtr.Zero, text, "nvidia-simple-undervolt",
            MB_OK | (exitCode == 0 ? MB_ICONINFORMATION : MB_ICONERROR));
    }

    /// <summary>Writes through to the real console while also accumulating into a buffer.</summary>
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _target;
        private readonly StringBuilder _buffer;
        private readonly object _lock = new();

        public TeeWriter(TextWriter target, StringBuilder buffer)
        {
            _target = target;
            _buffer = buffer;
        }

        public override Encoding Encoding => _target.Encoding;

        public override void Write(char value)
        {
            lock (_lock)
            {
                _target.Write(value);
                _buffer.Append(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_lock)
            {
                _target.Write(value);
                _buffer.Append(value);
            }
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_lock)
            {
                _target.Write(buffer, index, count);
                _buffer.Append(buffer, index, count);
            }
        }

        public override void Flush() => _target.Flush();
    }

    private const uint MB_OK = 0x0;
    private const uint MB_ICONERROR = 0x10;
    private const uint MB_ICONINFORMATION = 0x40;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
