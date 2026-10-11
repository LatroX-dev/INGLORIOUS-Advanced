# Third-party notices

INGLORIOUS includes or builds on the components below. Their original notices and licenses remain in force. This file does not replace the full license texts shipped with them.

| Component | Version / source | License and distribution |
| --- | --- | --- |
| QMK firmware | `7a1bbf37c5c07da4ea0a162bb139083a46ef40a1` | GPL-2.0-or-later and per-file notices; full pinned source and modifications accompany firmware binaries. |
| Glorious GMMK 3 definitions | Three pinned manufacturer commits in `reference/glorious-gmmk3/sources.json` | Existing QMK/per-file licenses preserved; original files and normalized ports included. |
| ChibiOS | `6170ddf92d55be54c89b708d1882eea229edeb2c` | GPL and other per-file licenses/exceptions. Entire pinned source tree included. |
| ChibiOS-Contrib | `5a9ad82b6ba4f649cdb800e8c2bbc9be2d3ce767` | Upstream mixed per-file licenses; entire pinned source tree included. |
| LUFA and printf | QMK's pinned submodules | Upstream BSD/MIT and per-file notices; pinned source trees included. |
| dfu-util | 0.11 official Windows x64 binary | GPL-2.0-or-later; source release accompanies binary. Copyright OpenMoko and contributors. |
| libusb | `1a906274a66dd58bf81836db1306902d4a7dc185` | LGPL-2.1-or-later; exact source accompanies DLL and statically linked dfu-util. Copyright libusb contributors. |
| libusb for macOS/Linux | 1.0.29 | LGPL-2.1-or-later; pinned source accompanies the native packages. Copyright libusb contributors. |
| Westberry WB32 updater protocol | `656f6a56354de86a8213b0d2dc8349b0aeb4b67e` | Apache-2.0. Copyright 2021 Westberry Technology (ChangZhou) Corp., Ltd. Protocol reference adapted into bounded C# requests and validation. |
| .NET / Windows Desktop runtime | 9.0.15 | MIT and component notices included with self-contained output. Copyright .NET Foundation and contributors. |
| Avalonia | 12.1.4, commit `2d79ba55fae1b1e71c69f93a668080c9029e6a31` | MIT. Copyright 2013–2026 The AvaloniaUI Project. Used by the macOS/Linux application. |
| HidSharp | 2.6.4 | Apache-2.0. Copyright 2010–2025 James F. Bellinger. Used by the macOS/Linux application. |
| Inno Setup | 6.7.3 | Inno Setup license. Copyright 1997–2026 Jordan Russell; portions copyright 2000–2026 Martijn Laan. The Windows installer retains the upstream notices and installs its license text. |
| Oxanium | Bundled SemiBold font | SIL OFL 1.1. Copyright and license retained beside the font and in packaged licenses. |

The Windows adapter's `Licenses/dfu-util-binary-provenance.txt` is the official binary archive's README. It identifies the libusb source used by that release. A generic current libusb release is not substituted for it.

## Modifying and rebuilding

All original application and adapter sources are available. The distributed app pins its reviewed native binaries to avoid accidentally loading a different flashing tool. To use a modified libusb or dfu-util, rebuild it from the accompanying source, update the corresponding integrity constants and regenerate the tool catalog, then rebuild the adapter and app. No private signing key is required. See `docs/public-release.md` and `docs/license-audit.md`.

Firmware changes are in the original modules, generated keymaps, GMMK 3 ports and hash-checked QMK patch scripts. Upstream archives remain unmodified. The source package contains upstream sources and project build/patch scripts.

Proprietary Glorious firmware, original device captures, drivers and private device records are not distributed.
