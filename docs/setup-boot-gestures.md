# Setup boot gestures

Sources checked on 2026-10-11 for the device-bound Windows setup guide. These are physical entry instructions, not authorization to install firmware. The service must still verify the observed USB transition, bootloader identity, model, memory geometry and two complete matching backups.

| Current firmware | Device family | Gesture shown | Evidence |
| --- | --- | --- | --- |
| Glorious original | GMMK PRO | Space + B while connecting USB | [Manufacturer QMK installation guide, Flashing your Keymap, step 1](https://www.gloriousgaming.com/en-ca/blogs/guides-resources/step-by-step-guide-to-configuring-your-gmmk-pro-using-qmk) |
| Glorious original | GMMK2 | Space + B while connecting USB | [Manufacturer QMK installation guide, Putting the Keyboard into Bootloader Mode, step 17](https://www.gloriousgaming.com/blogs/guides-resources/gmmk-2-qmk-installation-guide) |
| Glorious original | GMMK3 65 / 75 / 100 | Esc while connecting USB | Reviewed manufacturer branch readmes in `reference/glorious-gmmk3/p{65,75,100}/{ansi,iso}/readme.md`, bootloader section. This is the branch's documented physical entry; the UI does not substitute Fn+Esc or imply a tested original-device transition. |
| Glorious original | GMMK Numpad | Plus + Enter while connecting USB | Pinned QMK `firmware/qmk_firmware/keyboards/gmmk/numpad/readme.md`, explicit “From Glorious Core Firmware” entry. |
| INGLORIOUS | All supported keyboards | Space + B while connecting USB | `firmware/inglorious/inglorious.c` fixed `recovery_startup` / `recovery_chord_held` path and board `IG_RECOVERY_KEY*` definitions. |
| INGLORIOUS | GMMK Numpad | Plus + Enter while connecting USB | Same fixed recovery path with the Numpad matrix positions. |

The guide asks users to keep the combination held for approximately three seconds. INGLORIOUS checks physical matrix positions independent of profile bindings, requires only the two expected keys, and enters the bootloader without resetting EEPROM. The documented Escape bootmagic in upstream PRO/GMMK2 QMK can erase persistent settings; it is deliberately not used as the stock-firmware backup instruction.
