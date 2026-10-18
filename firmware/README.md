# INGLORIOUS onboard firmware

All sixteen targets use firmware **0.11.8**, build `0x000B0801`, including the separate GMMK 3 PRO 100% ISO wired, non-HE target. Build success does not establish full physical acceptance. Model support and physical acceptance are listed in [the model overview](../docs/model-support.md).

## Behavior

The QMK-based firmware runs key assignments, four layers, macros, lighting and standby on the keyboard. The app may be closed after a successful configuration transfer. The Numpad is USB-only. Its slider supports disabled, lighting brightness, relative volume and MIDI control; linking brightness across devices requires the desktop app.

There are 21 lighting modes with per-LED overrides, custom color sequences and global settings. The optional lighting test lasts 21 seconds and the screensaver repeats the sequence after inactivity. Settings and the test sequence run locally, without a background service.

The knob can cycle through selected functions in a saved order. A brief color pulse confirms a function change, and the app can read the function actually active on the keyboard. Short presses can use one shared action or separate actions for each knob function. Cycling, press actions and the pulse work with the app closed; reading the live state does not change the saved profile.

Keys and rotary-knob directions can raise or lower keyboard brightness. The dimmer applies to all keys and side lighting, including individual effects, without the app running. Its current level is temporary; reconnecting power or transferring a profile restores the normal level. The assigned key or knob functions remain saved. On the Numpad, moving a slider assigned to brightness takes over the dimmer again.

Rotary-knob presets cover volume, brightness, tracks, vertical/horizontal scrolling, zoom, tabs and undo/redo. A per-layer option cycles selected presets in your chosen order after holding the knob for 700 ms; a short press retains its assigned action. Reconnecting or transferring a profile restores the saved preset. Volume, brightness, scrolling and zoom have an optional step multiplier from 1 to 5. Zoom and undo offer Ctrl or Command shortcuts; undo also supports QWERTY, QWERTZ and AZERTY host layouts. Application support for these shortcuts varies.

Each device has two configuration slots inside a 32-KiB internal flash reserve. Board IDs 1–9 use 7,168-byte slots; IDs 10–16 use 7,680-byte slots. Four layers, up to 32 macro slots and shared event/color pools fit within that fixed space. Oversized configurations are rejected rather than silently truncated. An upload stages data in the inactive part of the EEPROM RAM cache and commits only after full validation.

This storage implementation does not promise atomic recovery from power loss during QMK flash consolidation. External stock SPI memory is disabled. The firmware does not store profiles in the wireless controller or preserve all original external settings.

## Protocol and identities

[`protocol.md`](protocol.md) defines the 32-byte RAW-HID configuration protocol, feature flags, explicit version response and persistent configuration formats. Development USB PIDs `FEED:1C01` through `FEED:1C10` map to the 16 board IDs. These are development identities, not a commercial USB allocation. App handshake checks must agree with the expected model; a PID alone does not authorize writing.

The runtime protocol offers no arbitrary memory access, firmware write, protection change or remote bootloader command. A physical startup gesture enters the bootloader. INGLORIOUS keyboard firmware uses Space+B; the Numpad uses Plus+Enter. Original-firmware gestures may differ: see [`setup-boot-gestures.md`](../docs/setup-boot-gestures.md).

The generic installer composes an installation image using the reviewed firmware binary, a per-installation recovery identifier in the unused code gap and the configuration tail. The identifier is not part of the frozen raw firmware binary and is not an immutable hardware UID. Details are in [`device-setup.md`](../docs/device-setup.md).

## Build

The public build prepares pinned dependencies and builds all targets without accessing hardware:

```powershell
pwsh -NoProfile -File scripts/Initialize-BuildDependencies.ps1 -Scope Firmware
pwsh -NoProfile -File firmware/Build-Firmware.ps1 -Board all -Clean -OutputDirectory firmware/build
python firmware/Verify-CandidateBuild.py firmware/build
```

QMK base: `7a1bbf37c5c07da4ea0a162bb139083a46ef40a1`. ChibiOS: `6170ddf92d55be54c89b708d1882eea229edeb2c`. ChibiOS-Contrib: `5a9ad82b6ba4f649cdb800e8c2bbc9be2d3ce767`. The remaining submodule and compiler pins are in `scripts/build-dependencies.json`. Firmware uses QMK-MSYS 1.12.0 and ARM GCC 15.2.0; LTO is disabled. `Prepare-Overlay.ps1` generates board keymaps and physical mappings. The timing and storage patch scripts only accept their expected upstream input hashes.

The GMMK 3 wired non-HE ports are normalized from pinned manufacturer sources. Their original sources, provenance and documented corrections are retained under `reference/glorious-gmmk3` and `ports/gmmk3`. The physical OS switch remains disabled pending hardware validation.

The build records binary sizes, source hashes, compiler version, linker maps and stack reports. Firmware catalog hashes must match before those files can be packaged. A successful build proves neither hardware compatibility nor maximum stack usage under every input sequence.

Board 16 produces an application-only image at `0x08007000`. Its first 28 KiB must be preserved from that device's independently verified original backup; vendor bytes are never distributed with the build. The linker excludes that prefix, the identity page at `0x08037F00`, and settings at `0x08038000`. The retained loader's early Space+B route is part of the installation safety policy. See [the layout and engineering notes](engineering/README.md). Build manifests record version and layout per target, so each artifact has an explicit version and application offset.

Board 16 also provides opt-in physical key/encoder diagnostics and temporary all-LED or individual-LED colors. The session times out after ten seconds without a refresh, ends on USB suspend, and does not save settings or suppress normal key input. Other targets keep their existing manual lighting test. Exact command layouts are in [the protocol](protocol.md).

## Offline checks

```powershell
pwsh -NoProfile -File firmware/tests/Run-RuntimeChecks.ps1
pwsh -NoProfile -File firmware/tests/Run-PortChecks.ps1 -FixtureDirectory artifacts/gmmk3-pro-port-fixtures
pwsh -NoProfile -File firmware/tests/Run-StorageChecks.ps1
pwsh -NoProfile -File firmware/tests/Run-BulkChecks.ps1
```

Tests exercise actual C modules with simulated storage, time and USB traffic. The storage suite compiles the patched QMK driver and covers interrupted writes and readback failures. Port tests compare physical layout references, configuration round trips, recovery key positions and lighting sequences. None of these tests changes a real keyboard.

## License and source

Original common firmware is GPL-2.0-or-later. QMK, ChibiOS, ChibiOS-Contrib and manufacturer files retain their per-file licenses and exceptions. A binary release must include the matching source and build/patch scripts, as described in [`public-release.md`](../docs/public-release.md). Original manufacturer firmware and personal recovery archives are not distributed.
