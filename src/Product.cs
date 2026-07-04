using System.Reflection;

namespace SimpleNvidiaUndervolt;

/// <summary>The product name — the executable's base name, and what the install directory, logon task,
/// relay pipes and message-box captions are named after.</summary>
internal static class Product
{
    public const string Name = "simple-nvidia-undervolt";

    /// <summary>The running executable's full path — what the elevated relaunch and the installed copy
    /// are made from. Throws when the OS can't report it.</summary>
    public static string ExecutablePath()
        => Environment.ProcessPath ?? throw new CliError("Can't determine the running executable's path.");

    /// <summary>The build's version: the csproj Version plus the commit the SDK stamps into the
    /// informational version, e.g. <c>1.0.0+ab12cd34e</c>. Printed by <c>--version</c> and included in
    /// the layout reports, so a bug report identifies the exact binary that produced it.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        string? info = typeof(Product).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info))
        {
            return "unknown";
        }

        // The SDK appends the full 40-char commit hash; 9 characters identify it just as well.
        int plus = info.IndexOf('+');
        return plus >= 0 ? info[..Math.Min(info.Length, plus + 10)] : info;
    }
}
