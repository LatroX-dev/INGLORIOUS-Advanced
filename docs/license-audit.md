# License and source-distribution record

Updated for app 0.6.46 and included firmware 0.11.0. This is developer release documentation; it is not UI text or a guarantee that future dependencies have the same terms. Final native package and corresponding-source checks remain release requirements.

## Project license

The original desktop application, adapter and release tooling use GPL-3.0-or-later. `LightingPreviewEngine.cs` is explicitly derived from GPL-2.0-or-later firmware. The portable HID library and Westberry protocol reference use Apache-2.0. Selecting GPLv3 for the combined application accommodates these terms while preserving each component's original notice. GNU confirms the later-version route for GPLv2-or-later code, and Apache confirms compatibility with GPLv3: [GNU compatibility guide](https://www.gnu.org/licenses/quick-guide-gplv3.html), [Apache compatibility guidance](https://apache.org/licenses/GPL-compatibility.html).

Firmware modules retain GPL-2.0-or-later. QMK/ChibiOS/ChibiOS-Contrib and imported manufacturer sources retain all per-file terms and exceptions. The app's license does not automatically replace them. Oxanium remains OFL 1.1. Proprietary stock firmware is not part of this distribution.

## Matching source

The source package is created with `scripts/New-CorrespondingSource.ps1` from an already reviewed public export and pinned upstream commits. Its manifest lists every distributed archive and checksum. It includes:

- The complete reviewed application, adapter, firmware overlays, patch/build scripts and tests.
- The exact QMK base and the four submodules used by these targets: ChibiOS, ChibiOS-Contrib, LUFA and printf.
- dfu-util 0.11's original source tarball.
- The libusb commit identified by the official dfu-util 0.11 binary README, `1a906274a66dd58bf81836db1306902d4a7dc185`.
- The libusb 1.0.29 source archive used by the native macOS and Linux packages.
- The upstream WB32 updater reference, retained for its protocol provenance and Apache notices.

Archives generated from Git include tracked content at the exact commit, not the developer checkout's working files or `.git` directory. Original project modifications are included separately in the reviewed public source. QMK patches check their source hashes before applying. Compiler tools are not bundled into the application.

The Windows installer is built with Inno Setup 6.7.3. `scripts/installer-dependencies.json` pins the official compiler distribution, verified publisher and compiler hash. The generated installer retains Inno Setup's notices and installs its unmodified license as `Licenses/Inno-Setup.txt`. The compiler is a separate build tool, not an application dependency. Its license is distinct from the app's GPL license.

The Windows dfu-util binary provenance is `tools/InGlorious.FirmwareAdapter/Licenses/dfu-util-binary-provenance.txt`. Its build used MinGW on Ubuntu 20.04, dfu-util 0.11 and libusb `v1.0.24-66-g1a90627`. The project's primary [build instructions](https://dfu-util.sourceforge.net/build.html) explain MinGW cross-compilation and static linking. Native macOS/Linux tools are built from the source archives pinned in `scripts/posix-native-dependencies.json`. Use the versions in this repository's source-distribution manifests instead of substituting newer examples.

## Rebuilding modified native tools

Extract the accompanying dfu-util and libusb sources. Generate libusb's configure files with `./autogen.sh`, then build libusb and dfu-util for `x86_64-w64-mingw32` using the upstream configure/make instructions. The static dfu-util executable can be built with `make LDFLAGS=-static`. Keep notices with the rebuilt files.

For macOS and Linux, `Build-PosixNativeTools.ps1` builds the pinned source archives on the matching host and records the resulting library and dfu-util hashes in `NativeToolCatalog.json`. `Build-DesktopRelease.ps1` publishes the adapter against that catalog and regenerates `SetupToolCatalog.json` before rebuilding the app. Modified native sources and their pins must be reviewed before rebuilding. Windows builds retain their separately reviewed native binaries and hashes.

The app checks native binary hashes because those programs can write device firmware. Replacing a bundled library alone is rejected; regenerate the matching catalogs and rebuild the adapter and app together. All required source and build scripts are supplied; no private signing key or developer device is needed. The matching source archive must contain the exact native source versions used by every package in the release.

## Release checks

Do not publish a binary-only release. Attach matching application/upstream source and license texts alongside the download. Confirm the source package covers the exact native versions and firmware catalog in that download. Include package dependency licenses for each platform's output. Keep runtime license notices in self-contained packages. WPF and Avalonia dependencies are separate inventories; a Windows package audit does not certify a macOS or Linux package.

The source bundle does not contain original firmware, personal profiles, device bindings, local flash logs or driver installers. GPL source obligations do not require publishing those private captures.
