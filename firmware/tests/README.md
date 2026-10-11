# Offline firmware checks

These tests compile the actual INGLORIOUS C runtime and its helpers with mocked hardware interfaces. They do not discover, open, update or flash a physical device. The native compiler is the existing QMK-MSYS GCC; the runners do not install tools or modify the system PATH.

## Prepare fixtures

The runtime checks consume synthetic configurations emitted by the desktop serializer. From the repository root:

```powershell
dotnet run --project tests/InGlorious.OnboardChecks -c Release
dotnet run --project tests/InGlorious.OnboardChecks -c Release -- --port-fixtures artifacts/port-fixtures
dotnet run --project tests/InGlorious.OnboardChecks -c Release -- --gmmk3-fixtures artifacts/gmmk3-port-fixtures
pwsh -NoProfile -File tests/InGlorious.ColorSequenceChecks/Run-ColorSequenceChecks.ps1
```

For the storage-driver checks, prepare the pinned QMK checkout and apply the overlay first, using the build instructions in [the firmware README](../README.md). `Run-StorageChecks.ps1` verifies the expected patched source before compiling it.

## Run checks

```powershell
pwsh -NoProfile -File firmware/tests/Run-RuntimeChecks.ps1
pwsh -NoProfile -File firmware/tests/Run-PortChecks.ps1
pwsh -NoProfile -File firmware/tests/Run-StorageChecks.ps1
pwsh -NoProfile -File firmware/tests/Run-BulkChecks.ps1
pwsh -NoProfile -File firmware/tests/Run-EffectsChecks.ps1
pwsh -NoProfile -File tests/TimingChecks/Run-TimingChecks.ps1
```

- **Runtime:** PRO ISO and Numpad RAW-HID framing, lengths, padding, upload ordering, duplicate chunks, generation/CRC/semantic validation, malformed macros, busy guards, independent cache/backing failures, configuration reload, release reporting, recovery chord, slider filtering, brightness, standby and the complete 21-second lighting test.
- **Model ports:** the other thirteen layouts use the same production runtime with independently derived matrix, LED, recovery-key and lighting-test coordinates. Real serializer payloads pass upload, commit and full readback checks. All fifteen models check permanent reactive-history expiry across timer wrap.
- **Storage:** the actual hash-guarded QMK wear-leveling implementation, with both 2-byte and 8-byte backing writes and both slot geometries. Cases cover cache borrowing, abort without writes, phase failures, full logs, consolidation, dirty-cache recovery, backing read failures and invalid geometry. The physical flash interface is simulated.
- **WB32 bulk writer:** every supported WB32 board ID, scalar and full-page writes, erase polarity, alignment, bounds, first/middle/final failures, unchanged surrounding bytes and rejected unsupported compile configurations.
- **Effects and color sequences:** all 21 modes, brightness, reactive lifetimes, timing wrap and independent C#/C sequence-pixel comparison. The output-transfer tests compare the integer sRGB table with a separate floating-point reference.
- **Timing:** bounded polling intervals, deferred debounce, safe delayed USB reconnection, timer wrap and descriptor-patch guards.

Runtime, port, storage and bulk runners accept `-ReportPath` and create a new JSON report with source hashes and assertion counts. Counts include repeated per-byte, per-pixel and packet assertions; they are not counts of independent scenarios.

## Verify compiled ARM artifacts

```powershell
python firmware/Verify-CandidateBuild.py firmware/build
```

The read-only audit checks all fifteen build outputs against their source manifest, reserved flash region, actual USB descriptors, base keymaps, physical-key masks, LED coordinates/channels, linker memory reservations and compiler stack frames.

These results do not establish physical flash reliability, atomic power-loss recovery, USB behavior on every host, LED color calibration or maximum runtime stack use. Hardware acceptance is recorded separately in [the model overview](../../docs/model-support.md).
