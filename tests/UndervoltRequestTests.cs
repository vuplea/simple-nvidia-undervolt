namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for parsing and resolving the <c>undervolt</c> command's inputs: the mutually
/// exclusive voltage/clock forms, their validation, and how offsets/percentages resolve against the
/// curve and the peak operating point.</summary>
public class UndervoltRequestTests
{
    private static UndervoltRequest Parse(params string[] args) => UndervoltRequest.Parse(args);

    // --- Validation (UndervoltRequest.Parse) ---

    [Fact]
    public void MissingVoltageCap_Throws()
    {
        Assert.Throws<NvApiException>(() => Parse("undervolt"));
    }

    [Fact]
    public void TwoVoltageForms_Throws()
    {
        Assert.Throws<NvApiException>(() => Parse("undervolt", "--mv", "960", "--mv-offset", "-50"));
    }

    [Fact]
    public void TwoClockForms_Throws()
    {
        Assert.Throws<NvApiException>(
            () => Parse("undervolt", "--mv", "960", "--mhz", "2800", "--mhz-offset", "-50"));
    }

    [Theory]
    [InlineData("--mv-offset")]
    [InlineData("--mv-pct")]
    public void NonNegativeVoltageReduction_Throws(string flag)
    {
        Assert.Throws<NvApiException>(() => Parse("undervolt", flag, "50", "--peak-mv", "1060"));
    }

    [Fact]
    public void RelativeFormWithoutAReferencePoint_Throws()
    {
        Assert.Throws<NvApiException>(() => Parse("undervolt", "--mv-offset", "-100"));
    }

    [Fact]
    public void NonNumericValue_Throws()
    {
        Assert.Throws<NvApiException>(() => Parse("undervolt", "--mv", "lots"));
    }

    [Theory]
    [InlineData("--no-persit")]   // a misspelled --no-persist must not be silently ignored
    [InlineData("--mvv")]
    [InlineData("--peak")]
    public void UnknownFlag_Throws(string flag)
    {
        Assert.Throws<NvApiException>(() => Parse("undervolt", "--mv", "960", flag));
    }

    [Fact]
    public void KnownFlagsAndNegativeValues_AreAccepted()
    {
        // Negative numeric values start with a single dash, so they must not read as unknown flags.
        var request = Parse("undervolt", "--mv-offset", "-100", "--mhz-offset", "-50", "--peak-mv", "1060",
            "--cap-points", "8", "--no-persist", "--no-shortcut-rename", "--dry-run");
        Assert.Equal(-100, request.MvOffset);
        Assert.Equal(-50, request.MhzOffset);
    }

    [Fact]
    public void DecimalValue_ParsesInvariantly_RegardlessOfCulture()
    {
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            // A comma-decimal locale must not turn "2.5" into 25 (which would 10x the change).
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var (_, targetMhz) = Parse("undervolt", "--mv", "960", "--mhz-pct", "2.5", "--peak-mhz", "1000")
                .Resolve(TestCurves.Realistic());
            Assert.Equal(1025, targetMhz); // 1000 * 1.025, not 1000 * 1.25
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void CapPoints_DefaultsToTen()
    {
        Assert.Equal(10, Parse("undervolt", "--mv", "960").CapPoints);
        Assert.Equal(UndervoltRequest.DefaultCapPoints, Parse("undervolt", "--mv", "960").CapPoints);
    }

    [Fact]
    public void CapPoints_TakesTheGivenValue()
    {
        Assert.Equal(3, Parse("undervolt", "--mv", "960", "--cap-points", "3").CapPoints);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    public void CapPoints_BelowOne_Throws(string n)
    {
        Assert.Throws<NvApiException>(() => Parse("undervolt", "--mv", "960", "--cap-points", n));
    }

    [Fact]
    public void PersistAndRenameShortcut_DefaultOn_OptOutFlagsTurnThemOff()
    {
        var defaults = Parse("undervolt", "--mv", "960");
        Assert.True(defaults.Persist);
        Assert.True(defaults.RenameShortcut);

        var opted = Parse("undervolt", "--mv", "960", "--no-persist", "--no-shortcut-rename");
        Assert.False(opted.Persist);
        Assert.False(opted.RenameShortcut);
    }

    [Fact]
    public void ShortcutNameOverride_FromSaveShortcutOrShortcutName_ElseNull()
    {
        Assert.Null(Parse("undervolt", "--mv", "960").ShortcutNameOverride);
        Assert.Null(Parse("undervolt", "--mv", "960", "--save-shortcut").ShortcutNameOverride);
        Assert.Equal("My OC", Parse("undervolt", "--mv", "960", "--save-shortcut", "My OC").ShortcutNameOverride);
        Assert.Equal("My OC", Parse("undervolt", "--mv", "960", "--shortcut-name", "My OC").ShortcutNameOverride);
    }

    // --- Resolution (UndervoltRequest.Resolve) ---

    [Fact]
    public void AbsoluteVoltage_ResolvesUnchanged()
    {
        var (capMv, targetMhz) = Parse("undervolt", "--mv", "960").Resolve(TestCurves.Realistic());
        Assert.Equal(960, capMv);
        Assert.Null(targetMhz);
    }

    [Fact]
    public void VoltageOffset_AppliesToTheGivenPeak()
    {
        var (capMv, _) = Parse("undervolt", "--mv-offset", "-100", "--peak-mv", "1060")
            .Resolve(TestCurves.Realistic());
        Assert.Equal(960, capMv);
    }

    [Fact]
    public void VoltagePercent_AppliesToTheGivenPeak()
    {
        var (capMv, _) = Parse("undervolt", "--mv-pct", "-10", "--peak-mv", "1000")
            .Resolve(TestCurves.Realistic());
        Assert.Equal(900, capMv);
    }

    [Fact]
    public void AbsoluteClock_ResolvesUnchanged()
    {
        var (_, targetMhz) = Parse("undervolt", "--mv", "960", "--mhz", "2880").Resolve(TestCurves.Realistic());
        Assert.Equal(2880, targetMhz);
    }

    [Fact]
    public void ClockOffset_AppliesToTheGivenPeak()
    {
        var (capMv, targetMhz) = Parse("undervolt", "--mv", "960", "--mhz-offset", "-50", "--peak-mhz", "2880")
            .Resolve(TestCurves.Realistic());
        Assert.Equal(960, capMv);
        Assert.Equal(2830, targetMhz);
    }

    [Fact]
    public void ClockPercent_AppliesToTheGivenPeak()
    {
        var (_, targetMhz) = Parse("undervolt", "--mv", "960", "--mhz-pct", "5", "--peak-mhz", "2800")
            .Resolve(TestCurves.Realistic());
        Assert.Equal(2940, targetMhz); // 2800 * 1.05
    }

    [Fact]
    public void MissingPeakCoordinate_IsReadOffTheCurve()
    {
        // Only --peak-mhz is given; the peak voltage is interpolated from the curve, then the offset
        // applied. 2880 MHz falls between (1140 mV, 2850 MHz) and (1160 mV, 2900 MHz) -> 1152 mV.
        var (capMv, _) = Parse("undervolt", "--mv-offset", "-100", "--peak-mhz", "2880")
            .Resolve(TestCurves.Realistic());
        Assert.Equal(1052, capMv);
    }

    [Fact]
    public void MissingPeakCoordinateOnAnIdleCurve_Throws()
    {
        Assert.Throws<NvApiException>(
            () => Parse("undervolt", "--mv-offset", "-100", "--peak-mhz", "2880").Resolve(TestCurves.Collapsed()));
    }

    [Fact]
    public void CapAboveThePeak_Throws()
    {
        Assert.Throws<NvApiException>(
            () => Parse("undervolt", "--mv", "1100", "--peak-mv", "1000").Resolve(TestCurves.Realistic()));
    }

    [Theory]
    [InlineData("300")]    // below the plausible floor
    [InlineData("1300")]   // above the plausible ceiling
    public void ImplausibleVoltage_Throws(string mv)
    {
        Assert.Throws<NvApiException>(() => Parse("undervolt", "--mv", mv).Resolve(TestCurves.Realistic()));
    }

    [Theory]
    [InlineData("100")]     // below the plausible floor
    [InlineData("28800")]   // a 10x typo, above the ceiling
    [InlineData("NaN")]     // parses as a double but resolves to a nonsense clock
    public void ImplausibleFrequency_Throws(string mhz)
    {
        Assert.Throws<NvApiException>(
            () => Parse("undervolt", "--mv", "960", "--mhz", mhz).Resolve(TestCurves.Realistic()));
    }

    // --- Memory clock ---

    [Fact]
    public void NoMemoryFlag_MeansNoMemoryChange()
    {
        Assert.False(Parse("undervolt", "--mv", "960").UsesMemory);
    }

    [Fact]
    public void AbsoluteMemory_ResolvesToTheDeltaFromBase()
    {
        var request = Parse("undervolt", "--mv", "960", "--mem", "15000");
        Assert.True(request.UsesMemory);
        Assert.Equal((15000, 999_000), request.ResolveMemory(baseMemMhz: 14001));
    }

    [Fact]
    public void MemoryOffset_IsTheDeltaDirectly()
    {
        var (target, deltaKhz) = Parse("undervolt", "--mv", "960", "--mem-offset", "1000").ResolveMemory(14001);
        Assert.Equal(15001, target);
        Assert.Equal(1_000_000, deltaKhz);
    }

    [Fact]
    public void NegativeMemoryOffset_Downclocks()
    {
        var (target, deltaKhz) = Parse("undervolt", "--mv", "960", "--mem-offset", "-500").ResolveMemory(14001);
        Assert.Equal(13501, target);
        Assert.Equal(-500_000, deltaKhz);
    }

    [Fact]
    public void MemoryPercent_AppliesToBase()
    {
        var (target, deltaKhz) = Parse("undervolt", "--mv", "960", "--mem-pct", "10").ResolveMemory(14000);
        Assert.Equal(15400, target);
        Assert.Equal(1_400_000, deltaKhz);
    }

    [Fact]
    public void MemoryAtBase_IsAZeroDelta()
    {
        Assert.Equal((14001, 0), Parse("undervolt", "--mv", "960", "--mem", "14001").ResolveMemory(14001));
    }

    [Fact]
    public void TwoMemoryForms_Throws()
    {
        Assert.Throws<NvApiException>(() => Parse("undervolt", "--mv", "960", "--mem", "15000", "--mem-offset", "500"));
    }

    [Fact]
    public void ImplausibleMemory_Throws()
    {
        Assert.Throws<NvApiException>(
            () => Parse("undervolt", "--mv", "960", "--mem", "5000").ResolveMemory(baseMemMhz: 14001));
    }
}
