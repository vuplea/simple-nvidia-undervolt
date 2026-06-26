# Development

## Verify your card

> ⚠️ Undervolting is validated only on RTX 5090 (Blackwell). The tuning-buffer byte offsets are
> hardware-specific, so on any other generation confirm it works before relying on it.

The **read path checks itself**: `status` (and `undervolt`) validate that the V/F curve decodes as a
real NVIDIA table, and warn or refuse if the offsets don't fit your card. So to confirm a card you only
need to check the harder **write path** — that a change actually lands and reverts:

1. Run `status`. If it prints no layout warning, the read offsets fit your card. (`layout` shows the
   detected offsets explicitly if you want to see them.)
2. Under a sustained 3D load, apply a small change and read it back, then undo it:
   ```powershell
   simple-nvidia-undervolt undervolt --mv 950 --mhz 2800 --no-persist
   simple-nvidia-undervolt status     # the core curve offset should now be non-stock
   simple-nvidia-undervolt clear      # back to stock
   ```
   If the undervolt applies, `status` shows the offset, and `clear` removes it, the write path works —
   the card is confirmed. Please open an issue/PR so the generation can be marked supported.
3. If `status` warns or `undervolt` refuses, the offsets don't match this card. Port them with the
   advanced steps below.

## Build

```powershell
dotnet build src -c Release
src/bin/simple-nvidia-undervolt.exe status
dotnet test tests   # pure-logic unit tests (no GPU required)
dotnet test e2e     # real GPU read/write tests; run as Administrator (see e2e/README.md)
```

The `e2e` project drives the real driver (writing tuning and reading it back). Its tests skip unless the
host is elevated with an NVIDIA GPU, and they restore the tuning they find — see
[e2e/README.md](e2e/README.md).

For a standalone, single-file executable with no .NET runtime dependency, publish with Native AOT (needs
the Visual C++ build tools / `vswhere.exe` on PATH):

```powershell
dotnet publish src -c Release
src/publish/simple-nvidia-undervolt.exe status
```

## Diagnostics

For inspecting the NVAPI tuning structs and confirming the layout on a given card. These are hidden from
the main `--help`; run `--help-diagnostics` to list them.

```
curve                       Dump the live V/F curve.
layout                      Detect the status-curve offsets and check them against the build.
voltage                     Live core voltage/clock/temp/power, one-shot (continuous is 'watch').
clocks                      Current/base/boost clocks.
scan <value>                Find a value across the tuning buffers.
snapshot / diff             Capture buffers, then show which words a change moved.
probe <hexId>               Find accepted (version, size) pairs for a function.
extent <hexId> <v> <sz>     Measure the real struct size the driver writes.
raw <hexId> <v> <sz>        Dump the 32-bit words a GET writes.
```

The curve lives in two index-aligned buffers: the **status** (`0x21537AD4` v1, 28-byte entries, freq
+0x08 / volt +0x0C) holds the live effective curve; the **control** (`0x23F1B133` v1, 36-byte entries,
signed kHz delta +0x18) holds the editable deltas. A control entry drives the next status anchor (so
anchor `i` ← control `i-1`). The status freq column collapses at idle, but the voltages stay valid.

## Porting the offsets to a new card

Curve handling depends on byte offsets in `src/NvApi.cs`:

```
control: base 0x64  stride 36  delta +0x18   (size 9248)
status:  base 0x40  stride 28  freq +0x08  volt +0x0C   (size 7208)
```

The **status (read) offsets** are detectable: run `layout`. It finds them from the live curve and prints
them next to the build's; under a 3D load it confirms the freq column too. Paste any differing values
into the `Status*` constants and rebuild.

The **control (write) offsets** carry no voltage to anchor on, so derive them with a write, in an
Administrator terminal:

1. `clear`, then `snapshot`.
2. In Afterburner's curve editor (Ctrl+F) raise one point (e.g. 950 mV) and flatten to its right; Apply.
3. `diff` — the changed `curveControl` words give the stride (their spacing) and the delta offset; the
   run starts at control entry `i-1` for the lowest moved status anchor `i` (skip the `i-1` and writes
   land one anchor off). Set the `Ctrl*` constants and rebuild.

Then re-run **Verify your card** above; if both the read check and a write round-trip hold, please open
an issue/PR so the generation can be marked supported.
