using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace SimpleNvidiaUndervolt.E2E;

/// <summary>Starts the app the way the shell starts a double-clicked shortcut: the link's path rides
/// in the process startup info (<c>STARTF_TITLEISLINKNAME</c> in <c>lpTitle</c>), which is where
/// <c>Shortcut.LaunchingLnkPath</c> reads it. <see cref="System.Diagnostics.ProcessStartInfo"/>
/// cannot set that field, so this drives <c>CreateProcess</c> directly, with stdout and stderr
/// redirected into one pipe like <c>ChildProcess</c> combines them.</summary>
internal static class LinkLaunch
{
    public static (int ExitCode, string Output) Run(
        string lnkPath, string workingDir, string exe, params string[] args)
    {
        using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var startup = new STARTUPINFOW
        {
            cb = (uint)Marshal.SizeOf<STARTUPINFOW>(),
            lpTitle = lnkPath,
            dwFlags = STARTF_TITLEISLINKNAME | STARTF_USESTDHANDLES,
            hStdOutput = pipe.ClientSafePipeHandle.DangerousGetHandle(),
            hStdError = pipe.ClientSafePipeHandle.DangerousGetHandle(),
        };

        string commandLine = CommandLine.Join(args.Prepend(exe));
        if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, bInheritHandles: true,
                dwCreationFlags: 0, IntPtr.Zero, workingDir, ref startup, out PROCESS_INFORMATION process))
        {
            throw new InvalidOperationException(
                $"CreateProcess failed with error {Marshal.GetLastWin32Error()} for: {commandLine}");
        }

        try
        {
            // Drop the local copy of the child's pipe end, or the read below never sees end-of-pipe;
            // read concurrently, so a chatty child can't fill the pipe and deadlock against the wait.
            pipe.DisposeLocalCopyOfClientHandle();
            using var reader = new StreamReader(pipe);
            Task<string> output = reader.ReadToEndAsync();

            if (WaitForSingleObject(process.hProcess, 120_000) != 0)
            {
                throw new InvalidOperationException($"the link-launched run didn't finish: {commandLine}");
            }

            GetExitCodeProcess(process.hProcess, out uint exitCode);
            return ((int)exitCode, output.Result);
        }
        finally
        {
            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
        }
    }

    private const uint STARTF_USESTDHANDLES = 0x00000100;
    private const uint STARTF_TITLEISLINKNAME = 0x00000800;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public uint cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
