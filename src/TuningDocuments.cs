using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using static System.FormattableString;

namespace SimpleNvidiaUndervolt;

/// <summary>What identifies the GPU a document was captured from: the full name plus the PCI
/// identifiers (device, partner subsystem, revision), which pin the exact board model. Identity is
/// deliberately model-level, not per-unit: chips of the same model bin differently, but a document
/// carries offsets — applied to the target card's own stock curve — so another unit's document
/// lands as the same offsets on this card's binning, the way the shipped profiles do by design,
/// and sharing files between cards of the same model is part of what the format is for. What must
/// be refused is a structurally different curve table, and the PCI identifiers plus the anchor
/// matching catch that.</summary>
internal sealed record GpuIdentity(string Name, string PciIds)
{
    public static GpuIdentity Read(IntPtr gpu)
    {
        var (device, subSystem, revision, extDevice) = NvApi.GetPciIdentifiers(gpu);
        return new(
            NvApi.SafeFullName(gpu),
            $"{device:X8}-{subSystem:X8}-{revision:X8}-{extDevice:X8}");
    }
}

/// <summary>One curve anchor in a document, keyed by its voltage. In a reference curve an entry is
/// the stock point (<see cref="Mv"/>, <see cref="Mhz"/>). In a tuning it is a tuned anchor:
/// <see cref="Offset"/> (MHz, relative to stock) is what a replay applies, and <see cref="Mhz"/> —
/// the anchor's clock at the moment of tuning — is informative, unless the offset is absent, in
/// which case the offset resolves as <c>mhz - stock</c> against the reference curve (or the live
/// stock read when none is saved).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class CurveEntryDoc
{
    public int? Mv { get; set; }
    public int? Offset { get; set; }
    public int? Mhz { get; set; }
}

/// <summary>
/// A stock V/F reference curve: the full table as <see cref="CurveEntryDoc"/> points, plus the
/// metadata that pins where it came from — the GPU identity the import-side match checks against,
/// and the capture conditions. The same document is the stored form of the saved reference curve,
/// so a file exported by <c>set-reference-curve</c> imports back losslessly. Capture it stock, idle
/// and cool: the reference exists to make tuning reproducible, and a curve captured tuned or hot
/// bakes those conditions into every plan made from it.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ReferenceCurveDoc
{
    public string? Type { get; set; }
    public int FormatVersion { get; set; }
    public string? AppVersion { get; set; }
    public string? SavedAt { get; set; }

    /// <summary>The identity of the card the capture came from (see <see cref="GpuIdentity"/>).
    /// The tool's own exports fill both; in a hand-built document each is optional — what is named
    /// must match the live card, what is omitted is simply not checked.</summary>
    public string? GpuName { get; set; }

    public string? GpuPciIds { get; set; }

    public int? TempC { get; set; }

    [JsonConverter(typeof(CurveEntriesConverter))]
    public CurveEntryDoc[]? Curve { get; set; }
}

/// <summary>
/// An applied-tuning export: the knobs this tool tunes — the tuned curve anchors and the memory
/// offset (MHz) — with the identity of the card they were captured from. Replaying re-applies the
/// offsets; anchors are addressed by voltage against the live table (see
/// <see cref="TuningDocuments.ResolveCurveOffsetsKhz"/>), which is also the structural guard: an
/// anchor the card doesn't have refuses the document. The same document is what a persisting run
/// stores for the logon task (<see cref="PersistedTuning"/>).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class TuningDoc
{
    public string? Type { get; set; }
    public int FormatVersion { get; set; }
    public string? AppVersion { get; set; }
    public string? SavedAt { get; set; }

    /// <summary>The capture card's identity, optional field-by-field like
    /// <see cref="ReferenceCurveDoc.GpuName"/>.</summary>
    public string? GpuName { get; set; }

    public string? GpuPciIds { get; set; }

    /// <summary>The memory clock offset from the factory base, MHz.</summary>
    public int MemoryOffset { get; set; }

    /// <summary>The tuned range of anchors, ascending in voltage: from the first tuned anchor to
    /// the last, untouched interior anchors riding along at offset 0 (see
    /// <see cref="TuningDocuments.MakeTuningDoc"/>). Declared (and so serialized) last: the long
    /// list reads best after the scalar fields.</summary>
    [JsonConverter(typeof(CurveEntriesConverter))]
    public CurveEntryDoc[]? Curve { get; set; }
}

/// <summary>Renders curve entries one compact object per line — the default indentation would
/// spread every field onto a line of its own, burying a 127-point curve.</summary>
internal sealed class CurveEntriesConverter : JsonConverter<CurveEntryDoc[]>
{
    public override CurveEntryDoc[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("'curve' must be an array of anchor objects");
        }

        var entries = new List<CurveEntryDoc>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            entries.Add(JsonSerializer.Deserialize(ref reader, DocJsonContext.Default.CurveEntryDoc)
                        ?? throw new JsonException("each curve anchor must be an object"));
        }

        return entries.ToArray();
    }

    public override void Write(Utf8JsonWriter writer, CurveEntryDoc[] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (CurveEntryDoc entry in value)
        {
            // Invariant like every number this tool emits: the raw-value write is told to skip
            // validation, so a culture whose negative sign isn't '-' (ICU sv-SE) would otherwise
            // slip invalid JSON straight into the document.
            var fields = new List<string>(3) { Invariant($"\"mv\": {entry.Mv}") };
            if (entry.Offset is { } offset)
            {
                fields.Add(Invariant($"\"offset\": {offset}"));
            }

            if (entry.Mhz is { } mhz)
            {
                fields.Add(Invariant($"\"mhz\": {mhz}"));
            }

            // The writer applies no formatting around raw values, so the line break and indentation
            // (both documents hold their curve at nesting depth two) travel inside the raw payload
            // - leading whitespace, hence skipInputValidation.
            writer.WriteRawValue($"\n    {{ {string.Join(", ", fields)} }}", skipInputValidation: true);
        }

        writer.WriteEndArray();
    }
}

/// <summary>The source-generated serializer the AOT build requires (no reflection-based
/// serialization in a native image).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReferenceCurveDoc))]
[JsonSerializable(typeof(TuningDoc))]
[JsonSerializable(typeof(CurveEntryDoc))]
internal sealed partial class DocJsonContext : JsonSerializerContext;

/// <summary>
/// The JSON documents curve data travels in — between machines as files the user names, and within
/// this one as the install-directory files holding the saved reference
/// (<see cref="ReferenceCurve"/>) and the persisted tuning (<see cref="PersistedTuning"/>). Two
/// kinds — a reference curve (<see cref="ReferenceCurveDoc"/>) and an applied tuning
/// (<see cref="TuningDoc"/>) — told apart by which flag a file is handed to (a present <c>type</c>
/// field must agree). Only the curve itself is required; every metadata field is optional in a
/// hand-written document, with the tool's own exports filling them all. Parsing validates
/// structurally and physically — a document reaches a GPU write path only after the same
/// plausibility checks a live read gets, plus the identity and anchor matching against the live
/// card — so a hand-edited or mismatched file fails as a named error, never as a wrong write.
/// Unknown fields are refused like unknown CLI flags: a typo'd field (<c>memoryOffest</c>) must
/// fail rather than silently change the tuning.
/// </summary>
internal static class TuningDocuments
{
    public const string ReferenceCurveType = "referenceCurve";
    public const string TuningType = "tuning";
    private const int Version = 1;

    /// <summary>The source-generated options, minus the default escaper, which would render an app
    /// version like <c>0.0.0+abc</c> with the plus sign as a six-character unicode escape. Relaxed
    /// escaping is safe here: the documents hold only curve numbers and driver-reported identity
    /// strings, consumed as JSON, never embedded in HTML.</summary>
    private static readonly JsonSerializerOptions Options = new(DocJsonContext.Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    // --- Building documents ---

    public static ReferenceCurveDoc MakeReferenceCurveDoc(GpuIdentity gpu,
        IReadOnlyList<(int Mv, int Mhz)> points, int? tempC) => new()
    {
        Type = ReferenceCurveType,
        FormatVersion = Version,
        AppVersion = Product.Version,
        SavedAt = Timestamp(),
        GpuName = gpu.Name,
        GpuPciIds = gpu.PciIds,
        TempC = tempC,
        Curve = points.Select(p => new CurveEntryDoc { Mv = p.Mv, Mhz = p.Mhz }).ToArray(),
    };

    /// <summary>Builds the tuning document for an applied tuning: the contiguous range of anchors
    /// from the first tuned one to the last (see <see cref="GpuTuning.NameableTunedAnchors"/>),
    /// each named by its voltage on <paramref name="stock"/> with its offset and resulting clock.
    /// The zero-offset tails outside the range are trimmed; an untouched anchor inside it rides
    /// along at offset 0, which keeps the range contiguous — entries can only be matched back to
    /// anchors by voltage, and a gapless run of them pins each to its exact anchor even where
    /// adjacent anchors truncate to the same millivolt.</summary>
    public static TuningDoc MakeTuningDoc(GpuIdentity gpu, IReadOnlyList<(int Mv, int Mhz)> stock,
        AppliedTuning tuning)
    {
        int first = 0, last = -1;
        foreach ((int i, _) in GpuTuning.NameableTunedAnchors(stock.Count, tuning.CurveDeltasKhz))
        {
            if (last < 0)
            {
                first = i;
            }

            last = i;
        }

        // The range's top edge must not cut a same-millivolt run: with only the run's lower
        // (tuned) members named, the top-aligned resolution would land them on the untuned upper
        // ones. Extending across the run keeps it fully named; the lower edge needs no mirror -
        // a run cut there keeps exactly the tuned tail top-alignment restores.
        while (last >= 0 && last + 1 < stock.Count && stock[last + 1].Mv == stock[last].Mv)
        {
            last++;
        }

        var entries = new List<CurveEntryDoc>();
        for (int i = first; i <= last; i++)
        {
            int deltaKhz = i < tuning.CurveDeltasKhz.Length ? tuning.CurveDeltasKhz[i] : 0;
            int offsetMhz = (int)Math.Round(deltaKhz / 1000.0);
            entries.Add(new CurveEntryDoc
            {
                Mv = stock[i].Mv,
                Offset = offsetMhz,
                Mhz = stock[i].Mhz + offsetMhz,
            });
        }

        return new TuningDoc
        {
            Type = TuningType,
            FormatVersion = Version,
            AppVersion = Product.Version,
            SavedAt = Timestamp(),
            GpuName = gpu.Name,
            GpuPciIds = gpu.PciIds,
            Curve = entries.ToArray(),
            MemoryOffset = (int)Math.Round(tuning.MemoryDeltaKhz / 1000.0),
        };
    }

    public static string Render(ReferenceCurveDoc doc)
        => JsonSerializer.Serialize(doc, TypeInfo<ReferenceCurveDoc>());

    public static string Render(TuningDoc doc) => JsonSerializer.Serialize(doc, TypeInfo<TuningDoc>());

    // --- Parsing and validation ---

    /// <summary>Parses and validates a reference-curve document. <paramref name="source"/> names
    /// where the JSON came from (a file path, "the saved reference") for the error messages.</summary>
    public static ReferenceCurveDoc ParseReferenceCurveDoc(string json, string source)
    {
        RequireType(PeekType(json, source), ReferenceCurveType, source);
        ReferenceCurveDoc doc = Deserialize<ReferenceCurveDoc>(json, source)
                                ?? throw Invalid(source, "the document is empty");
        RequireSupportedVersion(doc.FormatVersion, source);
        if (doc.Curve is null)
        {
            throw Invalid(source, "'curve' is missing");
        }

        foreach (CurveEntryDoc entry in doc.Curve)
        {
            if (entry.Mv is null || entry.Mhz is null)
            {
                throw Invalid(source, "every reference curve anchor must carry 'mv' and 'mhz'");
            }

            // A stray offset most likely means tuned anchors were pasted in - and a reference built
            // from tuned data would bake that tuning into every plan made from it.
            if (entry.Offset is not null)
            {
                throw Invalid(source, "a reference curve carries no offsets - did you mean a "
                                      + "tuning document?");
            }
        }

        // The same physical judgment a live read gets: anything that doesn't look like a real
        // NVIDIA V/F table must not reach a curve plan.
        if (!GpuTuning.CurveVoltsPlausible(Points(doc)))
        {
            throw Invalid(source, "'curve' doesn't read as a recognized NVIDIA V/F table");
        }

        return doc;
    }

    /// <summary>Parses and validates a tuning document. The anchors must ascend in voltage — that
    /// is both the shape a real curve has and what lets <see cref="ResolveCurveOffsetsKhz"/> match
    /// them to the table in one ordered pass.</summary>
    public static TuningDoc ParseTuningDoc(string json, string source)
    {
        RequireType(PeekType(json, source), TuningType, source);
        TuningDoc doc = Deserialize<TuningDoc>(json, source)
                        ?? throw Invalid(source, "the document is empty");
        RequireSupportedVersion(doc.FormatVersion, source);
        if (doc.Curve is null)
        {
            throw Invalid(source, "'curve' is missing");
        }

        int previousMv = int.MinValue;
        foreach (CurveEntryDoc entry in doc.Curve)
        {
            if (entry.Mv is not { } mv)
            {
                throw Invalid(source, "every tuned anchor must carry 'mv'");
            }

            if (entry.Offset is null && entry.Mhz is null)
            {
                throw Invalid(source, $"the {mv} mV anchor must carry 'offset' (MHz from stock) "
                                      + "and/or 'mhz' (the absolute clock)");
            }

            // Non-decreasing, not strictly ascending: adjacent anchors of a real table can truncate
            // to the same millivolt.
            if (mv < previousMv)
            {
                throw Invalid(source, $"the tuned anchors must ascend in voltage ({mv} mV follows "
                                      + $"{previousMv} mV)");
            }

            previousMv = mv;
        }

        return doc;
    }

    // --- File IO ---

    public static ReferenceCurveDoc ReadReferenceCurveFile(string path)
        => ParseReferenceCurveDoc(ReadFile(path), path);

    public static TuningDoc ReadTuningFile(string path) => ParseTuningDoc(ReadFile(path), path);

    /// <summary>Writes a rendered document — a user-named export, or one of the app's own store
    /// files (whose <c>data</c> directory may not exist yet, hence the create) — returning the
    /// one-line log message. The environment refusing the write is the anticipated failure it is.</summary>
    public static string WriteFile(string path, string json, string what)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Write-then-move, so an interrupted save can't leave a torn document at the final
            // path: the store files are consumed by the elevated logon re-apply, which must find
            // either the previous document or the new one, never half of one.
            string temp = path + ".tmp";
            File.WriteAllText(temp, json + Environment.NewLine);
            File.Move(temp, path, overwrite: true);
            return $"Exported {what} to {path}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
            throw new CliError($"Writing {path} failed: {ex.Message}");
        }
    }

    // --- Domain conversion ---

    /// <summary>Whether the identity fields a document names all match the live card. Absent fields
    /// are simply not checked: the tool's own exports name both, and a hand-built document names
    /// what its author chose to pin.</summary>
    public static bool MatchesLive(string? gpuName, string? gpuPciIds, GpuIdentity live)
        => (gpuName is null || gpuName == live.Name)
           && (gpuPciIds is null || gpuPciIds == live.PciIds);

    /// <summary>Whether the document names any identity at all — what decides between the identity
    /// check and the skipped-with-a-warning path.</summary>
    public static bool NamesGpu(string? gpuName, string? gpuPciIds)
        => gpuName is not null || gpuPciIds is not null;

    /// <summary>A parsed reference document's curve as points.</summary>
    public static IReadOnlyList<(int Mv, int Mhz)> Points(ReferenceCurveDoc doc)
        => doc.Curve!.Select(e => (e.Mv!.Value, e.Mhz!.Value)).ToList();

    /// <summary>
    /// Resolves a tuning document's anchors into the per-anchor kHz delta array the control-table
    /// write takes, matching each entry to <paramref name="anchors"/> (the live table) by voltage
    /// in one ordered pass. Adjacent anchors of a real table can truncate to the same millivolt;
    /// the entries naming such a voltage fill its run of anchors from the top — the tool's own
    /// exports name a contiguous range ending at the last tuned anchor, so a run inside it is
    /// fully named and resolves positionally, while a run cut by the range's lower edge keeps
    /// only its tuned tail, which is exactly what top-alignment restores (a sparse hand-written
    /// document's entries land on the run's tail the same way). An entry naming a voltage the table
    /// doesn't have (or more entries at one voltage than the table has anchors) refuses the
    /// document: that is the structural guard against a different card's curve. An entry without
    /// an offset resolves it from its absolute clock against <paramref name="stock"/> — the saved
    /// reference curve, or a stock read, index-aligned with <paramref name="anchors"/> — invoked
    /// lazily, so a document of plain offsets (every export this tool writes) never needs it.
    /// Each resolved clock is held to the planned path's plausible range: a replay must not write
    /// offsets no plan could have produced.
    /// </summary>
    public static int[] ResolveCurveOffsetsKhz(TuningDoc doc, IReadOnlyList<(int Mv, int Mhz)> anchors,
        Func<IReadOnlyList<(int Mv, int Mhz)>> stock, string what)
    {
        IReadOnlyList<(int Mv, int Mhz)>? stockCurve = null;
        int StockMhzAt(int index) => (stockCurve ??= stock())[index].Mhz;

        // The resolved-clock bound below measures against the anchor's own clock - the clean
        // post-reset stock read on the real write path. It is skipped when the frequency column
        // isn't a clean read (a dry run against a transitional live table), like every other
        // frequency-dependent judgment.
        bool freqsReadable = GpuTuning.CurveFreqsReadable(anchors);

        int[] anchorIndices = MatchAnchors(doc, anchors, what);
        var deltasKhz = new int[anchors.Count];
        CurveEntryDoc[] entries = doc.Curve!;
        for (int e = 0; e < entries.Length; e++)
        {
            int i = anchorIndices[e];
            CurveEntryDoc entry = entries[e];
            long offsetMhz = entry.Offset ?? entry.Mhz!.Value - StockMhzAt(i);

            // A replay must not write what no plan could have produced (RunReplay holds the
            // memory offset to the same standard). The offset alone is bounded first, so a
            // hand-edited value can't overflow the kHz arithmetic below.
            long resolvedMhz = anchors[i].Mhz + offsetMhz;
            if (Math.Abs(offsetMhz) > TuneRequest.MaxPlausibleCoreClockMhz
                || (freqsReadable && resolvedMhz is < TuneRequest.MinPlausibleCoreClockMhz
                                                   or > TuneRequest.MaxPlausibleCoreClockMhz))
            {
                throw new CliError($"{what} tunes the {entry.Mv} mV anchor by {offsetMhz:+0;-0} MHz "
                    + $"to ~{resolvedMhz} MHz - outside the plausible "
                    + $"{TuneRequest.MinPlausibleCoreClockMhz}-{TuneRequest.MaxPlausibleCoreClockMhz} MHz "
                    + "core-clock range.");
            }

            deltasKhz[i] = (int)(offsetMhz * 1000);
        }

        return deltasKhz;
    }

    /// <summary>The structural half of a replay's validation: maps each document entry to its
    /// anchor index in <paramref name="anchors"/> by the matching rules above
    /// (<see cref="ResolveCurveOffsetsKhz"/>), refusing a document the table can't hold. Depends
    /// only on the voltage column — power-state independent and unaffected by any applied
    /// tuning — so it can vet a document against the pre-reset live table, where the tuned clocks
    /// would make the resolved-clock bound misjudge.</summary>
    public static int[] MatchAnchors(TuningDoc doc, IReadOnlyList<(int Mv, int Mhz)> anchors,
        string what)
    {
        CurveEntryDoc[] entries = doc.Curve!;
        var anchorIndices = new int[entries.Length];
        int a = 0;
        for (int e = 0; e < entries.Length;)
        {
            int mv = entries[e].Mv!.Value;
            int count = 1;
            while (e + count < entries.Length && entries[e + count].Mv == mv)
            {
                count++;
            }

            while (a < anchors.Count && anchors[a].Mv != mv)
            {
                a++;
            }

            int run = 0;
            while (a + run < anchors.Count && anchors[a + run].Mv == mv)
            {
                run++;
            }

            if (count > run)
            {
                throw new CliError(run == 0
                    ? $"{what} tunes a {mv} mV anchor this GPU's V/F curve doesn't have - it "
                      + "can't be applied here."
                    : $"{what} tunes {count} anchors at {mv} mV where this GPU's V/F curve has "
                      + $"{run} - it can't be applied here.");
            }

            for (int j = 0; j < count; j++)
            {
                int i = a + run - count + j;

                // Anchor 0 has no control entry, so an offset there would be silently dropped -
                // refuse it instead of applying less than the document promises.
                if (i == 0)
                {
                    throw new CliError($"{what} tunes the curve's lowest anchor ({mv} mV), which "
                                       + "has no control entry and cannot be offset.");
                }

                anchorIndices[e + j] = i;
            }

            a += run;
            e += count;
        }

        return anchorIndices;
    }

    /// <summary>Refuses a document whose named identity fields don't match the live card (a
    /// hand-built document may name one or none — naming none returns the warning to print
    /// instead).</summary>
    public static string? RequireMatchesGpu(string? gpuName, string? gpuPciIds, IntPtr gpu, string what)
    {
        if (NamesGpu(gpuName, gpuPciIds) && GpuIdentity.Read(gpu) is { } live
            && !MatchesLive(gpuName, gpuPciIds, live))
        {
            throw new CliError($"{what} was captured from '{gpuName ?? gpuPciIds}' and doesn't "
                               + $"match this GPU's identity ('{live.Name}') - it can't be applied here.");
        }

        return NamesGpu(gpuName, gpuPciIds)
            ? null
            : $"Warning: {what} names no GPU - skipping the identity check.";
    }

    internal static string Timestamp()
        => DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The capture-date phrase the messages that name a document use. <c>savedAt</c> is
    /// informational and optional (a hand-made document needn't carry one), so its absence degrades
    /// to a phrase rather than gating the document.</summary>
    public static string DescribeCapture(string? savedAt)
        => savedAt is null ? "capture date unknown" : $"saved {savedAt}";

    // --- Internals ---

    private static T? Deserialize<T>(string json, string source)
    {
        try
        {
            return JsonSerializer.Deserialize(json, TypeInfo<T>());
        }
        catch (JsonException ex)
        {
            throw Invalid(source, ex.Message);
        }
    }

    /// <summary>The document's <c>type</c> field alone, read ahead of the full parse: the strict
    /// unknown-field deserialization would trip over the other kind's fields first, burying the
    /// targeted wrong-kind message <see cref="RequireType"/> raises for a mixed-up file.</summary>
    private static string? PeekType(string json, string source)
    {
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(json);
            return parsed.RootElement.ValueKind == JsonValueKind.Object
                   && parsed.RootElement.TryGetProperty("type", out JsonElement type)
                   && type.ValueKind == JsonValueKind.String
                ? type.GetString()
                : null;
        }
        catch (JsonException ex)
        {
            throw Invalid(source, ex.Message);
        }
    }

    /// <summary>The type is optional — the flag a file is handed to already says what it holds, so
    /// a hand-written document needn't repeat it — but when present it must match: the wrong kind
    /// gets a targeted message, since handing a tuning export to a reference-curve flag (or vice
    /// versa) is the likely mistake, and "invalid file" wouldn't name it.</summary>
    private static void RequireType(string? type, string expected, string source)
    {
        if (type is null || type == expected)
        {
            return;
        }

        throw type is ReferenceCurveType or TuningType
            ? new CliError($"{source} holds a {Describe(type)} export, but a {Describe(expected)} "
                           + "export is needed here.")
            : Invalid(source, $"'type' must be '{expected}'");
    }

    private static string Describe(string type)
        => type == ReferenceCurveType ? "reference curve" : "tuning";

    /// <summary>An absent version means the current one — a hand-written document needn't state
    /// it — but a named one must be supported.</summary>
    private static void RequireSupportedVersion(int formatVersion, string source)
    {
        if (formatVersion != 0 && formatVersion != Version)
        {
            throw Invalid(source, $"formatVersion {formatVersion} isn't supported by this build "
                                  + $"(which reads version {Version})");
        }
    }

    private static CliError Invalid(string source, string why)
        => new($"{source} isn't a usable export: {why}.");

    private static string ReadFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
            throw new CliError($"Reading {path} failed: {ex.Message}");
        }
    }
}
