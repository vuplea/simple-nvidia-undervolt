namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for how a caught exception is rendered in a report: a <see cref="CliError"/> is the
/// one anticipated type and reads as its message alone, while anything else (a bug) keeps its type and
/// stack so a bug report from unknown hardware can pinpoint it.</summary>
public class ErrorReporterTests
{
    [Fact]
    public void CliError_ReadsAsItsMessageAlone()
    {
        Assert.Equal("boom", ErrorReporter.Describe(new CliError("boom")));
    }

    [Fact]
    public void RawEnvironmentalTypes_KeepTheirTypeForTheReport()
    {
        // File/process errors read as a message only where a call site expects them and converts to
        // CliError (installing, saving a shortcut, spawning a helper, the elevated relaunch). A raw
        // IOException or Win32Exception reaching a report came down a path no code anticipated - a
        // bug - so it keeps its type and stack.
        Assert.StartsWith("System.IO.IOException", ErrorReporter.Describe(new IOException("io")));
        Assert.StartsWith("System.UnauthorizedAccessException",
            ErrorReporter.Describe(new UnauthorizedAccessException("denied")));
        Assert.StartsWith("System.ComponentModel.Win32Exception",
            ErrorReporter.Describe(new System.ComponentModel.Win32Exception("win32")));
    }

    [Fact]
    public void UnanticipatedType_KeepsItsTypeForTheReport()
    {
        string described = ErrorReporter.Describe(new InvalidOperationException("a bug"));

        Assert.Contains(nameof(InvalidOperationException), described);
        Assert.Contains("a bug", described);
    }

    [Fact]
    public void BareException_IsNotAnticipated_AndKeepsItsType()
    {
        // Only CliError marks a failure as anticipated: a bare Exception out of framework or library
        // code is a bug, and must not be mistaken for a deliberately thrown message.
        string described = ErrorReporter.Describe(new Exception("a bug"));

        Assert.StartsWith("System.Exception", described);
        Assert.Contains("a bug", described);
    }
}
