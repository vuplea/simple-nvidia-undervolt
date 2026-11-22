namespace SimpleNvidiaUndervolt;

/// <summary>Guards against documents saved by earlier releases whose values mean something else
/// to this build.</summary>
internal static class Compat
{
    // Releases 1.0.5 (the first to write documents) through 1.0.9 read every curve as plain kHz.
    private static readonly Version FirstPlainKhzOnly = new(1, 0, 5);
    private static readonly Version LastPlainKhzOnly = new(1, 0, 9);

    /// <summary>Refuses a tuning whose offsets are doubled: it was saved on this card by a
    /// release that read its half-kHz curve (<see cref="NvApi.CurveUnitsPerKhz(IntPtr)"/>) as
    /// plain kHz. A document naming no release (hand-written, or a development build) passes.</summary>
    public static void RequireTrueClocks(string? appVersion, IntPtr gpu, string what)
    {
        if (SavedByPlainKhzOnlyRelease(appVersion) && NvApi.CurveUnitsPerKhz(gpu) != 1)
        {
            throw new CliError($"{what} was saved by {Product.Name} {appVersion}, which read this "
                               + "GPU's clocks at twice their real value - its offsets can't be "
                               + "used. Re-run your tune command.");
        }
    }

    internal static bool SavedByPlainKhzOnlyRelease(string? appVersion)
        => Version.TryParse(appVersion?.Split('+')[0], out Version? version)
           && version >= FirstPlainKhzOnly && version <= LastPlainKhzOnly;
}
