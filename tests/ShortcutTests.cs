namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for the --save-shortcut file name and the args baked into the link: the name describes
/// the settings, and the link re-runs the undervolt for real and interactively.</summary>
public class ShortcutTests
{
    private static UndervoltRequest Parse(params string[] args) => UndervoltRequest.Parse(args);

    [Fact]
    public void Describe_AbsoluteVoltageAndClock()
    {
        string name = ShortcutName.Describe(Parse("undervolt", "--mv", "960", "--mhz", "2880"));

        Assert.Equal("Undervolt 960mV 2880MHz", name);
    }

    [Fact]
    public void Describe_VoltageOnly()
    {
        Assert.Equal("Undervolt 925mV", ShortcutName.Describe(Parse("undervolt", "--mv", "925")));
    }

    [Fact]
    public void Describe_OffsetsKeepTheirSign()
    {
        string name = ShortcutName.Describe(
            Parse("undervolt", "--mv-offset", "-100", "--mhz-offset", "0", "--peak-mv", "1060"));

        Assert.Equal("Undervolt -100mV +0MHz", name);
    }

    [Fact]
    public void Describe_IncludesMemory()
    {
        string name = ShortcutName.Describe(Parse("undervolt", "--mv", "960", "--mem-offset", "1500"));

        Assert.Equal("Undervolt 960mV mem+1500", name);
    }

    [Fact]
    public void ShortcutArgs_DropSaveShortcutAndDryRun_AddInteractiveAndShortcutName()
    {
        var result = Shortcut.ShortcutArgs(
            new[] { "undervolt", "--mv", "960", "--mhz", "2880", "--save-shortcut", "--dry-run" },
            "Undervolt 960mV 2880MHz");

        Assert.Equal(
            new[] { "undervolt", "--mv", "960", "--mhz", "2880", "--shortcut-name", "Undervolt 960mV 2880MHz", "--interactive" },
            result);
    }

    [Fact]
    public void ShortcutArgs_DropSaveShortcutNameValue()
    {
        var result = Shortcut.ShortcutArgs(
            new[] { "undervolt", "--mv", "960", "--save-shortcut", "My OC" }, "My OC");

        Assert.Equal(new[] { "undervolt", "--mv", "960", "--shortcut-name", "My OC", "--interactive" }, result);
    }

    [Fact]
    public void ShortcutArgs_DropPipeNameAndExistingShortcutNameWithValues()
    {
        var result = Shortcut.ShortcutArgs(
            new[] { "undervolt", "--mv", "960", "--pipe-name", "abc123", "--shortcut-name", "Old", "--interactive" },
            "Undervolt 960mV");

        // --interactive is already present, so it keeps its place; the fresh --shortcut-name is appended.
        Assert.Equal(new[] { "undervolt", "--mv", "960", "--interactive", "--shortcut-name", "Undervolt 960mV" }, result);
    }

    // --- save target (name vs path) ---

    [Fact]
    public void ResolveSaveTarget_NoOverride_UsesSettingsNameInCwd()
    {
        var (lnk, dir, name) = Shortcut.ResolveSaveTarget(null, Parse("undervolt", "--mv", "960"), @"C:\work");

        Assert.Equal(@"C:\work\Undervolt 960mV.lnk", lnk);
        Assert.Equal(@"C:\work", dir);
        Assert.Equal("Undervolt 960mV", name);
    }

    [Theory]
    [InlineData("Quiet")]
    [InlineData("Quiet.lnk")]
    public void ResolveSaveTarget_BareName_AppendsLnkInCwd(string over)
    {
        var (lnk, dir, name) = Shortcut.ResolveSaveTarget(over, Parse("undervolt", "--mv", "960"), @"C:\work");

        Assert.Equal(@"C:\work\Quiet.lnk", lnk);
        Assert.Equal(@"C:\work", dir);
        Assert.Equal("Quiet", name);
    }

    [Fact]
    public void ResolveSaveTarget_AbsolutePath_KeepsItAndAppendsLnk()
    {
        var (lnk, dir, name) = Shortcut.ResolveSaveTarget(
            @"C:\Users\me\Desktop\Quiet", Parse("undervolt", "--mv", "960"), @"C:\work");

        Assert.Equal(@"C:\Users\me\Desktop\Quiet.lnk", lnk);
        Assert.Equal(@"C:\Users\me\Desktop", dir);
        Assert.Equal("Quiet", name);
    }

    [Fact]
    public void ResolveSaveTarget_RelativePath_ResolvesAgainstCwd()
    {
        var (lnk, dir, name) = Shortcut.ResolveSaveTarget(
            @"profiles\Quiet.lnk", Parse("undervolt", "--mv", "960"), @"C:\work");

        Assert.Equal(@"C:\work\profiles\Quiet.lnk", lnk);
        Assert.Equal(@"C:\work\profiles", dir);
        Assert.Equal("Quiet", name);
    }

    // --- [ACTIVE] marking ---

    [Fact]
    public void PlanActiveRenames_MarksMatchAndClearsOthers()
    {
        var plan = Shortcut.PlanActiveRenames(
            new[] { "Undervolt 960mV 2880MHz.lnk", "[ACTIVE] Undervolt 925mV.lnk" },
            "Undervolt 960mV 2880MHz");

        Assert.Equal(new[]
        {
            ("Undervolt 960mV 2880MHz.lnk", "[ACTIVE] Undervolt 960mV 2880MHz.lnk"),
            ("[ACTIVE] Undervolt 925mV.lnk", "Undervolt 925mV.lnk"),
        }, plan);
    }

    [Fact]
    public void PlanActiveRenames_WorksForACustomName()
    {
        var plan = Shortcut.PlanActiveRenames(new[] { "My OC.lnk" }, "My OC");

        Assert.Equal(new[] { ("My OC.lnk", "[ACTIVE] My OC.lnk") }, plan);
    }

    [Fact]
    public void PlanActiveRenames_LeavesTheAlreadyCorrectActiveLinkAlone()
    {
        var plan = Shortcut.PlanActiveRenames(
            new[] { "[ACTIVE] Undervolt 960mV.lnk" }, "Undervolt 960mV");

        Assert.Empty(plan);
    }

    [Fact]
    public void PlanActiveRenames_LeavesUnmarkedNonMatchesAlone()
    {
        var plan = Shortcut.PlanActiveRenames(
            new[] { "My Game.lnk", "Undervolt 925mV.lnk" }, "Undervolt 960mV");

        Assert.Empty(plan);
    }

    [Fact]
    public void PlanActiveRenames_ClearsAnyStaleMarkerWhenNothingMatches()
    {
        var plan = Shortcut.PlanActiveRenames(
            new[] { "[ACTIVE] Undervolt 925mV.lnk" }, "Undervolt 960mV");

        Assert.Equal(new[] { ("[ACTIVE] Undervolt 925mV.lnk", "Undervolt 925mV.lnk") }, plan);
    }
}
