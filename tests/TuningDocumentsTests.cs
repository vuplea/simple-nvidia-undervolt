namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for the JSON curve/tuning documents: the render/parse round-trips, the structural
/// and physical validation that keeps hand-edited or mismatched documents away from a GPU write,
/// the identity matching, and the voltage-keyed anchor resolution. File IO stays a thin untested
/// shell around these.</summary>
public class TuningDocumentsTests
{
    private static readonly GpuIdentity Card =
        new("NVIDIA GeForce RTX 5080", "2C0210DE-11223344-000000A1-2C0210DE");

    /// <summary>A tuning document over <paramref name="stock"/> with the given per-anchor kHz
    /// deltas, exactly as an export builds one.</summary>
    private static TuningDoc Tuning(IReadOnlyList<(int Mv, int Mhz)> stock, int[] deltasKhz,
        int memoryDeltaKhz = 0)
        => TuningDocuments.MakeTuningDoc(Card, stock, new AppliedTuning(deltasKhz, memoryDeltaKhz));

    // --- Reference curve documents ---

    [Fact]
    public void ReferenceCurveDoc_RoundTrips()
    {
        var curve = TestCurves.Realistic();
        ReferenceCurveDoc doc = TuningDocuments.MakeReferenceCurveDoc(Card, curve, tempC: 38);

        ReferenceCurveDoc parsed = TuningDocuments.ParseReferenceCurveDoc(TuningDocuments.Render(doc), "test");

        Assert.Equal(38, parsed.TempC);
        Assert.Equal(curve, TuningDocuments.Points(parsed));
        Assert.True(TuningDocuments.MatchesLive(parsed.GpuName, parsed.GpuPciIds, Card));
    }

    [Fact]
    public void ParseReferenceCurveDoc_GivenATuningDoc_NamesTheMixUp()
    {
        string json = TuningDocuments.Render(Tuning(TestCurves.Realistic(), new int[20]));

        var error = Assert.Throws<CliError>(() => TuningDocuments.ParseReferenceCurveDoc(json, "f.json"));
        Assert.Contains("tuning export", error.Message);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"formatVersion": 99, "curve": []}""")]
    public void ParseReferenceCurveDoc_RejectsMalformedDocuments(string json)
    {
        Assert.Throws<CliError>(() => TuningDocuments.ParseReferenceCurveDoc(json, "f.json"));
    }

    [Fact]
    public void ParseReferenceCurveDoc_RejectsAnImplausibleCurve()
    {
        ReferenceCurveDoc doc = TuningDocuments.MakeReferenceCurveDoc(Card, TestCurves.Garbage(), tempC: null);

        Assert.Throws<CliError>(() => TuningDocuments.ParseReferenceCurveDoc(TuningDocuments.Render(doc), "f.json"));
    }

    [Fact]
    public void ParseReferenceCurveDoc_RejectsAnAnchorWithAnOffset()
    {
        // A stray offset most likely means tuned anchors were pasted in - and a reference built
        // from tuned data would bake that tuning into every plan made from it.
        ReferenceCurveDoc doc = TuningDocuments.MakeReferenceCurveDoc(Card, TestCurves.Realistic(), tempC: null);
        doc.Curve![5].Offset = -100;

        Assert.Throws<CliError>(() => TuningDocuments.ParseReferenceCurveDoc(TuningDocuments.Render(doc), "f.json"));
    }

    [Fact]
    public void Parse_RequiresOnlyTheCurve()
    {
        // Everything but the curve is optional in a hand-written document: the flag a file is
        // handed to decides its kind, an absent version means the current one, and metadata is
        // the tool's own exports' business.
        var curve = TestCurves.Realistic();
        string entries = string.Join(",", curve.Select(p => $"{{\"mv\":{p.Mv},\"mhz\":{p.Mhz}}}"));

        ReferenceCurveDoc reference = TuningDocuments.ParseReferenceCurveDoc(
            $"{{\"curve\":[{entries}]}}", "test");
        Assert.Equal(curve, TuningDocuments.Points(reference));

        TuningDoc tuning = TuningDocuments.ParseTuningDoc(
            """{"curve": [{"mv": 900, "mhz": 2500}]}""", "test");
        Assert.Single(tuning.Curve!);
        Assert.False(TuningDocuments.NamesGpu(tuning.GpuName, tuning.GpuPciIds));
    }

    // --- Tuning documents ---

    [Fact]
    public void TuningDoc_RoundTripsTheOffsets()
    {
        var stock = TestCurves.Realistic();
        var deltasKhz = new int[stock.Count];
        deltasKhz[10] = -120_000;
        deltasKhz[11] = -150_000;
        TuningDoc doc = Tuning(stock, deltasKhz, memoryDeltaKhz: 500_000);

        TuningDoc parsed = TuningDocuments.ParseTuningDoc(TuningDocuments.Render(doc), "test");

        Assert.Equal(500, parsed.MemoryOffset);
        Assert.Collection(parsed.Curve!,
            e =>
            {
                Assert.Equal((stock[10].Mv, -120, stock[10].Mhz - 120), (e.Mv, e.Offset, e.Mhz));
            },
            e =>
            {
                Assert.Equal((stock[11].Mv, -150, stock[11].Mhz - 150), (e.Mv, e.Offset, e.Mhz));
            });
    }

    [Fact]
    public void MakeTuningDoc_SkipsUntouchedAnchors_AndDeltasWithNoAnchorToName()
    {
        var stock = TestCurves.Realistic();
        var deltasKhz = new int[stock.Count + 5];
        deltasKhz[0] = -50_000;               // anchor 0 has no control entry - never exported
        deltasKhz[7] = -100_000;
        deltasKhz[stock.Count + 2] = -60_000; // past the visible curve - no voltage to name it by

        TuningDoc doc = Tuning(stock, deltasKhz);

        CurveEntryDoc entry = Assert.Single(doc.Curve!);
        Assert.Equal(stock[7].Mv, entry.Mv);
        Assert.Equal(-100, entry.Offset);
    }

    [Fact]
    public void MakeTuningDoc_ExportsTheContiguousTunedRange_TrimmingZeroTails()
    {
        // Only the range from the first tuned anchor to the last is serialized. An untouched
        // anchor inside it rides along at offset 0, keeping the range gapless - entries can only
        // be matched back to anchors by voltage, and a gapless run pins each to its exact anchor
        // even where adjacent anchors truncate to the same millivolt.
        var stock = TestCurves.Realistic();
        var deltasKhz = new int[stock.Count];
        deltasKhz[8] = -50_000;
        deltasKhz[10] = -100_000;              // 9 untouched, inside the range

        TuningDoc doc = Tuning(stock, deltasKhz);

        Assert.Equal(new[] { (stock[8].Mv, -50), (stock[9].Mv, 0), (stock[10].Mv, -100) },
            doc.Curve!.Select(e => (e.Mv!.Value, e.Offset!.Value)));
        Assert.Equal(deltasKhz, Resolve(doc, stock));
    }

    [Theory]
    [InlineData("""{"curve": [{"mv": 900, "offset": -100}], "memoryOffest": 500}""")]
    [InlineData("""{"curve": [{"mv": 900, "offest": -100}]}""")]
    public void ParseTuningDoc_RefusesUnknownFields(string json)
    {
        // Like the CLI's own strict argv parsing: on a tool that writes hardware, a typo'd field
        // must fail rather than silently change the tuning.
        Assert.Throws<CliError>(() => TuningDocuments.ParseTuningDoc(json, "f.json"));
    }

    [Fact]
    public void ParseReferenceCurveDoc_RefusesUnknownFields()
    {
        Assert.Throws<CliError>(() => TuningDocuments.ParseReferenceCurveDoc(
            """{"curve": [], "temperature": 38}""", "f.json"));
    }

    [Fact]
    public void ParseTuningDoc_GivenAReferenceCurveDoc_NamesTheMixUp()
    {
        string json = TuningDocuments.Render(
            TuningDocuments.MakeReferenceCurveDoc(Card, TestCurves.Realistic(), tempC: null));

        var error = Assert.Throws<CliError>(() => TuningDocuments.ParseTuningDoc(json, "f.json"));
        Assert.Contains("reference curve export", error.Message);
    }

    [Theory]
    [InlineData("""{"curve": [{"offset": -100}]}""")]              // anchor without a voltage
    [InlineData("""{"curve": [{"mv": 900}]}""")]                   // neither offset nor mhz
    [InlineData("""{"curve": [{"mv": 900, "offset": -100}, {"mv": 850, "offset": -100}]}""")] // descending
    public void ParseTuningDoc_RejectsMalformedAnchors(string json)
    {
        Assert.Throws<CliError>(() => TuningDocuments.ParseTuningDoc(json, "f.json"));
    }

    // --- Voltage-keyed anchor resolution (what a replay writes) ---

    [Fact]
    public void ResolveCurveOffsets_MapsAnchorsByVoltage()
    {
        var stock = TestCurves.Realistic();
        var deltasKhz = new int[stock.Count];
        deltasKhz[10] = -120_000;
        deltasKhz[12] = -150_000;
        TuningDoc doc = Tuning(stock, deltasKhz);

        Assert.Equal(deltasKhz, Resolve(doc, stock));
    }

    [Fact]
    public void ResolveCurveOffsets_ResolvesAnAbsoluteClockAgainstTheStockCurve()
    {
        var stock = TestCurves.Realistic();
        var doc = new TuningDoc
        {
            Curve = new[] { new CurveEntryDoc { Mv = stock[10].Mv, Mhz = stock[10].Mhz - 120 } },
        };

        Assert.Equal(-120_000, Resolve(doc, stock)[10]);
    }

    [Fact]
    public void ResolveCurveOffsets_NeedsTheStockCurveOnlyForAbsoluteClockAnchors()
    {
        // The stock curve resolves lazily: a document of plain offsets - every export this tool
        // writes - must replay (and dry-run) without one, even where recovering it would fail.
        var stock = TestCurves.Realistic();
        var doc = new TuningDoc
        {
            Curve = new[] { new CurveEntryDoc { Mv = stock[10].Mv, Offset = -100 } },
        };

        int[] deltasKhz = TuningDocuments.ResolveCurveOffsetsKhz(doc, stock,
            () => throw new InvalidOperationException("the stock curve must not be read"), "The tuning");

        Assert.Equal(-100_000, deltasKhz[10]);
    }

    [Fact]
    public void ResolveCurveOffsets_TellsAdjacentSameMillivoltAnchorsApartByPosition()
    {
        // The raw table ascends in microvolts, so adjacent anchors can truncate to the same
        // millivolt - the ordered pass assigns the entries across the matching run in order.
        var stock = TestCurves.Realistic();
        stock[11] = (stock[10].Mv, stock[11].Mhz);
        var doc = new TuningDoc
        {
            Curve = new[]
            {
                new CurveEntryDoc { Mv = stock[10].Mv, Offset = -100 },
                new CurveEntryDoc { Mv = stock[10].Mv, Offset = -200 },
            },
        };

        int[] deltasKhz = Resolve(doc, stock);

        Assert.Equal(-100_000, deltasKhz[10]);
        Assert.Equal(-200_000, deltasKhz[11]);
    }

    [Fact]
    public void ResolveCurveOffsets_FillsASameMillivoltRunFromItsTop()
    {
        // Exports omit untouched anchors and the tuned anchors run to the table's top, so when
        // only the upper of two same-millivolt anchors was tuned, the single entry must land
        // there, not on the first match.
        var stock = TestCurves.Realistic();
        stock[11] = (stock[10].Mv, stock[11].Mhz);
        var doc = new TuningDoc
        {
            Curve = new[] { new CurveEntryDoc { Mv = stock[10].Mv, Offset = -100 } },
        };

        int[] deltasKhz = Resolve(doc, stock);

        Assert.Equal(0, deltasKhz[10]);
        Assert.Equal(-100_000, deltasKhz[11]);
    }

    [Fact]
    public void ExportedTuning_RoundTrips_WhenAnUntunedNeighbourSharesTheFlatStartsMillivolt()
    {
        // The straddle case top-alignment exists for: the untouched anchor just below the flatten
        // truncates to the flat start's millivolt, so a first-match resolution would shift the
        // flatten one anchor down onto the untouched neighbour.
        var stock = TestCurves.Realistic();
        stock[12] = (stock[11].Mv, stock[12].Mhz);
        var plan = GpuTuning.BuildCurvePlan(stock, capMv: stock[10].Mv, targetMhz: null, capPoints: 8);
        TuningDoc doc = Tuning(stock, plan.DeltasKhz);

        Assert.Equal(plan.DeltasKhz, Resolve(doc, stock));
    }

    [Fact]
    public void ExportedTuning_RoundTrips_WhenTheLastTunedAnchorsUpperNeighbourSharesItsMillivolt()
    {
        // The mirror straddle at the range's top edge: the anchor above the last tuned one is
        // untouched but truncates to the same millivolt. The export extends the range across the
        // run (the untuned member rides along at offset 0), because a top-aligned resolution of
        // the tuned member alone would land its offset on the untuned neighbour.
        var stock = TestCurves.Realistic();
        stock[19] = (stock[18].Mv, stock[19].Mhz);
        var deltasKhz = new int[stock.Count];
        for (int i = 10; i <= 18; i++)
        {
            deltasKhz[i] = -50_000;            // tuned through 18; 19 untouched at the same mv
        }

        TuningDoc doc = Tuning(stock, deltasKhz);

        Assert.Equal((stock[18].Mv, 0), (doc.Curve![^1].Mv!.Value, doc.Curve![^1].Offset!.Value));
        Assert.Equal(deltasKhz, Resolve(doc, stock));
    }

    [Fact]
    public void ResolveCurveOffsets_RefusesAnAnchorTheCurveDoesntHave()
    {
        // The structural guard against a different card's table.
        var stock = TestCurves.Realistic();
        var doc = new TuningDoc
        {
            Curve = new[] { new CurveEntryDoc { Mv = stock[10].Mv + 1, Offset = -100 } },
        };

        Assert.Throws<CliError>(() => Resolve(doc, stock));
    }

    [Fact]
    public void ResolveCurveOffsets_RefusesMoreEntriesThanTheCurveHasAtAVoltage()
    {
        var stock = TestCurves.Realistic();
        var doc = new TuningDoc
        {
            Curve = new[]
            {
                new CurveEntryDoc { Mv = stock[10].Mv, Offset = -100 },
                new CurveEntryDoc { Mv = stock[10].Mv, Offset = -200 },
            },
        };

        Assert.Throws<CliError>(() => Resolve(doc, stock));
    }

    [Fact]
    public void ResolveCurveOffsets_RefusesTheUnwritableLowestAnchor()
    {
        // Anchor 0 has no control entry, so an offset there would be silently dropped.
        var stock = TestCurves.Realistic();
        var doc = new TuningDoc
        {
            Curve = new[] { new CurveEntryDoc { Mv = stock[0].Mv, Offset = -100 } },
        };

        Assert.Throws<CliError>(() => Resolve(doc, stock));
    }

    [Theory]
    [InlineData(-2400)]        // resolves below the plausible floor
    [InlineData(2000)]         // resolves above the plausible ceiling
    [InlineData(int.MinValue)] // would overflow the kHz arithmetic
    public void ResolveCurveOffsets_RefusesAnImplausibleResolvedClock(int offsetMhz)
    {
        // The same net a planned run's resolved clock passes: a replay must not write offsets no
        // plan could have produced.
        var stock = TestCurves.Realistic();        // anchor 10 = (1000 mV, 2500 MHz)
        var doc = new TuningDoc
        {
            Curve = new[] { new CurveEntryDoc { Mv = stock[10].Mv, Offset = offsetMhz } },
        };

        Assert.Throws<CliError>(() => Resolve(doc, stock));
    }

    [Theory]
    [InlineData(-8)]     // a plan's own cap band offset
    [InlineData(-1100)]  // deeper than any plan on this curve - the exemption doesn't judge size
    public void ResolveCurveOffsets_ExemptsFloorPinnedAnchorsFromTheResolvedMinimum(int offsetMhz)
    {
        // At deep idle - the state the logon re-apply runs in - the lowest anchors read at the
        // idle floor clock, not their stock clocks, so a negative offset there resolves below
        // the plausible minimum without being implausible at all: the written delta lands on the
        // driver's true table. The 202 MHz floor sits two above the 200 MHz minimum - what makes
        // even the -8 case resolve under it.
        var idle = TestCurves.IdleFloorPinned(pinned: 8, floorMhz: 202);
        var doc = new TuningDoc
        {
            Curve = new[] { new CurveEntryDoc { Mv = idle[5].Mv, Offset = offsetMhz } },
        };

        Assert.Equal(offsetMhz * 1000, Resolve(doc, idle)[5]);
    }

    [Fact]
    public void ResolveCurveOffsets_ExemptsAFloorAnchorBehindAReadWobble()
    {
        // A clean read may wobble a pinned anchor up a bin or two (202, 220, 202 stays within
        // the benign-dip tolerance); the floor anchors after it are exempt all the same - the
        // floor test is per anchor, not a run broken by the first excursion.
        var idle = TestCurves.IdleFloorPinned(pinned: 8, floorMhz: 202);
        idle[3] = (idle[3].Mv, 220);
        var doc = new TuningDoc
        {
            Curve = new[] { new CurveEntryDoc { Mv = idle[5].Mv, Offset = -8 } },
        };

        Assert.Equal(-8000, Resolve(doc, idle)[5]);
    }

    [Fact]
    public void ResolveCurveOffsets_StillHoldsUnpinnedAnchorsToTheResolvedMinimum()
    {
        // Above the pinned run the read is the anchor's real clock, so the minimum stands.
        var idle = TestCurves.IdleFloorPinned(pinned: 8, floorMhz: 202);   // anchor 8 reads 402
        var doc = new TuningDoc
        {
            Curve = new[] { new CurveEntryDoc { Mv = idle[8].Mv, Offset = -300 } },
        };

        Assert.Throws<CliError>(() => Resolve(doc, idle));
    }

    [Fact]
    public void ResolveCurveOffsets_StillHoldsFloorPinnedAnchorsToTheResolvedCeiling()
    {
        // Only the minimum is exempt at the floor - a clock above any real core is refused
        // everywhere. +3900 stays under the offset-magnitude bound, so the ceiling alone judges
        // (202 + 3900 = 4102).
        var idle = TestCurves.IdleFloorPinned(pinned: 8, floorMhz: 202);
        var doc = new TuningDoc
        {
            Curve = new[] { new CurveEntryDoc { Mv = idle[5].Mv, Offset = 3900 } },
        };

        Assert.Throws<CliError>(() => Resolve(doc, idle));
    }

    [Fact]
    public void ExportedPlan_ResolvesAgainstAnIdleRead()
    {
        // The logon path end to end: a tuning planned and exported awake re-applies at deep
        // idle, where the baseline read pins the lowest anchors at the floor. The cap band's
        // small negative offsets land on those anchors and must resolve to the deltas the plan
        // wrote.
        var awake = TestCurves.Realistic();
        var plan = GpuTuning.BuildCurvePlan(awake, capMv: awake[10].Mv, targetMhz: null, capPoints: 8);
        TuningDoc doc = Tuning(awake, plan.DeltasKhz);

        Assert.Equal(plan.DeltasKhz, Resolve(doc, TestCurves.IdleFloorPinned(pinned: 8, floorMhz: 202)));
    }

    [Fact]
    public void ResolveCurveOffsets_SkipsTheResolvedBoundOnAnUnreadableTable()
    {
        // A dry run can resolve against a transitional live table; the resolved-clock judgments
        // are meaningless there and must not fire (the offset-magnitude bound still does).
        var curve = TestCurves.Realistic();
        curve[7] = (curve[7].Mv, 50);              // a collapsed point - not a clean read
        var doc = new TuningDoc
        {
            Curve = new[] { new CurveEntryDoc { Mv = curve[10].Mv, Offset = -2450 } },
        };

        Assert.Equal(-2_450_000, Resolve(doc, curve)[10]);
    }

    [Fact]
    public void ResolveCurveOffsets_RefusesAnAbsoluteClockAtAFloorPinnedAnchor()
    {
        // An absolute clock derives its offset from the stock clock at the anchor, which a
        // floor-pinned stock read doesn't hold - deriving against the pinned 202 would turn a
        // mild target into a huge positive delta. A named refusal, not a wrong write.
        var idle = TestCurves.IdleFloorPinned(pinned: 8, floorMhz: 202);
        var doc = new TuningDoc
        {
            Curve = new[] { new CurveEntryDoc { Mv = idle[5].Mv, Mhz = 900 } },
        };

        Assert.Throws<CliError>(() => Resolve(doc, idle));
    }

    [Fact]
    public void ResolveCurveOffsets_ResolvesAnAbsoluteClockAgainstASavedReferenceAtIdle()
    {
        // With a saved reference the stock clock is known regardless of power state: the offset
        // derives from the reference, and the pinned anchor's minimum exemption lets the small
        // result through.
        var idle = TestCurves.IdleFloorPinned(pinned: 8, floorMhz: 202);
        var reference = TestCurves.Realistic();
        var doc = new TuningDoc
        {
            Curve = new[] { new CurveEntryDoc { Mv = idle[5].Mv, Mhz = reference[5].Mhz - 120 } },
        };

        int[] deltasKhz = TuningDocuments.ResolveCurveOffsetsKhz(doc, idle, () => reference, "The tuning");

        Assert.Equal(-120_000, deltasKhz[5]);
    }

    private static int[] Resolve(TuningDoc doc, IReadOnlyList<(int Mv, int Mhz)> stock)
        => TuningDocuments.ResolveCurveOffsetsKhz(doc, stock, () => stock, "The tuning");

    // --- Identity matching (field-wise: what a document names must match; the rest is unchecked) ---

    [Fact]
    public void MatchesLive_ChecksTheFieldsTheDocumentNames()
    {
        Assert.True(TuningDocuments.MatchesLive(Card.Name, Card.PciIds, Card));
        Assert.False(TuningDocuments.MatchesLive(Card.Name, Card.PciIds,
            Card with { Name = "NVIDIA GeForce RTX 5070" }));
        Assert.False(TuningDocuments.MatchesLive(Card.Name, Card.PciIds,
            Card with { PciIds = "2F0410DE-11223344-000000A1-2F0410DE" }));
    }

    [Fact]
    public void MatchesLive_SkipsTheFieldsTheDocumentOmits()
    {
        // A hand-built document names what its author chose to pin - down to nothing at all.
        Assert.True(TuningDocuments.MatchesLive(Card.Name, null, Card));
        Assert.False(TuningDocuments.MatchesLive("NVIDIA GeForce RTX 5070", null, Card));
        Assert.True(TuningDocuments.MatchesLive(null, Card.PciIds, Card));
        Assert.True(TuningDocuments.MatchesLive(null, null, Card));
    }

    [Fact]
    public void NamesGpu_OnlyWhenAnyIdentityFieldIsPresent()
    {
        // What decides between the identity check and the skipped-with-a-warning path.
        Assert.True(TuningDocuments.NamesGpu(Card.Name, null));
        Assert.True(TuningDocuments.NamesGpu(null, Card.PciIds));
        Assert.False(TuningDocuments.NamesGpu(null, null));
    }

    [Fact]
    public void DescribeCapture_DegradesWhenTheDateIsAbsent()
    {
        Assert.Equal("saved 2026-07-23 10:00", TuningDocuments.DescribeCapture("2026-07-23 10:00"));
        Assert.Equal("capture date unknown", TuningDocuments.DescribeCapture(null));
    }
}
