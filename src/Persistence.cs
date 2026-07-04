using System.Security.Cryptography;
using System.Xml.Linq;

namespace SimpleNvidiaUndervolt;

/// <summary>Makes an undervolt survive a reboot: copies the app into Program Files — the executable
/// as-is plus a windowless <c>-nocmd</c> copy (so the logon task and shortcut clicks don't flash a
/// console, while the as-is copy stays a normal console tool) — and registers a Task Scheduler logon
/// task that re-runs the <c>-nocmd</c> copy (with <c>--silent</c>, so only a startup failure surfaces,
/// through its message box). The <c>clear</c> command removes the task; the installed copies stay,
/// since saved shortcuts target them.</summary>
internal static class Persistence
{
    public const string TaskName = Product.Name;

    /// <summary>schtasks rejects a longer <c>/TR</c> value, with an error that doesn't say so.</summary>
    private const int MaxTaskRunLength = 261;

    /// <summary>Registers the logon task that re-applies the undervolt, returning the one-line log
    /// message. <paramref name="absoluteArgs"/> is the fully-resolved undervolt command line (see
    /// <see cref="TuneRequest.ToAbsoluteArgs"/>). The caller installs the app first (see
    /// <see cref="InstallApp"/>); the task targets that copy.</summary>
    public static string RegisterLogonTask(string[] absoluteArgs)
    {
        // Run at logon (not at boot): the task then lives in the user's interactive session, so it can
        // reach the GPU and its failure box is actually on screen. /RL HIGHEST runs it elevated,
        // which the driver writes need.
        string taskRun = BuildTaskRun(InstalledNoCmdExePath(), absoluteArgs);
        RunSchtasks("/Create", "/F", "/TN", TaskName, "/SC", "ONLOGON", "/RL", "HIGHEST", "/TR", taskRun);
        return $"Registered logon task '{TaskName}'.";
    }

    /// <summary>Removes the logon task, so the undervolt no longer re-applies at startup. The installed
    /// copy in Program Files stays — saved shortcuts target it. A missing task is reported, not an
    /// error.</summary>
    public static string RemoveLogonTask()
        => RemoveTask(TaskName)
            ? $"Removed logon task '{TaskName}'; the undervolt will no longer re-apply at startup."
            : $"No logon task '{TaskName}' was registered.";

    /// <summary>The install directory: under Program Files (admin-only writable) because the logon task
    /// runs this binary elevated — from a user-writable location, any process running as the user could
    /// swap it for code that then runs as administrator at the next logon. Installing needs admin, which
    /// a real undervolt already has.</summary>
    internal static string InstallDir()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Product.Name);

    private const string NoCmdSuffix = "-nocmd";

    /// <summary>The installed executable, copied as-is (console subsystem).</summary>
    private static string InstalledExePath() => InstalledPath(suffix: string.Empty);

    /// <summary>The windowless <c>-nocmd</c> copy of the installed executable.</summary>
    public static string InstalledNoCmdExePath()
        => InstalledPath(NoCmdSuffix);

    /// <summary>The installed file for a suffix. Named for the product, not the running image, so a
    /// renamed download installs (and reinstalls) over the same files — saved shortcuts and the logon
    /// task keep resolving across re-persists from differently-named executables.</summary>
    private static string InstalledPath(string suffix)
        => Path.Combine(InstallDir(), Product.Name + suffix + ".exe");

    /// <summary>The active-marker icon — the app icon badged with a green check — installed next to
    /// the executables; the live profile's <c>.lnk</c> points its <c>IconLocation</c> here (see
    /// <see cref="Shortcut.MarkActive"/>).</summary>
    public static string InstalledActiveIconPath() => Path.Combine(InstallDir(), ActiveIconFileName);

    private const string ActiveIconFileName = "icon-active.ico";

    /// <summary>Writes the active-marker icon (an embedded resource) into the install directory when
    /// it isn't there — marking can run before anything was ever installed (a <c>--no-persist</c>
    /// run). Returns whether the icon exists afterwards; false (an unwritable install directory)
    /// makes the caller skip icon marking rather than point links at a missing file.</summary>
    public static bool EnsureActiveIcon()
    {
        string path = InstalledActiveIconPath();
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(InstallDir());
                using Stream icon = typeof(Persistence).Assembly.GetManifestResourceStream(ActiveIconFileName)!;
                using FileStream file = File.Create(path);
                icon.CopyTo(file);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Installs the app into Program Files: empties the install directory (so nothing from a
    /// previous install lingers), copies the running app — the executable as-is plus its own sidecar
    /// files — and adds a <c>-nocmd</c> copy of the executable made windowless (see
    /// <see cref="PeSubsystem"/>); shortcuts and the logon task target that copy. Returns whether it
    /// installed: the whole step is skipped when the installed executable exists, the <c>-nocmd</c>
    /// copy is verifiably windowless (an interrupted install that copied it but died before the patch
    /// reinstalls instead of skipping forever) and the running executable's bytes match its installed
    /// counterpart — a re-run of the same build skips, a different build (even one of identical size)
    /// reinstalls, and re-persisting from the install location itself, where the running image is
    /// locked, compares the file to itself and skips.
    /// Only the app's files are copied (<see cref="IsAppSidecar"/>), never everything in the
    /// source directory: the shipped build is a single AOT exe, so if it is run straight from a
    /// Downloads folder a copy-all would sweep unrelated files into the install directory.</summary>
    public static bool InstallApp()
    {
        string targetDir = InstallDir();
        string targetExe = InstalledExePath();
        string exeName = Path.GetFileName(targetExe);

        try
        {
            if (File.Exists(targetExe) && PeSubsystem.IsWindowless(InstalledNoCmdExePath())
                && SameContent(InstalledCounterpartPath(), Product.ExecutablePath()))
            {
                return false;
            }

            if (Directory.Exists(targetDir))
            {
                foreach (string file in Directory.GetFiles(targetDir))
                {
                    File.Delete(file);
                }
            }

            Directory.CreateDirectory(targetDir);

            // The exe is copied explicitly (the running file may carry any name - a renamed download,
            // the -nocmd copy - that the sidecar match below wouldn't map to the canonical one); the
            // loop brings its sidecars.
            File.Copy(Product.ExecutablePath(), targetExe, overwrite: true);
            RemoveMarkOfTheWeb(targetExe);
            foreach (string file in Directory.GetFiles(AppContext.BaseDirectory))
            {
                string name = Path.GetFileName(file);
                if (!name.Equals(exeName, StringComparison.OrdinalIgnoreCase) && IsAppSidecar(name, exeName))
                {
                    string copy = Path.Combine(targetDir, name);
                    File.Copy(file, copy, overwrite: true);
                    RemoveMarkOfTheWeb(copy);
                }
            }

            string noCmdExe = InstalledNoCmdExePath();
            File.Copy(targetExe, noCmdExe, overwrite: true);
            PeSubsystem.MakeWindowless(noCmdExe);
            EnsureActiveIcon(); // emptying the directory above removed any previous copy
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An installed file locked by a running shortcut instance, antivirus, a full disk - the
            // environment refusing the copy, so report the message, not a stack (see
            // ErrorReporter.Describe).
            throw new CliError($"Installing to {targetDir} failed: {ex.Message}");
        }
    }

    /// <summary>Removes the mark-of-the-web from an installed file. A downloaded (zip-extracted) exe
    /// carries the mark and <see cref="File.Copy(string,string,bool)"/> copies alternate data streams
    /// along, so without this the shortcuts' and logon task's first launch of the installed copy would
    /// re-trip SmartScreen on a file the user never knowingly downloaded. (The <c>-nocmd</c> copy is
    /// made from the already-cleaned installed exe, so it needs no strip of its own.) Best-effort: a
    /// missing stream — a build run from source — is the normal case.</summary>
    private static void RemoveMarkOfTheWeb(string path)
    {
        try
        {
            File.Delete(path + ":Zone.Identifier");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The installed file to compare the running executable against for the already-installed
    /// check: the <c>-nocmd</c> copy when this process runs from one (the canonical exe differs from it
    /// by the subsystem patch), else the console executable.</summary>
    private static string InstalledCounterpartPath()
        => Path.GetFileNameWithoutExtension(Product.ExecutablePath())
               .EndsWith(NoCmdSuffix, StringComparison.OrdinalIgnoreCase)
            ? InstalledNoCmdExePath()
            : InstalledExePath();

    /// <summary>Whether two files hold identical bytes: length first, then a SHA-256 of each. A running
    /// image reads fine — execution locks writes, not reads.</summary>
    private static bool SameContent(string pathA, string pathB)
    {
        if (new FileInfo(pathA).Length != new FileInfo(pathB).Length)
        {
            return false;
        }

        return HashFile(pathA).AsSpan().SequenceEqual(HashFile(pathB));
    }

    private static byte[] HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    /// <summary>Whether <paramref name="fileName"/> is one of this app's own files: the executable itself,
    /// or a build sidecar sharing its base name (a framework-dependent build's
    /// <c>.dll</c>/<c>.json</c>/<c>.pdb</c>; the shipped AOT single-file exe has none). Only those
    /// extensions count as sidecars — a user file that merely shares the base name (say a saved
    /// <c>simple-nvidia-undervolt.lnk</c>) is left behind, like everything else in the source directory.</summary>
    internal static bool IsAppSidecar(string fileName, string exeName)
    {
        if (fileName.Equals(exeName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fileName.StartsWith(Path.GetFileNameWithoutExtension(exeName) + ".", StringComparison.OrdinalIgnoreCase)
               && (fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                   || fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                   || fileName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The logon task's full command line: the quoted installed exe, the resolved undervolt and
    /// the <see cref="StartupFixedArgs"/>. Validated against <see cref="MaxTaskRunLength"/> here, since
    /// schtasks' own error doesn't name the limit.</summary>
    internal static string BuildTaskRun(string targetExe, string[] absoluteArgs)
    {
        string taskRun = CommandLine.Join(absoluteArgs.Concat(StartupFixedArgs).Prepend(targetExe));
        if (taskRun.Length > MaxTaskRunLength)
        {
            throw new CliError($"The logon task's command line is {taskRun.Length} characters; "
                                + $"schtasks allows at most {MaxTaskRunLength}. Shorten the arguments.");
        }

        return taskRun;
    }

    /// <summary>The fixed flags every persisted re-apply carries — it is itself the persistence, runs
    /// unattended and already elevated: don't re-persist, show only failures.</summary>
    private static readonly string[] StartupFixedArgs = { "--no-persist", "--silent" };

    /// <summary>One status line for the logon re-apply: the registered task's arguments (the command it
    /// will run), or "no" when nothing is persisted. Read-only. A Task Scheduler query that fails reads
    /// as unknown rather than as "not registered".</summary>
    public static string DescribeStartupTask()
    {
        try
        {
            var (exitCode, xml, error) = ChildProcess.Run("schtasks.exe", "/Query", "/TN", TaskName, "/XML");
            if (exitCode == 0)
            {
                return ParseTaskArguments(xml) is { } arguments
                    ? arguments
                    : $"yes (task '{TaskName}'), but its command couldn't be read from the registration.";
            }

            return TaskRegistered(TaskName) switch
            {
                false => "no (no logon task registered).",
                true => $"yes (task '{TaskName}'), but its registration couldn't be read ({error}).",
                null => $"unknown - couldn't query Task Scheduler ({error}).",
            };
        }
        catch (Exception ex)
        {
            // schtasks failing to start at all is a failed query like any other: report unknown rather
            // than throw - status consumes this line off a background Task, where a fault would surface
            // as a wrapped exception instead of a line in the report.
            return $"unknown - couldn't query Task Scheduler ({ErrorReporter.Describe(ex)}).";
        }
    }

    /// <summary>The &lt;Arguments&gt; of the task's exec action from schtasks' XML export, or null when
    /// the XML holds none (a hand-edited or foreign registration).</summary>
    internal static string? ParseTaskArguments(string taskXml)
    {
        try
        {
            return XDocument.Parse(taskXml).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Arguments")?.Value;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>Whether the task is registered, decided from the full task listing: schtasks' per-task
    /// query exits nonzero for a missing task and for a genuine failure alike, telling them apart only in
    /// localized text. Null when the listing itself fails, so a broken Task Scheduler doesn't read as
    /// "not registered".</summary>
    private static bool? TaskRegistered(string name)
    {
        var (exitCode, list, _) = ChildProcess.Run("schtasks.exe", "/Query", "/FO", "CSV", "/NH");
        return exitCode == 0 ? ListingContainsTask(list, name) : null;
    }

    /// <summary>Whether a <c>schtasks /Query /FO CSV /NH</c> listing contains the root-level task: rows
    /// lead with the quoted task path, and matching through the closing quote keeps a longer name
    /// (or the same name under a folder) from matching.</summary>
    internal static bool ListingContainsTask(string csvListing, string name)
    {
        string taskPath = $"\"\\{name}\"";
        return csvListing.Split('\n').Any(line => line.StartsWith(taskPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Deletes the named logon task, returning false when it wasn't registered. A deletion that
    /// fails for any other reason throws, so the caller doesn't report the task as gone.</summary>
    private static bool RemoveTask(string name)
    {
        var (exitCode, _, error) = ChildProcess.Run("schtasks.exe", "/Delete", "/TN", name, "/F");
        if (exitCode == 0)
        {
            return true;
        }

        if (TaskRegistered(name) == false)
        {
            return false;
        }

        throw new CliError($"schtasks failed: {error}");
    }

    private static void RunSchtasks(params string[] arguments)
    {
        var (exitCode, _, error) = ChildProcess.Run("schtasks.exe", arguments);
        if (exitCode != 0)
        {
            throw new CliError($"schtasks failed: {error}");
        }
    }
}
