using System.Diagnostics;
using System.Globalization;

namespace SimpleNvidiaUndervolt;

/// <summary>
/// <c>--save-shortcut</c>: drop a <c>.lnk</c> in the current directory that re-runs this undervolt for
/// real and interactively. The file name is <c>--save-shortcut &lt;name&gt;</c> if given, else describes the
/// settings (e.g. <c>Undervolt 960mV 2880MHz.lnk</c>). The target re-runs the same command with
/// <c>--interactive</c> (so a double-click shows the result in a message box) and without
/// <c>--dry-run</c>/<c>--save-shortcut</c>, carrying the link's own name in a hidden <c>--shortcut-name</c>
/// so a click can re-mark it <c>[ACTIVE]</c>.
/// </summary>
internal static class Shortcut
{
    /// <summary>The active-link <em>name</em> (no directory, no extension) for this request: derived from
    /// the explicit override (which may be a path) if given, else the settings-derived name. This is what
    /// gets the <c>[ACTIVE]</c> marker and is baked into the link as <c>--shortcut-name</c>.</summary>
    public static string ResolveName(UndervoltRequest request)
        => request.ShortcutNameOverride is { } over ? LinkBaseName(over) : ShortcutName.Describe(request);

    /// <summary>Where a saved link goes and what it's called. The override may be a bare name or a
    /// (relative or absolute) path, with or without the <c>.lnk</c> extension; a relative path resolves
    /// against <paramref name="cwd"/>. With no override the link lands in <paramref name="cwd"/> under the
    /// settings-derived name.</summary>
    internal static (string LnkPath, string Directory, string Name) ResolveSaveTarget(
        string? over, UndervoltRequest request, string cwd)
    {
        string name = over is { } o ? LinkBaseName(o) : ShortcutName.Describe(request);
        string lnkPath = over is { } o2
            ? Path.GetFullPath(EnsureLnkExtension(o2), cwd)
            : Path.Combine(cwd, name + ".lnk");
        return (lnkPath, Path.GetDirectoryName(lnkPath) ?? cwd, name);
    }

    /// <summary>Writes the link and returns a one-line log message naming the file.</summary>
    public static string SaveUndervolt(string[] args, UndervoltRequest request)
    {
        string exe = Environment.ProcessPath
            ?? throw new NvApiException("can't determine the running executable's path.");
        var (lnkPath, directory, name) =
            ResolveSaveTarget(request.ShortcutNameOverride, request, Directory.GetCurrentDirectory());
        Directory.CreateDirectory(directory);

        // The link's "Start in" is its own directory, so a click runs there and re-marks it [ACTIVE].
        string arguments = CommandLine.Join(ShortcutArgs(args, name));
        Save(lnkPath, exe, arguments, directory, "nvidia-simple-undervolt - " + name);
        return $"Saved shortcut: {lnkPath}";
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
    /// Renames the link named <paramref name="activeName"/> in the current directory to <c>[ACTIVE] …</c>
    /// and clears the marker from every other <c>[ACTIVE]</c> link there, so the directory shows which
    /// profile is live. File errors are reported as log lines rather than failing the undervolt.
    /// </summary>
    public static IReadOnlyList<string> MarkActive(string activeName)
    {
        string dir = Directory.GetCurrentDirectory();
        string[] fileNames = Directory.GetFiles(dir, "*.lnk").Select(Path.GetFileName).ToArray()!;
        var log = new List<string>();

        foreach ((string from, string to) in PlanActiveRenames(fileNames, activeName))
        {
            try
            {
                string destination = Path.Combine(dir, to);
                File.Delete(destination); // no-op if absent; clear a stale collision so Move can't fail
                File.Move(Path.Combine(dir, from), destination);
                if (to.StartsWith(ActivePrefix, StringComparison.Ordinal))
                {
                    log.Add($"Marked active: {to}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Add($"Could not rename {from}: {ex.Message}");
            }
        }

        return log;
    }

    /// <summary>Decides the <c>[ACTIVE]</c> renames among the given <c>.lnk</c> file names: the link whose
    /// bare name equals <paramref name="activeName"/> gains the prefix; any other link that currently
    /// carries the prefix loses it. The prefix is this tool's own marker, so identification needs no
    /// naming convention - which lets <c>--save-shortcut &lt;name&gt;</c> use any file name. A returned
    /// (from, to) pair is only included when the name actually changes.</summary>
    internal static IReadOnlyList<(string From, string To)> PlanActiveRenames(
        IEnumerable<string> lnkFileNames, string activeName)
    {
        var renames = new List<(string, string)>();
        foreach (string file in lnkFileNames)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            bool prefixed = name.StartsWith(ActivePrefix, StringComparison.Ordinal);
            string bare = prefixed ? name[ActivePrefix.Length..] : name;
            bool active = string.Equals(bare, activeName, StringComparison.OrdinalIgnoreCase);

            // Mark the active link; unmark only links that already bear the marker. Leave everything else.
            if (active == prefixed)
            {
                continue;
            }

            renames.Add((file, (active ? ActivePrefix : string.Empty) + bare + ".lnk"));
        }

        return renames;
    }

    /// <summary>The prefix that marks the live profile's link.</summary>
    private const string ActivePrefix = "[ACTIVE] ";

    /// <summary>The args the shortcut runs: the originals without <c>--save-shortcut</c> (and any name),
    /// <c>--dry-run</c> or relay/identity flags, plus <c>--interactive</c> (so a double-click reports its
    /// result in a box) and <c>--shortcut-name &lt;name&gt;</c> (so the click re-marks this very link active).</summary>
    internal static IReadOnlyList<string> ShortcutArgs(string[] args, string name)
    {
        var kept = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--dry-run")
            {
                continue;
            }

            if (args[i] is "--pipe-name" or "--shortcut-name")
            {
                i++; // drop the flag and its value
                continue;
            }

            if (args[i] == "--save-shortcut")
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    i++; // drop the optional name value too
                }

                continue;
            }

            kept.Add(args[i]);
        }

        kept.Add("--shortcut-name");
        kept.Add(name);
        if (!kept.Contains("--interactive"))
        {
            kept.Add("--interactive");
        }

        return kept;
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

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using Process process = Process.Start(psi)
            ?? throw new NvApiException("could not start powershell.exe to write the shortcut.");
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new NvApiException($"powershell failed to write the shortcut: {error.Trim()}");
        }
    }

    // PowerShell single-quoted string literal: a literal quote is doubled.
    private static string Escape(string value) => value.Replace("'", "''");
}

/// <summary>Builds the short, human-readable file name for a <c>--save-shortcut</c> link from the
/// undervolt settings, e.g. <c>Undervolt 960mV 2880MHz</c> or <c>Undervolt -100mV +0MHz mem+1500</c>.</summary>
internal static class ShortcutName
{
    /// <summary>Common leading text on every generated name; also used to recognize our own links.</summary>
    public const string NamePrefix = "Undervolt ";

    public static string Describe(UndervoltRequest request)
    {
        var parts = new List<string> { Voltage(request) };
        if (Frequency(request) is { } f)
        {
            parts.Add(f);
        }

        if (Memory(request) is { } m)
        {
            parts.Add(m);
        }

        return NamePrefix + string.Join(' ', parts);
    }

    private static string Voltage(UndervoltRequest r)
        => r.Mv is { } v ? $"{Num(v)}mV"
            : r.MvOffset is { } o ? $"{Signed(o)}mV"
            : $"{Signed(r.MvPct!.Value)}pctV";

    private static string? Frequency(UndervoltRequest r)
        => r.Mhz is { } v ? $"{Num(v)}MHz"
            : r.MhzOffset is { } o ? $"{Signed(o)}MHz"
            : r.MhzPct is { } p ? $"{Signed(p)}pctMHz"
            : null;

    private static string? Memory(UndervoltRequest r)
        => r.Mem is { } v ? $"mem{Num(v)}"
            : r.MemOffset is { } o ? $"mem{Signed(o)}"
            : r.MemPct is { } p ? $"mem{Signed(p)}pct"
            : null;

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Signed(double value) => (value >= 0 ? "+" : string.Empty) + Num(value);
}
