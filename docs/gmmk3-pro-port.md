# GMMK 3 PRO 100% ISO support

Reviewed: 2026-10-19. This exact wired, non-HE variant is supported without an experimental label after testing on one device under Windows. Installation, original-firmware restoration, normal typing and lighting, the complete lighting sequence, Windows/macOS modifier switching, and three independent saved profiles with firmware 0.11.11 have been confirmed within the scope below. App 0.7.0 automatic loading and one configuration transfer were also confirmed. The remaining checks are listed below.

Other sizes, ANSI layouts, HE variants and wireless operation are not covered. The model is identified separately from the generic GMMK 3 100% ISO; it is never selected by a similar retail name alone.

## Identity and layout

| Property | Value |
| --- | --- |
| Device / board ID | `gmmk3-pro-100-iso` / `16` |
| Stock USB identity | `342D:E3CE` |
| INGLORIOUS USB identity | `FEED:1C10` |
| Westberry update identity / protocol | `342D:DFA0` / `0.4` |
| Flash / SRAM | 262,144 / 36,864 bytes |
| Retained resident loader | `0x08000000`–`0x08006FFF` |
| Application start | `0x08007000` |
| Configuration matrix / LEDs | 14 × 8 / 126 |
| Configuration size, schema 1 / schemas 2–4 | 5,656 / 7,200 bytes |
| Configuration payload capacity | 7,648 bytes |

The memory geometry was observed on the tested device. It does not identify every PCB or prove a specific chip package. The normalized electrical source derives from the manufacturer's generic 100% ISO definition. Static and synthetic checks cover the mapping; the complete physical matrix, encoder and LED map still require acceptance.

## Installation and recovery

The normal app supports first installation, update and original-firmware recovery for this exact model. Enter update mode with **Space + B** while connecting USB. The original resident loader handles this before starting the application.

First installation requires two matching full reads of that device's original firmware, exact model/geometry checks and the reviewed resident loader. A recovery record binds the original backup to the installation. The original prefix is copied into the prepared image but is excluded from every erase/program command at the adapter and packet boundaries. The live prefix is read and checked again immediately before a one-use write grant is consumed.

Updates accept only cataloged application images and preserve the installation marker, loader and complete settings area. Restore uses the matching original backup. Both paths require verified full readbacks; a version string or USB product name alone is not firmware provenance. Changed or unknown loaders, ambiguous identities and missing recovery records stop the operation.

The first separately authorized engineering installation was followed by normal startup, basic typing and lighting. Space+B then returned the installed device to update mode. Original-firmware restoration produced two full readbacks matching the original backup; stock typing and lighting worked again. This verifies the normal round trip on that device, not recovery after interrupted writes or a lost installation marker. External storage is outside the internal-flash backup.

Subsequent installation through the app and an update to firmware 0.11.4 with app 0.6.54 completed. The tester confirmed the full lighting sequence and return to normal lighting. The experimental label is removed only for this exact variant; identity, backup and recovery checks are unchanged.

## Device test and settings

Firmware 0.11.3 adds a volatile device-test session for this variant. It records physical key presses and both encoder directions, and supports all-LED and individual-LED overlays. It does not change stored profiles. Closing the test restores normal lighting; an inactivity watchdog and USB suspension also end the session. Keys retain their normal operating-system functions during testing.

New configurations default to red. Existing saved configuration retains its colors. The app can import matching saved CORE per-key colors and simple bindings into an unsaved profile copy; it shows omitted settings and requires an explicit transfer to the keyboard.

Firmware 0.11.11 supports three independent hardware profiles selected by the physical switch, each with its own four layers. A separate bank occupies `0x0802A000`–`0x08035FFF`; the legacy configuration remains a read-only fallback for positions without saved data. The update image layout preserves both areas. The exact protocol and storage guarantees are described in the [firmware protocol](../firmware/protocol.md#private-board-16-hardware-profiles-profile-pairs-v1).

Windows-side tests confirmed independent saved colors, a key-binding change isolated to one profile, switching with the app closed, reconnecting in profile 2, and loading the active profile in the app. The OS switch was checked in both directions, including with the app closed. See [hardware controls](hardware-controls.md) for the observations and limits.

## Remaining acceptance

- Exercise original-firmware restoration through the integrated app guide on this variant; the earlier verified restoration used the separate engineering flow.
- Test every physical key, Fn action, knob direction and knob press.
- Compare every key, side and logo LED with the preview.
- Transfer a harmless macro, layer and lighting profile; verify it after reconnecting with the app closed.
- Verify three distinct hardware profiles survive a later firmware update, and test all layers, macros, knob settings and differing polling rates in each profile.
- Test native macOS/Linux packages and permissions separately.

Offline adapter, protocol, native-runtime and linked-firmware checks do not substitute for these physical results. See [hardware acceptance](hardware-acceptance.md) for the complete checklist. Original device images, personal configurations and local reports are excluded from the public source export.
