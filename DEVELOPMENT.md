# Development: build, diagnostics & confirming another card

The V/F-curve handling relies on the binary layout of a few NVAPI tuning structs, worked out
empirically so the tool can interoperate with the driver's published curve interface. These are the
diagnostic commands used to establish that layout, and the procedure to verify/port the offsets to a
different GPU.

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

For inspecting the NVAPI tuning structs and confirming the layout on a given card:

These are hidden from the main `--help`; run `--help-diagnostics` to list them.

```
curve                       Dump the live V/F curve.
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

## Confirming another card

Curve handling depends on byte offsets in `src/NvApi.cs`:

```
control: base 0x64  stride 36  delta +0x18   (size 9248)
status:  base 0x40  stride 28  freq +0x08  volt +0x0C   (size 7208)
```

Use Afterburner as a reference, in an Administrator terminal:

1. `clear -y`, then `snapshot`.
2. In Afterburner's curve editor (Ctrl+F) raise one point (e.g. 950 mV) and flatten to its right; Apply.
3. `diff` — the changed `curveControl` words reveal the stride and delta offset; `curve` should show the
   flat segment (confirms the status offsets).
4. From a changed control entry and the matching status entry, derive the two bases (mind the `i-1`
   offset). Adjust the constants, rebuild.
5. `status` should show the offset and `clear` should remove it (`curve` back to stock). If both hold,
   the card is confirmed. Sanity-check that `curve`'s clocks line up with `clocks`; if they don't,
   please open an issue/PR.

Until then, treat undervolting as unverified — everything goes through these curve buffers. If you
confirm a generation that isn't validated yet, please open an issue/PR so it can be marked supported.
