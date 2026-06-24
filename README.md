# simple-nvidia-undervolt

A small Windows command-line alternative to MSI Afterburner for undervolting an NVIDIA GPU. It talks to
the driver directly — no background process — and caps voltage by flattening the V/F curve.

Undervolting an NVIDIA GPU shouldn't mean dragging points around a curve editor. **Only one segment
matters** — the max voltage and the clock you run there — because that's where the card sits at max
load, and it impacts your peak framerate. The rest of the curve is ignored at idle and irrelevant at max
load. To simplify, you can just set your desired voltage and frequency pair with this tool.

> ⚠️ **Should work on GTX 10 (Pascal), RTX 20 (Turing), RTX 30 (Ampere), RTX 40 (Ada),
> RTX 50 (Blackwell) generations, but currently validated only on Blackwell.**

### Examples

```powershell
# Cap 960 mV, hold 2880 MHz there (re-applied at logon):
simple-nvidia-undervolt undervolt --mv 960 --mhz 2880
# Cap 925 mV, keep the stock clock for that voltage; don't persist:
simple-nvidia-undervolt undervolt --mv 925 --no-persist
# Same as the first, via offsets from a watch reading of 1060 mV / 2880 MHz:
simple-nvidia-undervolt undervolt --mv-offset -100 --mhz-offset 0 --peak-mv 1060
# Percentage adjusting - 5% reduction of max voltage, 2% increase of max compute and memory frequency:
simple-nvidia-undervolt undervolt --mv-pct -5 --mhz-pct 2 -mem-pct 2 --peak-mv 1060
```

## Usage

```
undervolt [options]   Cap voltage (flatten the curve); optionally set the clock.
status                Show curve offset, memory clock, voltage boost (read-only).
watch                 Poll live core voltage/clock/temp/power, tracking the max (read-only).
clear                 Reset all tuning to stock.
```

`status` and `watch` are read-only and need no elevation. **`undervolt` and `clear` write and need
administrator rights; if run from a normal terminal they prompt for elevation.**
`undervolt` re-applies itself at logon by default (see below), so it survives a reboot
unless you pass `--no-persist`. Low-level NVAPI inspection commands are listed under
`--help-diagnostics` and in [DEVELOPMENT.md](DEVELOPMENT.md).

### undervolt options

```
Voltage cap (required, one of):
  --mv <n>          Cap at n mV.
  --mv-offset <n>   Cap at peak_mV + n          (n < 0).
  --mv-pct <n>      Cap at peak_mV * (1 + n/100) (n < 0).
Clock at the cap (optional, one of; omit = stock clock there):
  --mhz <n>         n MHz.
  --mhz-offset <n>  peak_MHz + n.
  --mhz-pct <n>     peak_MHz * (1 + n/100).
Memory clock (optional, one of; relative to the factory base clock):
  --mem <n>         n MHz.
  --mem-offset <n>  base_MHz + n.
  --mem-pct <n>     base_MHz * (1 + n/100).
Reference for the offset/pct forms (pass one; the other is read off the curve):
  --peak-mv <n>     Peak voltage under load (mV).
  --peak-mhz <n>    Peak clock under load (MHz).
Other:
  --cap-points <n>  Curve anchors holding the cap's offset, counting down from the cap (cap
                    included; default 10). 1 = only the cap point.
  --no-persist      Don't persist; by default a real run re-applies at logon (see below).
  --save-shortcut [name]
                    Drop a .lnk (specify name/path, otherwise auto-generated).
```

The offset/pct forms are relative to the real under-load operating point — read it from `watch` under a
sustained load. Each real run resets to stock first.

**Mechanism.** The cap anchor and every point above it are flattened to one frequency
(`ClkVfPointsSetControl`); the boost algorithm then pins voltage at the cap. A band of `--cap-points`
anchors ending at the cap shares the cap's offset, so if the realized voltage settles a bin or two below
the cap under load the clock doesn't fall off the steep curve back to stock — which otherwise costs a lot
of MHz when overclocking. Points below the band stay stock, so `--mhz` only raises the cap region. After
writing, `undervolt` reads the curve back to confirm.

### Persisting at startup

The `undervolt` command **persists by default**: it copies the app to
`%LOCALAPPDATA%\Programs\nvidia-simple-undervolt` and registers a Task Scheduler task that
re-applies the same undervolt at logon, elevated. If apply ever fails you get a message box
so you are aware. Pass `--no-persist` to skip persistence; `unpersist` removes an existing task.

### Saving a shortcut

`--save-shortcut` writes a `.lnk` into the current directory, named for the settings (e.g.
`Undervolt 960mV 2880MHz.lnk`). Double-clicking it re-applies that undervolt and shows the
result in a message box. Pair it with `--dry-run` to save the shortcut without applying now.

After a successful apply, the link for those settings in the current directory is renamed to
`[ACTIVE] ….lnk` and the marker is cleared from the other marked links there, so the folder shows at a
glance which profile is live. Pass `--no-shortcut-rename` to leave the files untouched.

```powershell
# Drop a reusable shortcut without applying:
simple-nvidia-undervolt undervolt --mv 960 --mhz 2880 --dry-run --save-shortcut
# Custom name (creates "Quiet.lnk"):
simple-nvidia-undervolt undervolt --mv 925 --dry-run --save-shortcut Quiet
```

## clear

Resets to stock: the V/F curve offsets (`ClkVfPointsSetControl` → 0, including an Afterburner flatten),
the memory clock offset (`SetPstates20`), and the core voltage boost (`SetCoreVoltageBoostPercent`).
`status` shows the memory offset against the factory base clock (`GetAllClockFrequencies`, base type),
since the GET only reports the offset-applied absolute.

## Development

Building from source, diagnostic commands, the NVAPI buffer layout, and how to
verify/port the offsets to another GPU: [DEVELOPMENT.md](DEVELOPMENT.md).
