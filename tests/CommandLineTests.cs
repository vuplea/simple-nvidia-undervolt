using System.Runtime.InteropServices;

namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests that <see cref="CommandLine.Join"/> quotes arguments so Windows' own CommandLineToArgvW
/// re-splits them back to the originals — the parser the elevated relaunch, the saved shortcut and the
/// logon task all rely on. Round-tripping through that API exercises the backslash/quote edge cases a
/// naive quote-only escape gets wrong.</summary>
public class CommandLineTests
{
    [Theory]
    [InlineData("undervolt", "--mv", "960")]
    [InlineData("--shortcut-name", "My OC")]
    [InlineData(@"C:\dir with space\app", "--flag")]
    [InlineData(@"trailing\", "next")]              // trailing backslash
    [InlineData(@"path with space\", "next")]       // trailing backslash after a space
    [InlineData("embedded\"quote", "next")]         // embedded double quote
    [InlineData("back\\\"slash-quote", "next")]     // a backslash immediately before a quote
    [InlineData("", "after-empty")]                 // empty token
    public void Join_RoundTripsThroughArgvParsing(params string[] args)
    {
        Assert.Equal(args, SplitCommandLine(CommandLine.Join(args)));
    }

    /// <summary>Splits a command line the way the OS does. CommandLineToArgvW parses argv[0] by different
    /// rules than the rest, and Join only ever produces argv[1..], so a dummy program name is prepended
    /// and dropped.</summary>
    private static string[] SplitCommandLine(string commandLine)
    {
        IntPtr argv = CommandLineToArgvW("p " + commandLine, out int count);
        Assert.NotEqual(IntPtr.Zero, argv);
        try
        {
            var result = new string[count - 1];
            for (int i = 1; i < count; i++)
            {
                result[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            }

            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
