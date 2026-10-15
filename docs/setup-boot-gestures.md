# Setup boot gestures

Sources checked on 2026-10-11 for the device-bound Windows setup guide. These are physical entry instructions, not authorization to install firmware. The service must still verify the observed USB transition, bootloader identity, model, memory geometry and two complete matching backups.

| Current firmware | Device family | Gesture shown | Evidence |
| --- | --- | --- | --- |
| Glorious original | GMMK PRO | Space + B while connecting USB | [Manufacturer QMK installation guide, Flashing your Keymap, step 1](https://www.gloriousgaming.com/en-ca/blogs/guides-resources/step-by-step-guide-to-configuring-your-gmmk-pro-using-qmk) |
| Glorious original | GMMK2 | Space + B while connecting USB | [Manufacturer QMK installation guide, Putting the Keyboard into Bootloader Mode, step 17](https://www.gloriousgaming.com/blogs/guides-resources/gmmk-2-qmk-installation-guide) |
| Glorious original | GMMK3 65 / 75 / 100 | Esc while connecting USB | Reviewed manufacturer branch readmes in `reference/glorious-gmmk3/p{65,75,100}/{ansi,iso}/readme.md`, bootloader section. This is the branch's documented physical entry; the UI does not substitute Fn+Esc or imply a tested original-device transition. |
| Glorious original | GMMK 3 PRO 100% ISO, wired mechanical variant | Space + B while connecting USB | Observed on 2026-10-15: normal `342D:E3CE` changed to WB32 ROM `342D:DFA0` at the same physical USB port. Esc left this unit in normal mode. The app supports setup for this exact wired, non-HE 100% ISO variant. This observation does not cover other PRO sizes, layouts, HE variants or revisions; see the [PRO port notes](gmmk3-pro-port.md). |
| Glorious original | GMMK Numpad | Plus + Enter while connecting USB | Pinned QMK `firmware/qmk_firmware/keyboards/gmmk/numpad/readme.md`, explicit “From Glorious Core Firmware” entry. |
| INGLORIOUS | All supported keyboards | Space + B while connecting USB | `firmware/inglorious/inglorious.c` fixed `recovery_startup` / `recovery_chord_held` path and board `IG_RECOVERY_KEY*` definitions. |
| INGLORIOUS | GMMK Numpad | Plus + Enter while connecting USB | Same fixed recovery path with the Numpad matrix positions. |

The guide asks users to keep the combination held for approximately three seconds. INGLORIOUS checks physical matrix positions independent of profile bindings, requires only the two expected keys, and enters the bootloader without resetting EEPROM. The documented Escape bootmagic in upstream PRO/GMMK2 QMK can erase persistent settings; it is deliberately not used as the stock-firmware backup instruction.

The GMMK 3 PRO observation reported WB32 bootloader version 0.4, 256 KiB internal flash and 36 KiB SRAM. Two complete internal-flash reads matched and normal operation resumed afterward. This initial read-only check did not write firmware. Subsequent installation, original-firmware restoration and the update to firmware 0.11.4 were tested on the same variant; see the [hardware acceptance record](hardware-acceptance.md). Its geometry must not be accepted by an existing 128 KiB model, and the proprietary updater identity `2442:B059` is not a ROM transport. Personal captures and USB paths are kept outside public source exports.
