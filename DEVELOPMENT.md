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
layout                      Auto-detect the curve buffer offsets and check them against the build.
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

These NVAPI layouts have been stable across generations, so they most likely already fit. To confirm,
in an Administrator terminal with the GPU under a 3D load (so the curve's clocks are live):

1. **Read matches Afterburner.** `curve`'s voltage anchors and clocks should line up with Afterburner's
   curve editor (Ctrl+F), and with `clocks`. If they do, the status (read) offsets are right.
2. **A write round-trips.** `undervolt --mv <peak> --mhz <x> --no-persist` — then `curve`/`status` should
   show the flat cap, and `clear` should put it back to stock. That exercises the control (write) offsets.

If both hold, the card is confirmed — please open an issue/PR so the generation can be marked supported.

Only if step 1 *doesn't* match does the layout differ on this card; re-derive the offsets:

1. **Status** — run `layout`. It scans the live status buffer for the ascending-voltage array and prints
   the detected `base / stride / volt / freq`, flagging whether they match the build. Paste any differing
   values into the `Status*` constants in `src/NvApi.cs`. (The voltage axis reads at idle; run under load
   so it can also detect the freq column.)
2. **Control** — it carries no voltage to anchor on, so derive it with a write: `snapshot`, nudge one curve
   point in Afterburner, then `diff` (or `scan <deltaKhz>`). The changed `curveControl` words give the
   stride (their spacing) and the delta offset; the run starts at control entry `i-1` for the lowest moved
   status anchor `i` — skip the `i-1` and you land one stride off (reads fine, but writes hit the
   neighbour). Set the `Ctrl*` constants to match.
3. Rebuild and re-run the confirmation above.

Until confirmed, treat undervolting as unverified — everything goes through these curve buffers.
