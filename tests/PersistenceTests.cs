namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for the logon-task registration: the command line it runs — the resolved undervolt
/// (from <see cref="TuneRequest.ToPersistedArgs"/>) plus the fixed flags an unattended,
/// already-elevated re-apply needs — and how it is built and read back.</summary>
public class PersistenceTests
{
    // --- reading the registered task back ('status' shows what re-applies at logon) ---

    [Fact]
    public void ParseTaskArguments_ReadsTheExecActionsArguments()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions Context="Author">
                <Exec>
                  <Command>"C:\Program Files\simple-nvidia-undervolt\simple-nvidia-undervolt.exe"</Command>
                  <Arguments>--mv 960 --mhz-offset 190 --peak-mv 960 --no-persist --silent</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        Assert.Equal("--mv 960 --mhz-offset 190 --peak-mv 960 --no-persist --silent",
            Persistence.ParseTaskArguments(xml));
    }

    [Fact]
    public void ParseTaskArguments_NoArgumentsElement_IsNull()
    {
        Assert.Null(Persistence.ParseTaskArguments(
            "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Actions/></Task>"));
    }

    [Fact]
    public void ParseTaskArguments_MalformedXml_IsNull()
    {
        Assert.Null(Persistence.ParseTaskArguments("not xml at all"));
    }

    [Fact]
    public void InstalledNoCmdExePath_IsNamedForTheProduct_NotTheRunningImage()
    {
        // The install layout uses the canonical product name (here the running image is the test
        // host), so a renamed download reinstalls over the same files and previously saved shortcuts
        // keep resolving.
        Assert.Equal(
            Path.Combine(Persistence.InstallDir(), "simple-nvidia-undervolt-nocmd.exe"),
            Persistence.InstalledNoCmdExePath());
    }

    [Theory]
    [InlineData("simple-nvidia-undervolt.exe")]   // the executable itself
    [InlineData("simple-nvidia-undervolt.dll")]   // framework-dependent build sidecars
    [InlineData("simple-nvidia-undervolt.pdb")]
    [InlineData("simple-nvidia-undervolt.runtimeconfig.json")]
    [InlineData("simple-nvidia-undervolt.deps.json")]
    public void IsAppSidecar_CopiesTheAppsOwnFiles(string fileName)
    {
        Assert.True(Persistence.IsAppSidecar(fileName, "simple-nvidia-undervolt.exe"));
    }

    [Theory]
    [InlineData("Tune 960mV 2880MHz.lnk")]        // a saved shortcut sharing the directory
    [InlineData("simple-nvidia-undervolt.lnk")]   // shares the base name, but isn't a build sidecar
    [InlineData("some-download.zip")]             // an unrelated file (exe run from Downloads)
    [InlineData("nvapi64.dll")]                   // a system dll that happens to sit alongside
    [InlineData("simple-nvidia-undervolt")]       // the base name without the trailing dot: not ours
    public void IsAppSidecar_LeavesUnrelatedFilesBehind(string fileName)
    {
        Assert.False(Persistence.IsAppSidecar(fileName, "simple-nvidia-undervolt.exe"));
    }

    // --- the registered command line (schtasks' /TR value) ---

    [Fact]
    public void BuildTaskRun_QuotesTheExeAndAppendsTheUnattendedFixedFlags()
    {
        string run = Persistence.BuildTaskRun(
            @"C:\Program Files\simple-nvidia-undervolt\simple-nvidia-undervolt.exe",
            new[] { "--mv", "960" });

        Assert.Equal("\"C:\\Program Files\\simple-nvidia-undervolt\\simple-nvidia-undervolt.exe\" "
                     + "--mv 960 --no-persist --silent", run);
    }

    [Fact]
    public void BuildTaskRun_RejectsACommandLineSchtasksWouldTruncate()
    {
        // schtasks rejects a /TR over 261 characters with an error that doesn't say so; the build
        // must fail with the real reason instead.
        string deepExe = @"C:\" + new string('x', 300) + @"\simple-nvidia-undervolt.exe";
        Assert.Throws<CliError>(() => Persistence.BuildTaskRun(deepExe, new[] { "--mv", "960" }));
    }

    // --- finding the task in schtasks' CSV listing (how a missing task is told from a query failure) ---

    [Fact]
    public void ListingContainsTask_FindsTheRootLevelTask()
    {
        const string csv = "\"\\OtherTask\",\"N/A\",\"Ready\"\r\n"
                           + "\"\\simple-nvidia-undervolt\",\"N/A\",\"Ready\"\r\n";

        Assert.True(Persistence.ListingContainsTask(csv, "simple-nvidia-undervolt"));
    }

    [Theory]
    [InlineData("\"\\simple-nvidia-undervolt-old\",\"N/A\",\"Ready\"")]        // a longer name is not ours
    [InlineData("\"\\Vendor\\simple-nvidia-undervolt\",\"N/A\",\"Ready\"")]    // ours is at the root
    [InlineData("")]                                                           // an empty listing
    public void ListingContainsTask_DoesNotMatchOtherRegistrations(string csv)
    {
        Assert.False(Persistence.ListingContainsTask(csv, "simple-nvidia-undervolt"));
    }
}
