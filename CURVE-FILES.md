# Curve data files

Curve data travels as JSON documents, so you can plot it, archive it, or move it between installs.
There are two kinds:

- a **reference curve** — the card's full stock V/F table, as `set-reference-curve` saves it;
- a **tuning** — the offsets a tuning run applies: the tuned curve anchors and the memory offset.

## Exporting and importing

```powershell
# Save the reference and also export it:
simple-nvidia-undervolt set-reference-curve --out-curve-file stock.json
# Restore that reference later (validated against the card - same GPU, same curve anchors):
simple-nvidia-undervolt set-reference-curve --in-curve-file stock.json
# Tune and also export the resulting tuning:
simple-nvidia-undervolt --mv 900 --out-tuning-file tuning.json
# Export whatever tuning is currently applied (works for a foreign one, e.g. Afterburner's):
simple-nvidia-undervolt status --out-tuning-file tuning.json
# Re-apply an exported tuning exactly, offsets as data - no planning:
simple-nvidia-undervolt --in-tuning-file tuning.json
```

With `--dry-run`, `--out-tuning-file` exports the planned tuning without applying anything.

## Reference curve

The full stock table, one `{ mv, mhz }` entry per anchor, plus where it came from:

```json
{
  "type": "referenceCurve",
  "formatVersion": 1,
  "appVersion": "1.0.4",
  "savedAt": "2026-07-25 09:14",
  "gpuName": "NVIDIA GeForce RTX 5090",
  "gpuPciIds": "2B8510DE-89EE1043-000000A1-00002B85",
  "tempC": 30,
  "curve": [
    { "mv": 900, "mhz": 2392 },
    { "mv": 910, "mhz": 2415 },
    … one entry per anchor, the whole stock table …
    { "mv": 1235, "mhz": 3187 },
    { "mv": 1240, "mhz": 3195 }
  ]
}
```

Capture it stock, idle and cool: the reference exists to make tuning reproducible, and a curve
captured tuned or hot bakes those conditions into every plan made from it. Importing checks the
identity fields and the anchor voltages against the live card.

## Tuning

The tuned range of anchors — `offset` (MHz from stock) is what a re-apply writes, `mhz` (the
anchor's clock at the moment of tuning) is informative — plus `memoryOffset` (MHz from the factory
base clock):

```json
{
  "type": "tuning",
  "formatVersion": 1,
  "appVersion": "1.0.4",
  "savedAt": "2026-07-25 09:14",
  "gpuName": "NVIDIA GeForce RTX 5090",
  "gpuPciIds": "2B8510DE-89EE1043-000000A1-00002B85",
  "memoryOffset": 1400,
  "curve": [
    { "mv": 890, "offset": 480, "mhz": 2820 },
    { "mv": 895, "offset": 480, "mhz": 2842 },
    { "mv": 900, "offset": 480, "mhz": 2872 },
    { "mv": 910, "offset": 457, "mhz": 2872 },
    … the flattened anchors above the cap …
    { "mv": 1240, "offset": -323, "mhz": 2872 }
  ]
}
```

Re-applying matches each entry to the card's table by voltage and writes the offsets exactly as
validated — no re-planning. Anchors the document doesn't name stay stock. Identity is model-level
on purpose: chips of the same model bin differently, but offsets land on the target card's own
stock curve, so sharing a tuning between cards of the same model is part of what the format is
for — a structurally different curve table is refused by the anchor matching.

## Hand-written documents

Only `curve` is required; every other field may be omitted. The minimal tuning — raise the 900 mV
anchor by 100 MHz, leave everything else stock:

```json
{
  "curve": [
    { "mv": 900, "offset": 100 }
  ]
}
```

Each tuned anchor needs `mv` and `offset` and/or `mhz` — given only `mhz`, the offset resolves as
`mhz - stock` against the saved reference (or a live stock read). The minimal reference curve is
the same document as above with the metadata dropped — but its `curve` must still be the complete
stock table, so in practice start from an `--out-curve-file` export.

The optional fields degrade predictably: a document naming no GPU applies with a warning instead
of an identity check, and an absent `type`/`formatVersion` is accepted (the flag a file is handed
to already says which kind it holds). What must match, must match exactly: a named identity field,
a present `type`, the anchor voltages. Unknown fields are refused like unknown CLI flags — a
typo'd `memoryOffest` must fail, not silently change the tuning — and every offset is held to the
same plausibility bounds a planned tuning gets. Anything only a foreign tool sets — a voltage
boost, say — is outside the format, so it is neither exported nor re-applied.

## The app's own store files

The same documents are how the app stores its own state, under `data\` in the install directory
(`%ProgramFiles%\simple-nvidia-undervolt`):

- `data\reference-curve.json` — the saved reference curve (the same document `--out-curve-file`
  exports).
- `data\persisted-tuning.json` — the tuning the logon task re-applies (the same document
  `--out-tuning-file` exports). It lives under Program Files deliberately: the file is admin-only
  writable, and the elevated logon task must not consume user-writable data. `clear` removes it
  along with the task.
