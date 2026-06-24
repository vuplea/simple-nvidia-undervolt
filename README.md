# simple-nvidia-undervolt

A small Windows command-line alternative to MSI Afterburner for undervolting an NVIDIA GPU. It talks to
the driver directly — no background process — and caps voltage by flattening the V/F curve.

Undervolting an NVIDIA GPU shouldn't mean dragging 127 points around a curve editor. **Only one segment
matters** — the max voltage and the clock you run there — because that's where the card sits at max
load, and it impacts your peak framerate. The rest of the curve is ignored at idle and irrelevant at max
load. To simplify, you can just set your desired voltage and frequency pair with this tool.

> ⚠️ **Should work on GTX 10 (Pascal), RTX 20 (Turing), RTX 30 (Ampere), RTX 40 (Ada),
> RTX 50 (Blackwell) generations, but currently validated only on Blackwell.**

## Usage

```
undervolt [options]   Cap voltage (flatten the curve); optionally set the clock.
status                Show curve offset, memory clock, voltage boost (read-only).
watch                 Poll live core voltage/clock/temp/power, tracking the max (read-only).
clear                 Reset all tuning to stock.
```

`status` and `watch` are read-only and need no elevation. **`undervolt` and `clear` write and must run
from an Administrator terminal.** Changes are live-only: they revert on reboot or when Afterburner
re-applies a profile, and don't touch saved profiles. Low-level NVAPI inspection commands are listed
under `--help-diagnostics` and in [DEVELOPMENT.md](DEVELOPMENT.md).

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
  --persist         Re-apply this undervolt automatically at logon (see below).
  --msgbox          Also show errors in a Windows message box.
  --dry-run         Print the changes without writing.
```

The offset/pct forms are relative to the real under-load operating point — read it from `watch` under a
sustained load. Each real run resets to stock first.

```powershell
# Cap 960 mV, hold 2880 MHz there:
simple-nvidia-undervolt undervolt --mv 960 --mhz 2880
# Same, via offsets from a watch reading of 1060 mV / 2880 MHz:
simple-nvidia-undervolt undervolt --mv-offset -100 --mhz-offset 0 --peak-mv 1060
# Cap 925 mV, keep stock clock there:
simple-nvidia-undervolt undervolt --mv 925
# Cap 960 mV and push the memory clock +1500 MHz over base:
simple-nvidia-undervolt undervolt --mv 960 --mem-offset 1500
```

**Mechanism.** The cap anchor and every point above it are flattened to one frequency
(`ClkVfPointsSetControl`); the boost algorithm then pins voltage at the cap. A band of `--cap-points`
anchors ending at the cap shares the cap's offset, so if the realized voltage settles a bin or two below
the cap under load the clock doesn't fall off the steep curve back to stock — which otherwise costs a lot
of MHz when overclocking. Points below the band stay stock, so `--mhz` only raises the cap region. After
writing, `undervolt` reads the curve back to confirm.

### Persisting at startup

Tuning is live-only and reverts on reboot. Add `--persist` to a real `undervolt` run to make it stick:
it copies the app to `%LOCALAPPDATA%\Programs\nvidia-simple-undervolt` and registers a Task Scheduler
task that re-applies the same undervolt at logon, elevated. The task runs with `--msgbox`, so if it ever
fails (idle GPU, driver change) you get a message box instead of a silent no-op.

```powershell
# Re-apply this undervolt at every logon:
simple-nvidia-undervolt undervolt --mv 960 --mhz 2880 --persist
# Stop re-applying it:
simple-nvidia-undervolt unpersist
```

## clear

Resets to stock: the V/F curve offsets (`ClkVfPointsSetControl` → 0, including an Afterburner flatten),
the memory clock offset (`SetPstates20`), and the core voltage boost (`SetCoreVoltageBoostPercent`).
`status` shows the memory offset against the factory base clock (`GetAllClockFrequencies`, base type),
since the GET only reports the offset-applied absolute.

## Development

Building from source, diagnostic commands, the NVAPI buffer layout, and how to
verify/port the offsets to another GPU: [DEVELOPMENT.md](DEVELOPMENT.md).
