using System.Globalization;
using System.Runtime.InteropServices;

namespace SimpleNvidiaUndervolt;

/// <summary>
/// <c>--save-shortcut</c>: drop a <c>.lnk</c> that re-runs this tuning for real — at the given name
/// or (relative or absolute) path, else in the current directory under a settings-derived name
/// (e.g. <c>Tune 960mV 2880MHz.lnk</c>). The link targets the installed <c>-nocmd</c>
/// copy in Program Files (windowless, so a double-click doesn't flash a console; stable, so the link
/// outlives the downloaded exe) and re-runs the same command (see
/// <see cref="TuneRequest.ToShortcutArgs"/>); a double-click shows its result in a message box
/// automatically, since the windowless copy has no console. The link's identity is not baked into its
/// arguments — a click finds the launching link through the process startup info (see
/// <see cref="LaunchingLnkPath"/>) — so the user can rename the file freely.
/// </summary>
internal static class Shortcut
{
    /// <summary>The <c>.lnk</c> the user double-clicked to start this process, or null when it wasn't
    /// started by one: the shell passes the shortcut's path in the startup info
    /// (STARTF_TITLEISLINKNAME). Read at launch, it names the link as it exists right now, so it
    /// survives any renaming of the file — unlike a name baked into the link's arguments.</summary>
    public static string? LaunchingLnkPath()
    {
        GetStartupInfoW(out STARTUPINFOW info);
        if ((info.dwFlags & STARTF_TITLEISLINKNAME) == 0 || info.lpTitle == IntPtr.Zero)
        {
            return null;
        }

        string? title = Marshal.PtrToStringUni(info.lpTitle);
        return title is not null && title.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            ? title
            : null;
    }
    /// <summary>Where a saved link goes and what it's called. The override
    /// (<see cref="TuneRequest.ShortcutNameOverride"/>) may be a bare name or a (relative or
    /// absolute) path, with or without the <c>.lnk</c> extension; a relative path resolves against
    /// <paramref name="cwd"/>. With no override the link lands in <paramref name="cwd"/> under the
    /// settings-derived name. The returned name (no directory, no extension) titles the link's
    /// description.</summary>
    internal static (string LnkPath, string Directory, string Name) ResolveSaveTarget(
        TuneRequest request, string cwd)
    {
        string name;
        string lnkPath;
        if (request.ShortcutNameOverride is { } over)
        {
            name = LinkBaseName(over);
            lnkPath = Path.GetFullPath(EnsureLnkExtension(over), cwd);
        }
        else
        {
            name = ShortcutName.Describe(request);
            lnkPath = Path.Combine(cwd, name + ".lnk");
        }

        return (lnkPath, Path.GetDirectoryName(lnkPath) ?? cwd, name);
    }

    /// <summary>Writes the link and returns its path plus a one-line log message naming the file. A dry
    /// or non-persisting run doesn't install the copy the link targets, so when it isn't installed
    /// either the message says the link needs a persisting run to work.</summary>
    public static (string LnkPath, string Message) SaveUndervolt(TuneRequest request)
    {
        string exe = Persistence.InstalledNoCmdExePath();
        var (lnkPath, directory, name) = ResolveSaveTarget(request, Directory.GetCurrentDirectory());
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A protected or otherwise unwritable target directory is an environment problem: report
            // the message, not a stack (see ErrorReporter.Describe).
            throw new CliError($"could not create the directory '{directory}': {ex.Message}");
        }

        // The link's "Start in" is its own directory, so a click runs with it as the working directory.
        string arguments = CommandLine.Join(request.ToShortcutArgs());
        Save(lnkPath, exe, arguments, directory, Product.Name + " - " + name);

        bool installsThisRun = request.Persist && !request.DryRun;
        return (lnkPath, installsThisRun || File.Exists(exe)
            ? $"Saved shortcut: {lnkPath}"
            : $"Saved shortcut: {lnkPath} (targets the installed copy, which isn't installed yet - "
              + "run a persisting undervolt to install it)");
    }

    private static string EnsureLnkExtension(string path)
        => path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? path : path + ".lnk";

    /// <summary>The bare link name — directory and the <c>.lnk</c> extension stripped — of a name or path.</summary>
    private static string LinkBaseName(string nameOrPath)
    {
        string file = Path.GetFileName(EnsureLnkExtension(nameOrPath));
        return file[..^".lnk".Length];
    }

    /// <summary>
    /// Badges the live profile's link — the <c>.lnk</c> this run was started from (see
    /// <see cref="LaunchingLnkPath"/>), or the one a real apply just saved
    /// (<see cref="SaveUndervolt"/>) — with the checkmarked app icon (its <c>IconLocation</c> is
    /// pointed at <see cref="Persistence.InstalledActiveIconPath"/>) and clears the badge from every
    /// other <c>.lnk</c> in the same directory, so the directory shows which profile is live without
    /// touching any file name. Only those runs carry a link identity to mark; a plain terminal run
    /// touches no links. File errors are reported as log lines rather than failing the undervolt.
    /// </summary>
    public static IReadOnlyList<string> MarkActive(string lnkPath)
    {
        string dir = Path.GetDirectoryName(lnkPath) is { Length: > 0 } d
            ? d
            : Directory.GetCurrentDirectory();
        string activeName = LinkBaseName(lnkPath);

        if (!Directory.Exists(dir))
        {
            return Array.Empty<string>(); // no directory, no links to mark or unmark
        }

        // Marking is cosmetic throughout: anything that fails from here on is a log line, never a
        // failed undervolt.
        if (!Persistence.EnsureActiveIcon())
        {
            return new[]
            {
                $"Could not write the active-marker icon to {Persistence.InstallDir()}; "
                + "links were left unmarked.",
            };
        }

        IReadOnlyList<(string File, string Icon)> links;
        try
        {
            links = ReadLinkIcons(dir);
        }
        catch (CliError ex)
        {
            return new[] { $"Could not read the links in {dir}: {ex.Message}" };
        }

        var changes = PlanActiveMarking(links, activeName, ActiveIconLocation());
        if (changes.Count == 0)
        {
            return Array.Empty<string>();
        }

        try
        {
            SetLinkIcons(dir, changes);
            foreach ((string file, _) in changes)
            {
                NotifyShellOfUpdate(Path.Combine(dir, file));
            }
        }
        catch (CliError ex)
        {
            return new[] { $"Could not update the link icons in {dir}: {ex.Message}" };
        }

        return Array.Empty<string>();
    }

    /// <summary>Decides the icon changes among the given links: the link whose name equals
    /// <paramref name="activeName"/> gets <paramref name="activeIcon"/>; any other link currently
    /// carrying that exact icon loses it (back to the default <see cref="NoIcon"/> — a custom icon on
    /// an unrelated link is never touched). The badge is this tool's own marker, so identification
    /// needs no naming convention — which lets <c>--save-shortcut &lt;name&gt;</c> use any file
    /// name.</summary>
    internal static IReadOnlyList<(string File, string Icon)> PlanActiveMarking(
        IReadOnlyList<(string File, string Icon)> links, string activeName, string activeIcon)
    {
        var changes = new List<(string File, string Icon)>();
        foreach ((string file, string icon) in links)
        {
            bool active = string.Equals(Path.GetFileNameWithoutExtension(file), activeName,
                StringComparison.OrdinalIgnoreCase);
            bool badged = string.Equals(icon, activeIcon, StringComparison.OrdinalIgnoreCase);
            if (active && !badged)
            {
                changes.Add((file, activeIcon));
            }
            else if (!active && badged)
            {
                changes.Add((file, NoIcon));
            }
        }

        return changes;
    }

    /// <summary>The <c>IconLocation</c> of a link with no explicit icon (what WScript.Shell reads back
    /// for one); assigning it restores the default target-derived icon.</summary>
    internal const string NoIcon = ",0";

    /// <summary>The <c>IconLocation</c> that marks the live profile's link.</summary>
    private static string ActiveIconLocation() => Persistence.InstalledActiveIconPath() + ",0";

    /// <summary>Every <c>.lnk</c> in the directory with its current <c>IconLocation</c>, read through
    /// one PowerShell pass (COM shortcut access, like <see cref="Save"/>). A link PowerShell cannot
    /// load reports an empty icon, so it can never read as badged. <c>|</c> separates safely — it
    /// cannot occur in a file name.</summary>
    private static IReadOnlyList<(string File, string Icon)> ReadLinkIcons(string directory)
    {
        string script =
            "$s=New-Object -ComObject WScript.Shell;"
            + $"Get-ChildItem -LiteralPath '{Escape(directory)}' -Filter *.lnk | ForEach-Object {{"
            + "$f=$_;try{$l=$s.CreateShortcut($f.FullName);Write-Output ($f.Name + '|' + $l.IconLocation)}"
            + "catch{Write-Output ($f.Name + '|')}}";

        var (exitCode, output, error) = ChildProcess.RunPowerShell(script);
        if (exitCode != 0)
        {
            throw new CliError($"powershell failed to read the links: {error}");
        }

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => (parts[0], parts[1]))
            .ToList();
    }

    /// <summary>Writes the planned <c>IconLocation</c>s in one PowerShell pass.</summary>
    private static void SetLinkIcons(string directory, IEnumerable<(string File, string Icon)> changes)
    {
        var script = new System.Text.StringBuilder("$s=New-Object -ComObject WScript.Shell;");
        foreach ((string file, string icon) in changes)
        {
            script.Append($"$l=$s.CreateShortcut('{Escape(Path.Combine(directory, file))}');")
                .Append($"$l.IconLocation='{Escape(icon)}';$l.Save();");
        }

        var (exitCode, _, error) = ChildProcess.RunPowerShell(script.ToString());
        if (exitCode != 0)
        {
            throw new CliError($"powershell failed to write the icons: {error}");
        }
    }

    private static void Save(string lnkPath, string target, string arguments, string workingDir, string description)
    {
        // Native AOT rules out in-process COM activation of WScript.Shell, so write the .lnk through
        // PowerShell's shortcut object (always present on Windows) - the same shell-out pattern as
        // persistence uses for schtasks.
        string script =
            $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{Escape(lnkPath)}');"
            + $"$s.TargetPath='{Escape(target)}';"
            + $"$s.Arguments='{Escape(arguments)}';"
            + $"$s.WorkingDirectory='{Escape(workingDir)}';"
            + $"$s.Description='{Escape(description)}';"
            + "$s.Save()";

        var (exitCode, _, error) = ChildProcess.RunPowerShell(script);
        if (exitCode != 0)
        {
            throw new CliError($"powershell failed to write the shortcut: {error}");
        }
    }

    // PowerShell single-quoted string literal: a quote is escaped by doubling it. The tokenizer
    // treats the U+2018..U+201B smart quotes as single-quote delimiters too, so they must be doubled
    // as well - an unescaped ’ (what Word/Explorer autocorrect puts in folder names) would terminate
    // the literal and hand the rest of the path to PowerShell as elevated script.
    internal static string Escape(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            sb.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛')
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>Tells the shell the file changed, so an Explorer view showing it (the desktop,
    /// typically) re-reads the link's icon instead of keeping the cached one.</summary>
    private static void NotifyShellOfUpdate(string path)
        => SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, path, IntPtr.Zero);

    private const int SHCNE_UPDATEITEM = 0x00002000;
    private const uint SHCNF_PATHW = 0x0005;

    /// <summary>With SHCNF_PATHW item1 is a wide path string; item2 is unused for SHCNE_UPDATEITEM.</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);

    /// <summary>lpTitle holds the path of the .lnk that started this process.</summary>
    private const uint STARTF_TITLEISLINKNAME = 0x00000800;

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public uint cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern void GetStartupInfoW(out STARTUPINFOW info);
}

/// <summary>Builds the short, human-readable file name for a <c>--save-shortcut</c> link from the
/// tuning settings, e.g. <c>Tune 960mV 2880MHz</c> or <c>Tune -100mV +0MHz mem+1500</c>.</summary>
internal static class ShortcutName
{
    public static string Describe(TuneRequest request)
    {
        string?[] parts =
        {
            Part(request.Mv, prefix: "", unit: "mV", pctUnit: "pctV"),
            Part(request.Mhz, prefix: "", unit: "MHz", pctUnit: "pctMHz"),
            Part(request.Mem, prefix: "mem", unit: "", pctUnit: "pct"),
        };

        return "Tune " + string.Join(' ', parts.Where(p => p is not null));
    }

    /// <summary>One settings token: the value unsigned for the absolute form (<c>960mV</c>), signed for
    /// the relative ones (<c>-100mV</c>, <c>mem+1500</c>, <c>+5pctMHz</c>), or null when unset.</summary>
    private static string? Part(ValueSpec spec, string prefix, string unit, string pctUnit)
        => spec.Absolute is { } v ? $"{prefix}{Num(v)}{unit}"
            : spec.Offset is { } o ? $"{prefix}{Signed(o)}{unit}"
            : spec.Percent is { } p ? $"{prefix}{Signed(p)}{pctUnit}"
            : null;

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Signed(double value) => (value >= 0 ? "+" : string.Empty) + Num(value);
}
