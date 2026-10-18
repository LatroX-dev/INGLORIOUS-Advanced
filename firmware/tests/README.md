# Offline firmware checks

These tests compile the actual INGLORIOUS C runtime and its helpers with mocked hardware interfaces. They do not discover, open, update or flash a physical device. The native compiler is the existing QMK-MSYS GCC; the runners do not install tools or modify the system PATH.

## Prepare fixtures

The runtime checks consume synthetic configurations emitted by the desktop serializer. From the repository root:

```powershell
dotnet run --project tests/InGlorious.OnboardChecks -c Release
dotnet run --project tests/InGlorious.OnboardChecks -c Release -- --port-fixtures artifacts/port-fixtures
dotnet run --project tests/InGlorious.OnboardChecks -c Release -- --gmmk3-fixtures artifacts/gmmk3-port-fixtures
dotnet run --project tests/InGlorious.OnboardChecks -c Release -- --retained-loader-fixtures artifacts/retained-loader-fixtures
dotnet run --project tests/InGlorious.OnboardChecks -c Release -- --knob-fixtures artifacts/knob-fixtures
dotnet run --project tests/InGlorious.OnboardChecks -c Release -- --knob-order-fixtures artifacts/knob-order-fixtures
dotnet run --project tests/InGlorious.OnboardChecks -c Release -- --knob-press-fixtures artifacts/knob-press-fixtures
pwsh -NoProfile -File tests/InGlorious.ColorSequenceChecks/Run-ColorSequenceChecks.ps1
```

The knob exporter requires a new output directory; use a fresh project-local path for a later export and pass that path to both runners.

For the storage-driver checks, prepare the pinned QMK checkout and apply the overlay first, using the build instructions in [the firmware README](../README.md). `Run-StorageChecks.ps1` verifies the expected patched source before compiling it.

## Run checks

```powershell
pwsh -NoProfile -File firmware/tests/Run-RuntimeChecks.ps1 -KnobFixtureDirectory artifacts/knob-fixtures -KnobOrderFixtureDirectory artifacts/knob-order-fixtures
pwsh -NoProfile -File firmware/tests/Run-PortChecks.ps1 -FixtureDirectory artifacts/retained-loader-fixtures -KnobFixtureDirectory artifacts/knob-fixtures -KnobOrderFixtureDirectory artifacts/knob-order-fixtures
pwsh -NoProfile -File firmware/tests/Run-StorageChecks.ps1
pwsh -NoProfile -File firmware/tests/Run-BulkChecks.ps1
pwsh -NoProfile -File firmware/tests/Run-EffectsChecks.ps1
pwsh -NoProfile -File tests/TimingChecks/Run-TimingChecks.ps1
```

- **Runtime:** PRO ISO and Numpad RAW-HID framing, lengths, padding, upload ordering, duplicate chunks, generation/CRC/semantic validation, malformed macros, busy guards, independent cache/backing failures, configuration reload, release reporting, recovery chord, slider filtering, brightness, standby and the complete 21-second lighting test.
- **Model ports:** the other fourteen layouts use the same production runtime with independently derived matrix, LED, recovery-key and lighting-test coordinates. Real serializer payloads pass upload, commit and full readback checks. All sixteen models check permanent reactive-history expiry across timer wrap.
- **Storage:** the actual hash-guarded QMK wear-leveling implementation, with both 2-byte and 8-byte backing writes and both slot geometries. Cases cover cache borrowing, abort without writes, phase failures, full logs, consolidation, dirty-cache recovery, backing read failures and invalid geometry. The physical flash interface is simulated.
- **WB32 bulk writer:** every supported WB32 board ID, scalar and full-page writes, erase polarity, alignment, bounds, first/middle/final failures, unchanged surrounding bytes and rejected unsupported compile configurations.
- **Engineering board 16:** separate exact-target selection guards, 256 KiB flash with the final 32 KiB reserved, both directions of the old/new flash-size mismatch, and explicit engineering compile gates. Storage checks include its 7,680-byte slots; the opt-in port harness consumes separately generated offline fixtures.
- **Effects and color sequences:** all 21 modes, brightness, reactive lifetimes, timing wrap and independent C#/C sequence-pixel comparison. The output-transfer tests compare the integer sRGB table with a separate floating-point reference.
- **Brightness bindings:** key and encoder actions, saturated steps, release suppression, saved-binding readback, standby wake, macro cleanup, existing effect scaling and unchanged flash/configuration state. Board 16 also checks that local dimming leaves diagnostics active.
- **Knob presets:** schema5 validation and old-schema compatibility, all mask/step/layout byte values, physical short/long presses, selectable cycling, timer wrap, saved/custom mode restoration, layer/key/suspend/upload/diagnostic cancellation, bounded macro taps, native wheel direction codes, per-encoder multipliers and exactly restored modifier state for zoom/tab/undo shortcuts. Tests also cover transparent directions, binding-origin undo layouts, pre-held modifiers and all potential lower-layer press bindings. QWERTY, QWERTZ and AZERTY undo positions are checked separately. The optional `-KnobFixtureDirectory` consumes all ten presets exported by the current C# serializer for each encoder board and verifies upload, reboot and byte-exact readback. Reports record the fixture and test-source hashes. Tests use synthetic events; actual OS/application shortcuts remain hardware acceptance work.
- **Ordered presets:** all40,320 Lehmer permutations are checked against independent inversion ranking; all255 nonempty masks are exercised against representative orders. Actual long-press tests cover reversed/sparse orders, the custom slot, independent layers, reboot and downgrade. Schema6 requires exact length and canonical ranks; staging, aborts and invalid order commits preserve the active configuration. `-KnobOrderFixtureDirectory` checks the current C# schema6 fixtures alongside the unchanged schema5 set, including all-byte readback after reboot.
- **Timing:** bounded polling intervals, deferred debounce, safe delayed USB reconnection, timer wrap and descriptor-patch guards.
- **Live knob feedback and press actions:** actual layer-stack resolution and read-only status, change counters, 600-ms pulse output and lighting priorities, schema7 canonical tables, inherited Fn/modifier holds, latched short presses and long-press suppression. The optional `-KnobPressFixtureDirectory` consumes real schema7 C# payloads on all twelve encoder boards, alongside the schema5/6 fixtures, and checks commit, reboot and exact readback. These simulations do not establish physical LED appearance or host application behavior.

Runtime, port, storage and bulk runners accept `-ReportPath` and create a new JSON report with source hashes and assertion counts. Counts include repeated per-byte, per-pixel and packet assertions; they are not counts of independent scenarios.

## Verify compiled ARM artifacts

```powershell
python firmware/Verify-CandidateBuild.py firmware/build
```

The read-only audit checks all sixteen build outputs against their source manifest, reserved flash region, actual USB descriptors, base keymaps, physical-key masks, LED coordinates/channels, linker memory reservations and compiler stack frames.

The separate [engineering target](../engineering/README.md) requires an explicit
`--engineering --board gmmk3-pro-100-iso` verification request. It also checks the
actual linked WB32 EFL descriptor, the 36 KiB RAM extent, the engineering compile
gate, physical Space+B recovery positions, the relocated vector/startup VTOR,
and ELF/HEX exclusion of the retained loader and identity page. Default verification includes
all sixteen release targets, with the retained-loader protections also enforced for board16.

These results do not establish physical flash reliability, atomic power-loss recovery, USB behavior on every host, LED color calibration or maximum runtime stack use. Hardware acceptance is recorded separately in [the model overview](../../docs/model-support.md).
