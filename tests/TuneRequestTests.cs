namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for parsing and resolving the <c>tune</c> command's inputs: the mutually
/// exclusive voltage/clock forms, their validation, and how offsets/percentages resolve against the
/// curve and the peak operating point.</summary>
public class TuneRequestTests
{
    private static TuneRequest Parse(params string[] args) => TuneRequest.Parse(args);

    // --- Validation (TuneRequest.Parse) ---

    [Fact]
    public void NoVoltageCapAndNoMemory_Throws()
    {
        Assert.Throws<CliError>(() => Parse("tune"));
    }

    [Fact]
    public void MemoryAlone_Parses()
    {
        var request = Parse("tune", "--mem-pct", "5");
        Assert.False(request.Mv.IsSet);
        Assert.True(request.Mem.IsSet);
    }

    [Fact]
    public void ClockWithoutAVoltageCap_Throws()
    {
        Assert.Throws<CliError>(() => Parse("tune", "--mhz", "2800", "--mem-pct", "5"));
    }

    [Fact]
    public void CapPointsWithoutAVoltageCap_Throws()
    {
        Assert.Throws<CliError>(() => Parse("tune", "--mem-pct", "5", "--cap-points", "25"));
    }

    [Fact]
    public void TwoVoltageForms_Throws()
    {
        Assert.Throws<CliError>(() => Parse("tune", "--mv", "960", "--mv-offset", "-50"));
    }

    [Fact]
    public void TwoClockForms_Throws()
    {
        Assert.Throws<CliError>(
            () => Parse("tune", "--mv", "960", "--mhz", "2800", "--mhz-offset", "-50"));
    }

    [Theory]
    [InlineData("--mv-offset")]
    [InlineData("--mv-pct")]
    public void NonNegativeVoltageReduction_Throws(string flag)
    {
        Assert.Throws<CliError>(() => Parse("tune", flag, "50", "--peak-mv", "1060"));
    }

    [Fact]
    public void RelativeFormWithoutAReferencePoint_Throws()
    {
        Assert.Throws<CliError>(() => Parse("tune", "--mv-offset", "-100"));
    }

    [Fact]
    public void PeakWithoutARelativeForm_Throws()
    {
        // Only the offset/percentage forms consume the peak reference; accepted alongside absolute
        // values it would change nothing, so it is rejected like any other unconsumed option.
        Assert.Throws<CliError>(() => Parse("tune", "--mv", "960", "--mhz", "2880", "--peak-mv", "1060"));
    }

    [Fact]
    public void NonNumericValue_Throws()
    {
        Assert.Throws<CliError>(() => Parse("tune", "--mv", "lots"));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void NonFiniteValue_Throws(string value)
    {
        // double.TryParse accepts these, but a non-finite tuning value is meaningless - reject it at parse.
        Assert.Throws<CliError>(() => Parse("tune", "--mv", value));
    }

    [Theory]
    [InlineData("--no-persit")]   // a misspelled --no-persist must not be silently ignored
    [InlineData("--mvv")]
    [InlineData("--peak")]
    public void UnknownFlag_Throws(string flag)
    {
        Assert.Throws<CliError>(() => Parse("tune", "--mv", "960", flag));
    }

    [Theory]
    [InlineData("no-persist")]   // forgot the dashes - silently ignoring it would persist anyway
    [InlineData("-no-persist")]  // a single dash isn't a flag either
    public void StrayToken_Throws(string token)
    {
        Assert.Throws<CliError>(() => Parse("tune", "--mv", "950", token));
    }

    [Fact]
    public void DuplicatedFlag_Throws()
    {
        // The second value would silently lose to the first, so a duplicate must fail instead.
        Assert.Throws<CliError>(() => Parse("tune", "--mv", "900", "--mv", "950"));
    }

    [Fact]
    public void ValueFlagWithoutAValue_Throws()
    {
        Assert.Throws<CliError>(() => Parse("tune", "--mv"));
    }

    [Fact]
    public void ValueFlagFollowedByAnotherKnownFlag_Throws()
    {
        // A token spelled like a known flag is never consumed as a value: "--mv --silent" is a
        // missing value, not a voltage - and the guarantee keeps the raw pre-parse --silent scan
        // (InteractiveOutput.Install) in step with what a successful parse would decide.
        Assert.Throws<CliError>(() => Parse("tune", "--mv", "--silent"));
        Assert.Throws<CliError>(() => Parse("tune", "--mv", "--dry-run", "950"));
    }

    [Fact]
    public void KnownFlagsAndNegativeValues_AreAccepted()
    {
        // Negative numeric values start with a single dash, so they must not read as unknown flags.
        var request = Parse("tune", "--mv-offset", "-100", "--mhz-offset", "-50", "--peak-mv", "1060",
            "--cap-points", "8", "--no-persist", "--dry-run");
        Assert.Equal(-100, request.Mv.Offset);
        Assert.Equal(-50, request.Mhz.Offset);
    }

    [Fact]
    public void DecimalValue_ParsesInvariantly_RegardlessOfCulture()
    {
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            // A comma-decimal locale must not turn "2.5" into 25 (which would 10x the change).
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var (capMv, _) = Parse("tune", "--mv-pct", "-2.5", "--peak-mv", "1000")
                .Resolve(TestCurves.Realistic());
            Assert.Equal(975, capMv); // 1000 * 0.975, not 1000 * 0.75
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void CapPoints_DefaultsToTwentyFive()
    {
        Assert.Equal(25, Parse("tune", "--mv", "960").CapPoints);
    }

    [Fact]
    public void CapPoints_TakesTheGivenValue()
    {
        Assert.Equal(3, Parse("tune", "--mv", "960", "--cap-points", "3").CapPoints);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    public void CapPoints_BelowOne_Throws(string n)
    {
        Assert.Throws<CliError>(() => Parse("tune", "--mv", "960", "--cap-points", n));
    }

    [Fact]
    public void CapPoints_Fractional_Throws()
    {
        // Rounding 2.7 to 3 would silently apply a band the user didn't ask for.
        Assert.Throws<CliError>(() => Parse("tune", "--mv", "960", "--cap-points", "2.7"));
    }

    [Fact]
    public void Persist_DefaultOn_OptOutFlagTurnsItOff()
    {
        Assert.True(Parse("tune", "--mv", "960").Persist);
        Assert.False(Parse("tune", "--mv", "960", "--no-persist").Persist);
    }

    [Fact]
    public void ShortcutNameOverride_FromSaveShortcutsValue_ElseNull()
    {
        Assert.Null(Parse("tune", "--mv", "960").ShortcutNameOverride);
        Assert.Null(Parse("tune", "--mv", "960", "--save-shortcut").ShortcutNameOverride);
        Assert.Equal("My OC", Parse("tune", "--mv", "960", "--save-shortcut", "My OC").ShortcutNameOverride);
    }

    // --- Replay (--in-tuning-file / --apply-persisted) ---

    [Fact]
    public void TuningFilePath_ResolvesToAbsolute()
    {
        // Resolved at parse so every later use and error message names one unambiguous file.
        var request = Parse("tune", "--in-tuning-file", "t.json");
        Assert.True(Path.IsPathFullyQualified(request.InTuningFile!));
        Assert.EndsWith("t.json", request.InTuningFile);
    }

    [Fact]
    public void TuningFileAlone_IsAReplay()
    {
        var request = Parse("tune", "--in-tuning-file", "t.json", "--no-persist", "--dry-run");
        Assert.True(request.IsReplay);
        Assert.False(Parse("tune", "--mv", "960").IsReplay);
    }

    [Theory]
    [InlineData("--mv", "960")]
    [InlineData("--mhz", "2800")]
    [InlineData("--mem-offset", "500")]
    [InlineData("--cap-points", "8")]
    [InlineData("--save-shortcut", "My OC")]
    public void ReplayWithAnyOtherTuningOption_Throws(params string[] extra)
    {
        // The document is the whole tuning - an option that would steer it is a contradiction.
        Assert.Throws<CliError>(() => Parse(new[] { "tune", "--in-tuning-file", "t.json" }
            .Concat(extra).ToArray()));
    }

    [Fact]
    public void BothReplayForms_Throws()
    {
        Assert.Throws<CliError>(
            () => Parse("tune", "--in-tuning-file", "t.json", "--apply-persisted"));
    }

    // --- Resolution (TuneRequest.Resolve) ---

    [Fact]
    public void AbsoluteVoltage_ResolvesUnchanged()
    {
        var (capMv, targetMhz) = Parse("tune", "--mv", "960").Resolve(TestCurves.Realistic());
        Assert.Equal(960, capMv);
        Assert.Null(targetMhz);
    }

    [Fact]
    public void VoltageOffset_AppliesToTheGivenPeak()
    {
        var (capMv, _) = Parse("tune", "--mv-offset", "-100", "--peak-mv", "1060")
            .Resolve(TestCurves.Realistic());
        Assert.Equal(960, capMv);
    }

    [Fact]
    public void VoltagePercent_AppliesToTheGivenPeak()
    {
        var (capMv, _) = Parse("tune", "--mv-pct", "-10", "--peak-mv", "1000")
            .Resolve(TestCurves.Realistic());
        Assert.Equal(900, capMv);
    }

    [Fact]
    public void AbsoluteClock_ResolvesUnchanged()
    {
        var (_, targetMhz) = Parse("tune", "--mv", "960", "--mhz", "2880").Resolve(TestCurves.Realistic());
        Assert.Equal(2880, targetMhz);
    }

    [Fact]
    public void ClockOffset_AppliesToThePeakFrequencyReadOffTheCurve()
    {
        // The peak frequency is read off the curve at --peak-mv: 1160 mV is the anchor at 2900 MHz.
        var (capMv, targetMhz) = Parse("tune", "--mv", "960", "--mhz-offset", "-50", "--peak-mv", "1160")
            .Resolve(TestCurves.Realistic());
        Assert.Equal(960, capMv);
        Assert.Equal(2850, targetMhz);
    }

    [Fact]
    public void ClockPercent_AppliesToThePeakFrequencyReadOffTheCurve()
    {
        // 1000 mV is the anchor at 2500 MHz; 2500 * 1.05 = 2625.
        var (_, targetMhz) = Parse("tune", "--mv", "960", "--mhz-pct", "5", "--peak-mv", "1000")
            .Resolve(TestCurves.Realistic());
        Assert.Equal(2625, targetMhz);
    }

    [Fact]
    public void PeakFrequency_IsInterpolatedBetweenAnchors()
    {
        // 1150 mV falls between (1140 mV, 2850 MHz) and (1160 mV, 2900 MHz) -> 2875 MHz.
        var (_, targetMhz) = Parse("tune", "--mv", "960", "--mhz-offset", "-50", "--peak-mv", "1150")
            .Resolve(TestCurves.Realistic());
        Assert.Equal(2825, targetMhz);
    }

    [Fact]
    public void RelativeClockOnACollapsedCurve_Throws()
    {
        // The peak frequency can't be read off a curve whose frequency column has collapsed.
        Assert.Throws<CliError>(
            () => Parse("tune", "--mv", "960", "--mhz-offset", "-50", "--peak-mv", "1160")
                .Resolve(TestCurves.Collapsed()));
    }

    [Fact]
    public void RelativeVoltageOnACollapsedCurve_Resolves()
    {
        // A relative voltage alone needs no frequency read: the peak voltage is given directly.
        var (capMv, _) = Parse("tune", "--mv-offset", "-100", "--peak-mv", "1060")
            .Resolve(TestCurves.Collapsed());
        Assert.Equal(960, capMv);
    }

    [Fact]
    public void CapAboveThePeak_Throws()
    {
        // 1100 mV passes the plausible range and sits below the curve's 1180 mV top, but a cap above
        // the peak reference caps nothing. (The relative clock form is what brings the peak along.)
        Assert.Throws<CliError>(() => Parse("tune", "--mv", "1100", "--mhz-offset", "-50", "--peak-mv", "1000")
            .Resolve(TestCurves.Realistic()));
    }

    [Fact]
    public void CapAboveTheCurveMax_Throws()
    {
        // With no peak reference, only the curve's own top bounds the cap: 1190 mV passes the plausible
        // 400-1200 range but exceeds the highest anchor (1180 mV), so flattening there caps nothing.
        Assert.Throws<CliError>(() => Parse("tune", "--mv", "1190").Resolve(TestCurves.Realistic()));
    }

    [Fact]
    public void CapBelowTheCurveMin_Throws()
    {
        // 790 mV passes the plausible range but sits below the first writable curve anchor (800 mV).
        // Accepting it would apply a different cap than the command reports and persists.
        Assert.Throws<CliError>(() => Parse("tune", "--mv", "790").Resolve(TestCurves.Realistic()));
    }

    [Theory]
    [InlineData("300")]    // below the plausible floor
    [InlineData("1300")]   // above the plausible ceiling
    public void ImplausibleVoltage_Throws(string mv)
    {
        Assert.Throws<CliError>(() => Parse("tune", "--mv", mv).Resolve(TestCurves.Realistic()));
    }

    [Theory]
    [InlineData("100")]     // below the plausible floor
    [InlineData("28800")]   // a 10x typo, above the ceiling
    [InlineData("NaN")]     // parses as a double but resolves to a nonsense clock
    public void ImplausibleFrequency_Throws(string mhz)
    {
        Assert.Throws<CliError>(
            () => Parse("tune", "--mv", "960", "--mhz", mhz).Resolve(TestCurves.Realistic()));
    }

    // --- Memory clock ---

    [Fact]
    public void NoMemoryFlag_MeansNoMemoryChange()
    {
        Assert.False(Parse("tune", "--mv", "960").Mem.IsSet);
    }

    [Fact]
    public void AbsoluteMemory_ResolvesToTheDeltaFromBase()
    {
        var request = Parse("tune", "--mv", "960", "--mem", "15000");
        Assert.True(request.Mem.IsSet);
        Assert.Equal((15000, 999_000), request.ResolveMemory(baseMemMhz: 14001));
    }

    [Fact]
    public void MemoryOffset_IsTheDeltaDirectly()
    {
        var (target, deltaKhz) = Parse("tune", "--mv", "960", "--mem-offset", "1000").ResolveMemory(14001);
        Assert.Equal(15001, target);
        Assert.Equal(1_000_000, deltaKhz);
    }

    [Fact]
    public void NegativeMemoryOffset_Downclocks()
    {
        var (target, deltaKhz) = Parse("tune", "--mv", "960", "--mem-offset", "-500").ResolveMemory(14001);
        Assert.Equal(13501, target);
        Assert.Equal(-500_000, deltaKhz);
    }

    [Fact]
    public void MemoryPercent_AppliesToBase()
    {
        var (target, deltaKhz) = Parse("tune", "--mv", "960", "--mem-pct", "10").ResolveMemory(14000);
        Assert.Equal(15400, target);
        Assert.Equal(1_400_000, deltaKhz);
    }

    [Fact]
    public void MemoryAtBase_IsAZeroDelta()
    {
        Assert.Equal((14001, 0), Parse("tune", "--mv", "960", "--mem", "14001").ResolveMemory(14001));
    }

    [Fact]
    public void TwoMemoryForms_Throws()
    {
        Assert.Throws<CliError>(() => Parse("tune", "--mv", "960", "--mem", "15000", "--mem-offset", "500"));
    }

    [Theory]
    [InlineData("5000")]    // far below the base
    [InlineData("18000")]   // more than 25% above the base (14001 * 1.25 = 17501)
    public void ImplausibleMemory_Throws(string mem)
    {
        Assert.Throws<CliError>(
            () => Parse("tune", "--mv", "960", "--mem", mem).ResolveMemory(baseMemMhz: 14001));
    }

}
