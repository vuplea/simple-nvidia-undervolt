namespace SimpleNvidiaUndervolt;

/// <summary>Reading flag values out of the raw <c>args</c> array. The CLI hand-parses argv (rather than
/// pulling in a parser library) so the elevation relaunch, saved shortcuts and the logon task can pass
/// the same tokens straight through; these helpers are the one place "find the value after a flag" lives.</summary>
internal static class Args
{
    /// <summary>The token immediately after the first occurrence of <paramref name="flag"/>, or null when
    /// the flag is absent or is the final token (so no value follows).</summary>
    public static string? ValueAfter(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Like <see cref="ValueAfter"/>, but for a flag whose value is optional: returns the next
    /// token only when it isn't itself a flag (doesn't start with '-'), so a bare flag reads as null.</summary>
    public static string? OptionalValueAfter(string[] args, string flag)
        => ValueAfter(args, flag) is { } value && !value.StartsWith('-') ? value : null;
}
