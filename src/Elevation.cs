using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace SimpleNvidiaUndervolt;

/// <summary>Joins arguments into a Windows command line, quoting tokens that contain whitespace — used
/// both to relaunch the elevated child and to bake a shortcut's arguments into its <c>.lnk</c>.</summary>
internal static class CommandLine
{
    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    private static string Quote(string arg)
        => arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '"')
            ? arg
            : "\"" + arg.Replace("\"", "\\\"") + "\"";
}

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
/// (one UAC prompt) and relays everything that copy prints back to this console — and, with
/// <c>--interactive</c>, into the message box — through a named pipe. The elevated child is given the
/// pipe name with <c>--pipe-name</c> and routes its console output to the pipe instead of a fresh,
/// invisible console of its own. So the user runs the write commands from an ordinary terminal (or a
/// shortcut) and still sees the result.
/// </summary>
internal static class ElevationRelay
{
    /// <summary>Parent side: open the pipe, launch an elevated copy, relay its output here, and return
    /// its exit code. A declined UAC prompt is reported and leaves the GPU untouched.</summary>
    public static int Elevate(string[] args)
    {
        string exe = Environment.ProcessPath
            ?? throw new NvApiException("can't determine the running executable's path.");
        string pipeName = "nvidia-simple-undervolt-" + Guid.NewGuid().ToString("N");

        using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Console.WriteLine("Requesting administrator access (a UAC prompt will appear)...");

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true, // required for the "runas" verb (and so we can't redirect - hence the pipe)
            Verb = "runas",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            Arguments = BuildArguments(args, pipeName),
        };

        Process child;
        try
        {
            child = Process.Start(psi) ?? throw new NvApiException("could not start an elevated instance.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            ErrorReporter.Report("Administrator access was declined; no changes were made.");
            return 5;
        }

        RelayUntilDone(server, child);
        child.WaitForExit();
        return child.ExitCode;
    }

    /// <summary>Child side: connect to the parent's pipe and route this process's console output to it.
    /// Disposing restores the console and closes the pipe, signalling end-of-output to the parent.</summary>
    public static IDisposable RedirectToParent(string pipeName)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
        client.Connect(10000);
        return new PipeRedirect(client);
    }

    private static void RelayUntilDone(NamedPipeServerStream server, Process child)
    {
        // The child connects right after it starts; don't hang if it dies before it does.
        Task connect = server.WaitForConnectionAsync();
        while (!connect.Wait(200))
        {
            if (child.HasExited)
            {
                return;
            }
        }

        using var reader = new StreamReader(server, new UTF8Encoding(false));
        var buffer = new char[1024];
        int n;
        while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            Console.Out.Write(buffer, 0, n);
        }
    }

    /// <summary>The elevated child's command line: the original args plus <c>--pipe-name</c>.</summary>
    private static string BuildArguments(string[] args, string pipeName)
        => CommandLine.Join(new List<string>(args) { "--pipe-name", pipeName });

    private sealed class PipeRedirect : IDisposable
    {
        private readonly NamedPipeClientStream _client;
        private readonly StreamWriter _writer;
        private readonly TextWriter _originalOut;
        private readonly TextWriter _originalError;

        public PipeRedirect(NamedPipeClientStream client)
        {
            _client = client;
            _writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            _originalOut = Console.Out;
            _originalError = Console.Error;
            Console.SetOut(_writer);
            Console.SetError(_writer);
        }

        public void Dispose()
        {
            Console.SetOut(_originalOut);
            Console.SetError(_originalError);
            _writer.Dispose();
            _client.Dispose();
        }
    }
}
