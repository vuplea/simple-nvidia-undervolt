using System.Text;

namespace SimpleNvidiaUndervolt;

/// <summary>Joins arguments into a single Windows command line — used to relaunch the elevated child,
/// bake a shortcut's arguments into its <c>.lnk</c>, and build the logon task's command. Quoting follows
/// the <c>CommandLineToArgvW</c> rules every consumer re-splits with, so a token survives intact even
/// with spaces, embedded quotes or trailing backslashes.</summary>
internal static class CommandLine
{
    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    /// <summary>Quotes one argument per the <c>CommandLineToArgvW</c> rules: a token with no whitespace
    /// or quote is returned unchanged; otherwise it is wrapped in quotes with backslashes that precede a
    /// quote (or the closing quote) doubled, and embedded quotes escaped as <c>\"</c>. Escaping only the
    /// quote — as a naive <c>Replace</c> would — mis-parses backslash runs next to a quote.</summary>
    private static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '\r' or '\n' or '\v' or '"'))
        {
            return arg;
        }

        var sb = new StringBuilder(arg.Length + 2).Append('"');
        for (int i = 0; ; i++)
        {
            int backslashes = 0;
            while (i < arg.Length && arg[i] == '\\')
            {
                i++;
                backslashes++;
            }

            if (i == arg.Length)
            {
                sb.Append('\\', backslashes * 2); // double the trailing run so the closing quote stays a delimiter
                break;
            }

            if (arg[i] == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"'); // escape the run and the embedded quote
            }
            else
            {
                sb.Append('\\', backslashes).Append(arg[i]); // backslashes are literal away from a quote
            }
        }

        return sb.Append('"').ToString();
    }
}
