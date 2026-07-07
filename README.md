# <picture><source media="(prefers-color-scheme: dark)" srcset="assets/logo-dark.png"><img src="assets/logo-light.png" alt="simple-nvidia-undervolt logo: an NVIDIA GPU voltage/frequency curve flattening at the voltage cap" height="50" align="middle"></picture>&nbsp; simple-nvidia-undervolt

A small Windows command-line alternative to MSI Afterburner for overclocking or undervolting an NVIDIA GPU.
It talks to the driver directly — no background process — and caps voltage by flattening the V/F curve.

Overclocking/undervolting shouldn't mean dragging points around a curve editor. **Only the top
segment usually matters** — the max voltage you allow and the clock you run there — because that's
where the card sits under load, and it sets your peak framerate. To simplify, you can just set your
desired voltage and frequency pair with this tool. To skip manual investigation of tuning options,
the tool also offers ready-made profiles.

The tool is expected to work on GTX 10 (Pascal), RTX 20 (Turing), RTX 30 (Ampere), RTX 40 (Ada),
RTX 50 (Blackwell) generations, but validated only on Blackwell — if it works for you, confirm
it [here](https://github.com/vuplea/simple-nvidia-undervolt/issues/1). Also read the [disclaimer](#disclaimer) on using the tool.

Download `simple-nvidia-undervolt.exe` from [releases](https://github.com/vuplea/simple-nvidia-undervolt/releases),
or `simple-nvidia-undervolt-profiles.zip` which contains the tool + ready-made profiles.

### Examples

```powershell
# Cap 925 mV, keep the stock clock for that voltage:
simple-nvidia-undervolt --mv 925
# Cap 960 mV, hold 2880 MHz there:
simple-nvidia-undervolt --mv 960 --mhz 2880
# Memory overclock only - +5% of the factory base clock:
simple-nvidia-undervolt --mem-pct 5
# Percentage adjusting - 5% reduction of peak voltage, 2% increase of the peak clock and of memory clock:
simple-nvidia-undervolt --mv-pct -5 --mhz-pct 2 --mem-pct 2 --peak-mv 1060
# Revert to stock:
simple-nvidia-undervolt clear
```

### Ready-made profiles

`simple-nvidia-undervolt-profiles.zip` contains folders of profile shortcuts per card range —
three families (perf boost / power cut at the same performance / deep power cut) in four risk
tiers each, built from community-converged values per generation, memory type and power-limit
class. Extract the zip, double-click `~install-simple-nvidia-undervolt.exe` once (it copies the
app to Program Files, which the shortcuts target), then double-click a profile from the folder
that lists your card. [PROFILES.md](PROFILES.md) explains the matrix and the values.

## Usage

```
[options]             By not specifying a verb, it is implicitly a 'tune' command: you can cap voltage
                      (flatten the curve), set the clock at the cap, and offset the memory clock.
install               Copy the app to Program Files, so saved or profile shortcuts work.
status                Show curve offset, memory clock, voltage boost, and logon re-apply.
watch                 Poll live core voltage/clock/temp/power, tracking the max.
clear                 Reset all tuning to stock and remove logon re-apply.
```

`status` and `watch` are read-only and need no elevation. **Tuning, `clear` and `install` need
administrator rights; if run from a normal terminal they prompt for elevation.**
A tuning run re-applies itself at logon by default, so it survives a reboot
unless you pass `--no-persist`. Low-level NVAPI inspection commands are listed under
`--help-diagnostics` and in [DEVELOPMENT.md](DEVELOPMENT.md).

### Tuning options

```
Voltage cap, pick preferred syntax (required to provide, unless tuning just the memory clock):
  --mv <n>          n mV.
  --mv-offset <n>   peak_mV + n           (n < 0).
  --mv-pct <n>      peak_mV * (1 + n/100) (n < 0).
Clock at the cap, pick preferred syntax (omit = stock clock there):
  --mhz <n>         n MHz.
  --mhz-offset <n>  peak_MHz + n.
  --mhz-pct <n>     peak_MHz * (1 + n/100).
Peak voltage reference, required for --mv-offset/pct and --mhz-offset/pct:
  --peak-mv <n>     Peak voltage under load (mV).
Memory clock (optional):
  --mem <n>         n MHz.
  --mem-offset <n>  base_MHz + n.
  --mem-pct <n>     base_MHz * (1 + n/100).
Other:
  --cap-points <n>  Curve anchors holding the cap's offset, counting down from the cap.
                    1 = only the cap point.
  --no-persist      Don't persist; by default a real run re-applies at logon.
  --save-shortcut [name]
                    Drop a .lnk (specify name/path, otherwise auto-generated).
```

The offset/pct forms are relative to the real under-load operating voltage — read it from `watch` under a
sustained load.

Apply your settings at a consistent GPU temperature — simplest with a cooled-down card. Clock targets
resolve against the live curve, which shifts slightly with temperature, so applying hot vs. cold bakes
in a slightly different tuning.

### Persisting at startup

A tuning run **persists by default**: it copies the app to Program Files, and registers a Task Scheduler
task that re-applies the same undervolt at logon, elevated. If apply ever fails you get a message box
so you are aware. Pass `--no-persist` to skip persistence.

### Saving a shortcut

`--save-shortcut` writes a `.lnk` in the current directory named for the settings (e.g.
`Tune 960mV 2880MHz.lnk`). Double-clicking it applies that tuning and shows the
result in a message box. Use `--dry-run` to save a shortcut without applying.
Shortcuts target the `%ProgramFiles%` copy.

After applying from a link, that link's icon gains a green checkmark and the previously
active link in the same directory loses it, to see at a glance which profile is live.

```powershell
# Drop a reusable shortcut without applying:
simple-nvidia-undervolt --mv 960 --mhz 2880 --dry-run --save-shortcut
# Custom name (creates "Quiet.lnk"):
simple-nvidia-undervolt --mv 925 --dry-run --save-shortcut Quiet
```

### clear

The `clear` command resets all tuning to stock: the V/F curve offsets, memory clock and core voltage boost.
The command also stops the re-apply at logon by removing the registered task. The Program Files copy stays, so
saved shortcuts keep working.

## Disclaimer

The app writes GPU tuning via `ClkVfPointsSetControl`, and the memory clock offset via
`SetPstates20`. The worst case is believed to be an unstable configuration causing application,
driver, or system crashes. It never raises voltage or power limits: the voltage-boost and
voltage-offset fields are only written as zero, and power-limit APIs are not used. The tuning
itself does not survive a reboot, but by default it is re-applied at logon — run `clear` to revert
to stock.

Importantly, this is still an experimental tool and you use it entirely at your own risk — we are not
responsible for any consequences of using it, whether to hardware or software.

This project is not affiliated with, sponsored by, or endorsed by NVIDIA Corporation. NVIDIA, GeForce,
GTX and RTX are trademarks of NVIDIA Corporation, used here only to identify the hardware the tool
operates on.

## Development

The cap anchor and every point above it are flattened to one frequency through `ClkVfPointsSetControl`;
the boost algorithm then pins voltage at the cap. A band of `--cap-points` anchors ending at the cap
shares the cap's offset, so if the realized voltage settles a bin or two below the cap under load,
an overclocked cap doesn't fall back down the steep stock curve.
Points below the band stay stock; `--mhz` only raises the cap region. After writing, the tool reads the
curve back to confirm.

Building from source, diagnostic commands, the NVAPI buffer layout, and how to
verify/port the offsets to another GPU: [DEVELOPMENT.md](DEVELOPMENT.md).
