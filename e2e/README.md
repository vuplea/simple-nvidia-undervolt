# End-to-end tests (real GPU writes)

xUnit tests that exercise the **real** built executable (`src/bin/simple-nvidia-undervolt.exe`) against
the actual driver and Task Scheduler: the action always goes through the exe, and the result is verified
with direct library calls (reading the GPU via NVAPI, inspecting files, or querying Task Scheduler). They
are a separate project from `tests/` (pure logic, no GPU) so the unit tests stay fast and hardware-free.

> ⚠️ These change live GPU tuning and touch Task Scheduler / `%LOCALAPPDATA%`. A collection fixture
> snapshots the current core V/F curve deltas, memory offset and voltage boost and restores them when the
> run finishes; the persistence test backs up and restores any existing logon-task registration. Run them
> deliberately.

## Running

From an **Administrator** shell:

```powershell
dotnet test e2e
```

Each test is skipped (not failed) unless the host is elevated and an NVIDIA GPU is present, so running
without admin — or as part of a wider `dotnet test` — does nothing.

The persistence test mutates the real `nvidia-simple-undervolt` logon task and `%LOCALAPPDATA%` install,
but backs both up and restores them when it finishes.

## What runs

All actions are driven through the exe; assertions read back directly.

- `Status`, `DryRunUndervolt` — read-only / dry-run; the dry run also confirms (via NVAPI) nothing changed.
- `Clear` — `clear`, then assert the curve deltas and voltage boost are zero.
- `Undervolt_WithMemoryOffset` — `undervolt --mem-offset`, then assert the memory clock moved (idle-safe).
- `Undervolt_UnderLoad_WritesCurveDeltas` — `undervolt`, then assert curve deltas were written. Needs the
  GPU under a 3D load (the curve flatten is skipped at idle), so it is skipped at idle.
- `SaveShortcut` (default + custom name) — the exe writes a `.lnk`; assert it targets the exe with the
  expected baked arguments.
- `Undervolt_MarksTheMatchingLinkActiveOnDisk` — a real apply renames the matching `.lnk` to `[ACTIVE] …`
  and clears other markers.
- `Persist_RegistersATaskThatTaskSchedulerRunsSuccessfully` — the exe registers the logon task; the test
  then triggers it through Task Scheduler and checks it ran with result 0, and that `unpersist` removes it.
  It backs up both the existing `nvidia-simple-undervolt` registration (byte-for-byte) and the
  `%LOCALAPPDATA%` install folder, and restores both afterwards (the task restore is verified; a backup
  that can't be restored is kept and the test fails with its path).
