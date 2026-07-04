namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for the --save-shortcut file name and the args baked into the link: the name describes
/// the settings, and the link re-runs the undervolt for real.</summary>
public class ShortcutTests
{
    private static TuneRequest Parse(params string[] args) => TuneRequest.Parse(args);

    [Fact]
    public void Describe_AbsoluteVoltageAndClock()
    {
        string name = ShortcutName.Describe(Parse("tune", "--mv", "960", "--mhz", "2880"));

        Assert.Equal("Tune 960mV 2880MHz", name);
    }

    [Fact]
    public void Describe_VoltageOnly()
    {
        Assert.Equal("Tune 925mV", ShortcutName.Describe(Parse("tune", "--mv", "925")));
    }

    [Fact]
    public void Describe_OffsetsKeepTheirSign()
    {
        string name = ShortcutName.Describe(
            Parse("tune", "--mv-offset", "-100", "--mhz-offset", "0", "--peak-mv", "1060"));

        Assert.Equal("Tune -100mV +0MHz", name);
    }

    [Fact]
    public void Describe_IncludesMemory()
    {
        string name = ShortcutName.Describe(Parse("tune", "--mv", "960", "--mem-offset", "1500"));

        Assert.Equal("Tune 960mV mem+1500", name);
    }

    [Fact]
    public void ToShortcutArgs_DropsSaveShortcutAndDryRun()
    {
        var result = Parse("tune", "--mv", "960", "--mhz", "2880", "--save-shortcut", "--dry-run")
            .ToShortcutArgs();

        Assert.Equal(new[] { "--mv", "960", "--mhz", "2880" }, result);
    }

    [Fact]
    public void ToShortcutArgs_DropsNoElevate()
    {
        // A double-clicked link must auto-elevate; a baked --no-elevate would make every click fail
        // against the driver.
        var result = Parse("tune", "--mv", "960", "--no-elevate", "--save-shortcut")
            .ToShortcutArgs();

        Assert.Equal(new[] { "--mv", "960" }, result);
    }

    [Fact]
    public void ToShortcutArgs_KeepsTheRelativeFormsAndPeak()
    {
        // The link reproduces the command as given, not one resolution of it: an offset and its peak
        // reference are re-emitted intact.
        var result = Parse("tune", "--mv-offset", "-100", "--peak-mv", "1060", "--save-shortcut")
            .ToShortcutArgs();

        Assert.Equal(new[] { "--mv-offset", "-100", "--peak-mv", "1060" }, result);
    }

    [Fact]
    public void ToShortcutArgs_CarriesTheOptOutFlagsAndCapPoints()
    {
        var result = Parse("tune", "--mv", "960", "--cap-points", "4", "--no-persist", "--save-shortcut")
            .ToShortcutArgs();

        Assert.Equal(
            new[] { "--mv", "960", "--cap-points", "4", "--no-persist" },
            result);
    }

    [Fact]
    public void ToShortcutArgs_CarriesSilent()
    {
        var result = Parse("tune", "--mv", "960", "--silent", "--save-shortcut").ToShortcutArgs();

        Assert.Equal(new[] { "--mv", "960", "--silent" }, result);
    }

    // --- save target (name vs path) ---

    [Fact]
    public void ResolveSaveTarget_NoOverride_UsesSettingsNameInCwd()
    {
        var (lnk, dir, name) = Shortcut.ResolveSaveTarget(
            Parse("tune", "--mv", "960", "--save-shortcut"), @"C:\work");

        Assert.Equal(@"C:\work\Tune 960mV.lnk", lnk);
        Assert.Equal(@"C:\work", dir);
        Assert.Equal("Tune 960mV", name);
    }

    [Theory]
    [InlineData("Quiet")]
    [InlineData("Quiet.lnk")]
    public void ResolveSaveTarget_BareName_AppendsLnkInCwd(string over)
    {
        var (lnk, dir, name) = Shortcut.ResolveSaveTarget(
            Parse("tune", "--mv", "960", "--save-shortcut", over), @"C:\work");

        Assert.Equal(@"C:\work\Quiet.lnk", lnk);
        Assert.Equal(@"C:\work", dir);
        Assert.Equal("Quiet", name);
    }

    [Fact]
    public void ResolveSaveTarget_AbsolutePath_KeepsItAndAppendsLnk()
    {
        var (lnk, dir, name) = Shortcut.ResolveSaveTarget(
            Parse("tune", "--mv", "960", "--save-shortcut", @"C:\Users\me\Desktop\Quiet"), @"C:\work");

        Assert.Equal(@"C:\Users\me\Desktop\Quiet.lnk", lnk);
        Assert.Equal(@"C:\Users\me\Desktop", dir);
        Assert.Equal("Quiet", name);
    }

    [Fact]
    public void ResolveSaveTarget_RelativePath_ResolvesAgainstCwd()
    {
        var (lnk, dir, name) = Shortcut.ResolveSaveTarget(
            Parse("tune", "--mv", "960", "--save-shortcut", @"profiles\Quiet.lnk"), @"C:\work");

        Assert.Equal(@"C:\work\profiles\Quiet.lnk", lnk);
        Assert.Equal(@"C:\work\profiles", dir);
        Assert.Equal("Quiet", name);
    }

    // --- active marking (the badge icon on the live profile's link) ---

    private const string Badge = @"C:\Program Files\simple-nvidia-undervolt\icon-active.ico,0";

    private static IReadOnlyList<(string File, string Icon)> Plan(
        (string File, string Icon)[] links, string activeName)
        => Shortcut.PlanActiveMarking(links, activeName, Badge);

    [Fact]
    public void PlanActiveMarking_BadgesMatchAndClearsOthers()
    {
        var changes = Plan(new[]
        {
            ("Tune 960mV 2880MHz.lnk", Shortcut.NoIcon),
            ("Tune 925mV.lnk", Badge),
        }, "Tune 960mV 2880MHz");

        Assert.Equal(new[]
        {
            ("Tune 960mV 2880MHz.lnk", Badge),
            ("Tune 925mV.lnk", Shortcut.NoIcon),
        }, changes);
    }

    [Fact]
    public void PlanActiveMarking_WorksForACustomName()
    {
        Assert.Equal(new[] { ("My OC.lnk", Badge) },
            Plan(new[] { ("My OC.lnk", Shortcut.NoIcon) }, "My OC"));
    }

    [Fact]
    public void PlanActiveMarking_LeavesTheAlreadyBadgedActiveLinkAlone()
    {
        Assert.Empty(Plan(new[] { ("Tune 960mV.lnk", Badge) }, "Tune 960mV"));
    }

    [Fact]
    public void PlanActiveMarking_NeverTouchesCustomIconsOnOtherLinks()
    {
        // Only the tool's own badge identifies a marked link; a link with any other explicit icon is
        // someone else's and stays untouched.
        var changes = Plan(new[]
        {
            ("My Game.lnk", @"C:\Games\game.ico,0"),
            ("Tune 925mV.lnk", Shortcut.NoIcon),
        }, "Tune 960mV");

        Assert.Empty(changes);
    }

    [Fact]
    public void PlanActiveMarking_ClearsAnyStaleBadgeWhenNothingMatches()
    {
        Assert.Equal(new[] { ("Tune 925mV.lnk", Shortcut.NoIcon) },
            Plan(new[] { ("Tune 925mV.lnk", Badge) }, "Tune 960mV"));
    }

    // --- PowerShell escaping (the .lnk is written through a PowerShell script) ---

    [Fact]
    public void Escape_DoublesEveryQuotePowerShellRecognizes()
    {
        // The U+2018..U+201B smart quotes delimit single-quoted strings too; an unescaped one in a
        // path (Word/Explorer autocorrect produces them) would end the literal and hand the rest to
        // PowerShell as elevated script.
        Assert.Equal("It''s", Shortcut.Escape("It's"));
        Assert.Equal("a’’b", Shortcut.Escape("a’b"));
        Assert.Equal("‘‘‚‚‛‛", Shortcut.Escape("‘‚‛"));
        Assert.Equal(@"C:\plain\path", Shortcut.Escape(@"C:\plain\path"));
    }
}
