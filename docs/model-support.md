# Supported keyboards

The app contains 15 model/layout variants on Windows, macOS and Linux. Two models have been used on physical hardware through the Windows app; the remaining 13 are marked **Experimental**. An experimental label does not bypass device, backup or firmware checks.

| Keyboard | Layout | Status | Device ID |
| --- | --- | --- | --- |
| GMMK PRO, revision 1 (STM32) | ISO | Hardware used | `pro-iso` |
| GMMK Numpad | USB only; no Bluetooth | Hardware used | `numpad` |
| GMMK PRO, revision 1 (STM32) | ANSI | Experimental | `pro-ansi` |
| GMMK PRO, revision 2 (WB32) | ANSI | Experimental | `pro-rev2-ansi` |
| GMMK PRO, revision 2 (WB32) | ISO | Experimental | `pro-rev2-iso` |
| GMMK 2, 65% | ANSI | Experimental | `gmmk2-65-ansi` |
| GMMK 2, 65% | ISO | Experimental | `gmmk2-65-iso` |
| GMMK 2, 96% | ANSI | Experimental | `gmmk2-96-ansi` |
| GMMK 2, 96% | ISO | Experimental | `gmmk2-96-iso` |
| GMMK 3, wired non-HE, 65% | ANSI | Experimental | `gmmk3-65-ansi` |
| GMMK 3, wired non-HE, 65% | ISO | Experimental | `gmmk3-65-iso` |
| GMMK 3, wired non-HE, 75% | ANSI | Experimental | `gmmk3-75-ansi` |
| GMMK 3, wired non-HE, 75% | ISO | Experimental | `gmmk3-75-iso` |
| GMMK 3, wired non-HE, 100% | ANSI | Experimental | `gmmk3-100-ansi` |
| GMMK 3, wired non-HE, 100% | ISO | Experimental | `gmmk3-100-iso` |

All listed variants have an editor layout, configuration protocol and built firmware. First installation, update and original-firmware recovery are implemented in the guide on all three platforms and require its checks to pass. The general workflow still needs its hardware acceptance run; native macOS/Linux package and device validation is also pending. Previous use of a model does not certify every revision, firmware feature, operating system or recovery scenario.

The app shows connected devices only. The physical ANSI/ISO layout is separate from the operating system's typing language. PRO revisions that share a normal USB ID are distinguished in update mode; the app must resolve the revision before writing.

## Not supported

- GMMK 3 PRO as a separate model: the available definitions do not establish an independent PRO board identity.
- GMMK HE variants and their Hall-effect features.
- GMBK and GMMK Eternal.
- Bluetooth and other wireless modes, including on the Numpad.
- Unknown revisions, modified bootloaders or boards that do not match the expected memory layout.

Do not select a similar model to bypass a recognition failure. Use the device-support issue template to report a new variant without uploading original firmware.

## Evidence and developer references

Layouts and LED mapping are pinned in `reference/qmk/sources.json`; GMMK 3 manufacturer sources are recorded in `reference/glorious-gmmk3/sources.json`. Runtime identities and memory geometry are in `firmware/targets.json`. Stock USB IDs come from `reference/glorious-stock-devices.json`.

Offline suites compare reference geometry, configuration round trips, generated firmware, backup association and error handling. They cannot validate physical wiring, LED color appearance, signal noise or recovery after power loss. See [hardware acceptance](hardware-acceptance.md), [device setup internals](device-setup.md) and [GMMK 3 port notes](gmmk3-port.md).
