namespace SimpleNvidiaUndervolt;

/// <summary>An anticipated, user-facing failure: bad arguments, a driver refusal, an environment the
/// command can't run in. Thrown wherever the code detects a condition it has a message for, and
/// reported as that message alone — any other exception type is a bug and keeps its type and stack
/// (see <see cref="ErrorReporter.Describe"/>).</summary>
internal sealed class CliError : Exception
{
    public CliError(string message) : base(message)
    {
    }
}

/// <summary>Reports errors to stderr. When no console is watching (a shortcut or the logon task), the
/// message box surfaces them instead — it captures stderr too.</summary>
internal static class ErrorReporter
{
    public static void Report(string message) => Console.Error.WriteLine(message);

    /// <summary>How a caught exception reads in a report. A <see cref="CliError"/> is an anticipated
    /// failure and reads as its message alone. Any other type — a raw file or process error included —
    /// is a path no code anticipated, so the full <c>ToString</c> (type and stack) goes out, giving a
    /// bug report from unknown hardware something to pinpoint. The call sites where the environment is
    /// expected to fail (installing to Program Files, saving a shortcut, spawning a helper process,
    /// the elevated relaunch) convert those errors to CliError with context, so they still read as
    /// plain messages.</summary>
    public static string Describe(Exception ex)
        => ex is CliError ? ex.Message : ex.ToString();
}
