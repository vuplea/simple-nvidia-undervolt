using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace SimpleNvidiaUndervolt;

internal static class Elevation
{
    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Auto-elevation for the write commands. A non-elevated instance relaunches an elevated copy of itself
/// (one UAC prompt) and relays everything that copy prints back to this console — and into the
/// message box, when the outcome shows one — through named pipes, one per console stream, so the
/// child's stdout and stderr land on this process's own stdout and stderr (a redirect like
/// <c>2&gt;errors.log</c> keeps working across the elevation hop). The elevated child is given the pipe
/// base name with <c>--pipe-name</c> and routes its console output to the pipes instead of a fresh,
/// invisible console of its own. So the user runs the write commands from an ordinary terminal (or a
/// shortcut) and still sees the result.
/// </summary>
internal static class ElevationRelay
{
    private const string OutSuffix = "-out";
    private const string ErrSuffix = "-err";

    /// <summary>Parent side: open the pipes, launch an elevated copy, relay its output here, and return
    /// its exit code (0 or 1). A declined UAC prompt, or a child that dies before it can relay, throws a
    /// <see cref="CliError"/> the caller reports; the GPU is left untouched.</summary>
    public static int Elevate(string[] args)
    {
        string exe = Product.ExecutablePath();
        string pipeName = Product.Name + "-" + Guid.NewGuid().ToString("N");

        using var outServer = NewServer(pipeName + OutSuffix);
        using var errServer = NewServer(pipeName + ErrSuffix);

        // The elevated copy is started by ShellExecute, not by the user's .lnk, so its own startup
        // info can't name the launching link - hand the path across the hop (consumed by
        // TakeLaunchingLnk, mirroring --pipe-name).
        var childArgs = new List<string>(args);
        if (Shortcut.LaunchingLnkPath() is { } lnk)
        {
            childArgs.Add("--launching-lnk");
            childArgs.Add(lnk);
        }

        childArgs.Add("--pipe-name");
        childArgs.Add(pipeName);

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true, // required for the "runas" verb (and so we can't redirect - hence the pipes)
            Verb = "runas",
            // The child's own console window would sit empty (its output arrives here through the
            // pipes), so don't show one. With UseShellExecute this maps to ShellExecuteEx's nShow.
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            Arguments = CommandLine.Join(childArgs),
        };

        Process child;
        try
        {
            child = Process.Start(psi) ?? throw new CliError("Could not start an elevated instance.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            throw new CliError("Administrator access was declined; no changes were made.");
        }
        catch (Win32Exception ex)
        {
            // Any other ShellExecute refusal (policy blocking the relaunch, a blocked exe) is an
            // environment problem: report the message, not a stack (see ErrorReporter.Describe).
            throw new CliError($"Could not start an elevated instance: {ex.Message}");
        }

        bool relayed = RelayUntilDone(outServer, errServer, child);
        child.WaitForExit();
        if (!relayed && child.ExitCode != 0)
        {
            // The child failed before (or without) fully connecting back, so what it printed went to
            // its own hidden console - say so rather than end silently with only a bare exit code.
            // The one systemic cause is a cross-account elevation (see Connect), so name it here,
            // where the user actually sees a message.
            throw new CliError($"The elevated instance exited with code {child.ExitCode} without "
                + "connecting back; its output could not be relayed.\n"
                + "If the UAC prompt asked for another account's credentials (a standard-user "
                + "session), elevating across accounts is not supported - run from a terminal "
                + "elevated as that administrator instead.");
        }

        // The child relayed its own output already, so don't re-report - just adopt its success/failure.
        return child.ExitCode == 0 ? 0 : 1;
    }

    /// <summary>Child side: connect to the parent's pipes and route this process's stdout and stderr to
    /// them. Disposing restores the console and closes the pipes, signalling end-of-output to the parent.</summary>
    public static IDisposable RedirectToParent(string pipeName)
    {
        NamedPipeClientStream outPipe = Connect(pipeName + OutSuffix);
        try
        {
            return new PipeRedirect(outPipe, Connect(pipeName + ErrSuffix));
        }
        catch
        {
            outPipe.Dispose(); // the second connect failed; don't leak the first
            throw;
        }
    }

    /// <summary>Recognizes a relay child from its argv and takes the pipe name out of it.
    /// <see cref="Elevate"/> appends <c>--pipe-name &lt;name&gt;</c> as the final token pair, so only
    /// there, and only with the exact generated shape (see <see cref="IsRelayPipeName"/>), is the pair
    /// consumed — and stripped, so the command parses the original argv. A hand-typed
    /// <c>--pipe-name</c> anywhere else stays in the args and fails validation as an unknown option.</summary>
    public static string? TakeRelayPipeName(ref string[] args)
        => TakeTrailingPair(ref args, "--pipe-name", IsRelayPipeName);

    /// <summary>Takes the launching-link path <see cref="Elevate"/> forwarded, if any. Called only on
    /// a relay child (after <see cref="TakeRelayPipeName"/> stripped its trailing pair), where the pair
    /// can only be <see cref="Elevate"/>'s own - and stripped, so the command parses the original
    /// argv.</summary>
    public static string? TakeLaunchingLnk(ref string[] args)
        => TakeTrailingPair(ref args, "--launching-lnk", static _ => true);

    /// <summary>Takes a <c>&lt;flag&gt; &lt;value&gt;</c> pair off the end of the argv when it is there
    /// (and <paramref name="accept"/>s the value), returning the value or null.</summary>
    private static string? TakeTrailingPair(ref string[] args, string flag, Func<string, bool> accept)
    {
        if (args.Length >= 3 && args[^2] == flag && accept(args[^1]))
        {
            string value = args[^1];
            args = args[..^2];
            return value;
        }

        return null;
    }

    /// <summary>Whether a <c>--pipe-name</c> value has the exact shape <see cref="Elevate"/> generates
    /// (the product name plus 32 hex digits), so only our own relaunch is treated as a relay child.</summary>
    private static bool IsRelayPipeName(string name)
    {
        string prefix = Product.Name + "-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length != prefix.Length + 32)
        {
            return false;
        }

        return name[prefix.Length..].All(Uri.IsHexDigit);
    }

    /// <summary>An inbound relay pipe. CurrentUserOnly ACLs it to this user's account, so no other local
    /// account can connect in the elevated child's place (pipe names are enumerable, so the random name
    /// alone doesn't gate access). Only the server side can carry the option: an elevated process's owner
    /// SID is the Administrators group, so the client-side owner check would reject our own parent.
    /// The same ACL means a child elevated with a <em>different</em> account's credentials (a
    /// standard-user session) cannot connect either — unsupported, and diagnosed in
    /// <see cref="Connect"/> and <see cref="Elevate"/>'s fallback message.</summary>
    private static NamedPipeServerStream NewServer(string name)
        => new(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static NamedPipeClientStream Connect(string name)
    {
        var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
        try
        {
            client.Connect(10000);
        }
        catch (TimeoutException)
        {
            client.Dispose();
            throw new CliError("Could not connect back to the non-elevated instance that launched this one.");
        }
        catch (UnauthorizedAccessException)
        {
            // CurrentUserOnly pipes admit only the launching user's account. Elevating from a
            // standard-user session runs this child as the credentialed administrator - a different
            // account - so the connect is denied. Unsupported; say so instead of a raw denial (this
            // lands in the hidden console, so Elevate's fallback message names the cause too).
            client.Dispose();
            throw new CliError("Could not connect back to the launching instance: it runs as a "
                + "different account. Elevating with another account's credentials (a standard-user "
                + "session) is not supported - run from a terminal elevated as that administrator.");
        }

        return client;
    }

    /// <summary>Relays the child's connected pipes to this console until they close, returning whether
    /// both pipes connected — a child that dies during its own setup connects neither (or only one),
    /// and some or all of its output is lost, which the caller then flags.</summary>
    private static bool RelayUntilDone(NamedPipeServerStream outServer, NamedPipeServerStream errServer,
        Process child)
    {
        // The child connects both pipes right after it starts; don't hang if it dies before it does.
        Task outConnect = outServer.WaitForConnectionAsync();
        Task errConnect = errServer.WaitForConnectionAsync();
        while (!BothSettled(outConnect, errConnect, 200))
        {
            if (child.HasExited)
            {
                // Give a connect the child initiated just before exiting a moment to land, so its last
                // output isn't dropped; then pump only what did connect - a pipe the dead child opened
                // may still hold output.
                BothSettled(outConnect, errConnect, 500);
                break;
            }
        }

        Task outPump = PumpIfConnected(outConnect, outServer, Console.Out);
        Task errPump = PumpIfConnected(errConnect, errServer, Console.Error);
        Task.WaitAll(outPump, errPump);
        return outConnect.IsCompletedSuccessfully && errConnect.IsCompletedSuccessfully;
    }

    /// <summary>Whether both connect tasks have settled (connected or failed) within the timeout.
    /// Task.WaitAll would rethrow a faulted connect into the caller — and, on a windowless parent, a
    /// raw stack into the message box; settled is all the loop needs, since only a successfully
    /// connected side is pumped.</summary>
    private static bool BothSettled(Task outConnect, Task errConnect, int timeoutMs)
        => Task.WhenAll(outConnect, errConnect).ContinueWith(_ => 0).Wait(timeoutMs);

    private static Task PumpIfConnected(Task connect, NamedPipeServerStream server, TextWriter target)
        => connect.IsCompletedSuccessfully ? Task.Run(() => Pump(server, target)) : Task.CompletedTask;

    private static void Pump(Stream source, TextWriter target)
    {
        using var reader = new StreamReader(source, new UTF8Encoding(false), leaveOpen: true);
        var buffer = new char[1024];
        try
        {
            int n;
            while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                target.Write(buffer, 0, n);
            }
        }
        catch (IOException)
        {
            // A hard-killed child breaks the pipe mid-read; whatever arrived is all there is.
        }
    }

    private sealed class PipeRedirect : IDisposable
    {
        private readonly TolerantPipeWriter _outWriter;
        private readonly TolerantPipeWriter _errWriter;
        private readonly TextWriter _originalOut;
        private readonly TextWriter _originalError;

        public PipeRedirect(NamedPipeClientStream stdout, NamedPipeClientStream stderr)
        {
            _outWriter = new TolerantPipeWriter(stdout);
            _errWriter = new TolerantPipeWriter(stderr);
            _originalOut = Console.Out;
            _originalError = Console.Error;
            Console.SetOut(_outWriter);
            Console.SetError(_errWriter);
        }

        public void Dispose()
        {
            Console.SetOut(_originalOut);
            Console.SetError(_originalError);
            _outWriter.Dispose();
            _errWriter.Dispose();
        }
    }

    /// <summary>The child's console writer over a relay pipe. Writes through until the pipe breaks —
    /// the parent was closed or killed mid-run — then discards. The output is cosmetic to the child's
    /// work, so a dead parent must not abort the GPU write, the error report, or the closing flush
    /// with pipe IOExceptions of its own.</summary>
    private sealed class TolerantPipeWriter : TextWriter
    {
        private readonly StreamWriter _inner;
        private bool _broken;

        public TolerantPipeWriter(NamedPipeClientStream pipe)
            => _inner = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value) => Guarded(w => w.Write(value));

        public override void Write(string? value) => Guarded(w => w.Write(value));

        public override void Write(char[] buffer, int index, int count)
            => Guarded(w => w.Write(buffer, index, count));

        public override void Flush() => Guarded(w => w.Flush());

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Guarded(w => w.Dispose()); // disposing flushes, which throws on a broken pipe too
            }

            base.Dispose(disposing);
        }

        private void Guarded(Action<StreamWriter> write)
        {
            if (_broken)
            {
                return;
            }

            try
            {
                write(_inner);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _broken = true;
            }
        }
    }
}
