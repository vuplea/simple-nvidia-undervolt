# End-to-end tests (real GPU writes)

xUnit tests that exercise the **real** built executable (`src/bin/simple-nvidia-undervolt.exe`) against
the actual driver and Task Scheduler: the action always goes through the exe, and the result is verified
with direct library calls (reading the GPU via NVAPI, inspecting files, or querying Task Scheduler). They
are a separate project from `tests/` (pure logic, no GPU) so the unit tests stay fast and hardware-free.

> ⚠️ These change live GPU tuning and touch Task Scheduler / Program Files. A collection fixture
> snapshots the current tuning (core V/F curve deltas, the P0 graphics/memory clock and core-voltage
> offsets, voltage boost) and restores it when the run finishes; every test that runs `clear` or a
> persisting undervolt backs up and restores the existing logon-task registration and Program Files
> install. Run them deliberately: if the host crashes (or the run is killed) before the restore, the
> test tuning and the replaced task/install remain — the backups are kept in `%TEMP%` as
> `nvundervolt-task-backup-*.xml` / `nvundervolt-install-backup-*`, and `clear` resets the GPU.

## Running

From an **Administrator** shell:

```powershell
dotnet test e2e
```

Each test is skipped (not failed) unless the host is elevated and an NVIDIA GPU is present, so running
without admin — or as part of a wider `dotnet test` — does nothing.

The under-load tests open a visible browser window (Playwright headless renders WebGL in software)
running a heavy WebGL shader for the duration of the test. Edge and Chrome are used if installed;
otherwise fetch the Chromium matching this project's Playwright with the script generated next to
the test binaries — a globally installed `playwright` CLI is often a different version and fetches
a browser it can't launch:

```powershell
pwsh e2e/bin/Debug/net10.0-windows/playwright.ps1 install chromium
```

These tests skip when no browser can be launched, the load doesn't land on the NVIDIA GPU (hybrid
graphics), or the card is power-limited — where TGP, not the voltage cap, picks the operating point.

The tests that run `clear` or a persisting undervolt mutate the real `simple-nvidia-undervolt` logon
task and Program Files install, but back both up (the task XML byte-for-byte) and restore them when they
finish. A restore that fails keeps the backup and fails the test with its path.

## What runs

All actions are driven through the exe; assertions read back directly. Coverage, by area:

- **Read-only / dry-run** — `status` reports the tuning; a dry-run undervolt prints the plan and
  (confirmed via NVAPI) writes nothing.
- **GPU writes** — `clear` back to stock (curve, voltage boost and memory offset); undervolts with a
  memory offset and with deep or driver-smoothed caps, asserting the deltas landed, the write
  verified, and the curve measurably lowered. Write tests skip if the curve doesn't read back
  cleanly (a brief transitional state).
- **Tuning shape** — the per-anchor deltas an apply writes: a plain cap leaves everything through
  the flat start at stock and only flattens above it; a cap with a reduced clock carries one shared
  offset from the band through the flat start; a memory-only tune leaves the curve alone; `status`
  reports the cap point while capped.
- **Under load** — a Playwright-driven WebGL shader holds a sustained load, and the tests assert
  the tool's actual contract: the boost settles on the requested cap point (plain and
  reduced-clock caps — the flatten starts one anchor above the cap because the boost pins one 5 mV
  step below the flat), and the `voltage` telemetry reports a real operating point.
- **Reference curve** — `set-reference-curve` captures the stock curve (resetting an applied tuning
  for the capture and restoring it exactly), exports a curve file that imports back, and tuning
  plans from the saved reference — or warns and falls back to the live curve when the reference
  doesn't match the card.
- **Tuning replay** — a tune's `--out-tuning-file` export and `status`'s applied-tuning export
  carry the same tuned anchors, and `--in-tuning-file` re-applies the exported deltas and memory
  offset exactly onto a cleared card.
- **Shortcuts** — saving a `.lnk` (default and custom name) that targets the installed Program Files
  copy with the expected baked arguments; a failed dry-run save leaves no link behind; a
  link-launched apply (or one that just saved its link) badges that link's icon on disk and clears
  the badge from the previously active one, while a plain terminal run touches no links.
- **Persistence** — a persisting undervolt registers the logon task, Task Scheduler runs it with
  result 0, and `clear` removes it.
