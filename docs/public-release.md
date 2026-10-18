# Build and release

This document is for maintainers. The [getting-started guide](getting-started.md) is for users.

## Clean source checkout

The public repository contains reviewed source, public reference definitions, documentation and licenses. It does not contain original firmware, personal profiles, device bindings, captures, local update scripts, toolchains or built binaries. Use the source export to produce that tree from a development workspace; do not push a development workspace wholesale.

Requirements: PowerShell 7.2+, Git, Python 3, `tar` and the exact .NET SDK in `global.json`. Build each desktop package on its matching operating system and architecture. The full firmware build currently runs on Windows x64; its reviewed output is shared by all app platforms. Reserve several gigabytes of free disk space. The first full build downloads about 600 MB for QMK MSYS, plus other dependencies; extracting the toolchain and building all firmware variants takes time. Windows dependencies are pinned in `scripts/build-dependencies.json`, native USB sources in `scripts/native-source-dependencies.json`, and corresponding source archives in `reference/source-distribution.json`.

```powershell
pwsh -NoProfile -File scripts/Build-PublicRelease.ps1 -Mode SourceChecks -OutputDirectory artifacts/source-checks
pwsh -NoProfile -File scripts/Build-PublicRelease.ps1 -Mode Windows -OutputDirectory artifacts/windows-release
```

Use new output directories. The full Windows build obtains the pinned dependencies, builds every firmware model, rejects binaries that differ from the reviewed firmware catalog, publishes the adapter and app, runs offline checks and prepares a package and checksum. It does not access keyboards. `-Offline` uses already prepared dependency files. `-FirmwareDirectory` accepts only binaries that match the reviewed catalog; it is not a way to substitute unreviewed images.

`Initialize-BuildDependencies.ps1 -Scope Native`, `-Scope Firmware` or `-Scope All` can prepare dependencies separately. Public builds do not require a private device, original backup or machine-specific path. Configuration files and generated tool catalogs belong to the build output.

The app checks published releases from this repository. Releases are regular releases; only the macOS and Linux packages are marked **Early Alpha**. **Settings → Updates** controls automatic checks and provides a manual check. Downloads require a matching platform asset with a GitHub SHA-256 digest; the app verifies its size and digest before offering installation. No profile, backup or device data is sent. Windows opens the normal installer after the existing unsaved-change and firmware guards. macOS and Linux provide the verified package and replacement instructions. There is no silent installation or downgrade.

Firmware stays bundled with each app package. Updating the app does not flash connected keyboards. The device guide offers applicable firmware updates from the included catalog.

The Windows build produces `INGLORIOUS-Advanced-<version>-win-x64-Setup.exe` with the pinned Inno Setup compiler. It installs for the current user, adds a Start menu entry and registers the uninstaller in Windows Settings. The installer checks for a running app or firmware operation before replacing files. Test installation, upgrades, default background startup and both data-retention choices with `scripts/Test-NativeWindowsInstaller.ps1`. Its test build uses a separate application identity and synthetic data directory. The intermediate Windows ZIP is for build verification, not an end-user download.

## Source export

```powershell
pwsh -NoProfile -File scripts/Export-PublicSource.ps1
New-Item -ItemType Directory -Force workspace/exports | Out-Null
pwsh -NoProfile -File scripts/Export-PublicSource.ps1 -Destination workspace/exports/release-source -Export
```

The exporter uses an exact reviewed allowlist, checks content for private local identifiers and verifies every copied file. The destination must be a new folder directly under `workspace/exports/` in the source project. Existing directories are never cleaned or merged. The entire `workspace/` directory is excluded from source exports and Git tracking. The manifest records the exported files. Review that manifest before creating a Git commit.

Keep separate publication checkouts under `workspace/public`, temporary validation fixtures under `workspace/checks`, and generated builds under `artifacts/`. Do not create working copies beside the project. Historical working copies belong in `workspace/archive` and are not build inputs. None of these local working directories belongs in a public release.

## Matching source package

```powershell
pwsh -NoProfile -File scripts/New-CorrespondingSource.ps1 `
  -PublicSourceDirectory <reviewed-public-folder> `
  -QmkDirectory firmware/qmk_firmware `
  -Destination artifacts/INGLORIOUS-Advanced-0.6.64-Source `
  -Download
```

The source package combines the reviewed export with exact upstream source archives. It includes QMK and its four required submodules, dfu-util, libusb and the WB32 protocol reference. It does not copy arbitrary working-tree files or private firmware. The output ZIP has a separate checksum and a manifest with source provenance. Source files remain available under their existing licenses; see [license details](license-audit.md).

Keep this source package downloadable beside the matching app package. A link to a changing upstream branch is not a replacement for the matching sources. Inspect the final firmware catalog, native binary pins and source versions together.

## Native macOS and Linux packages

The Avalonia app includes the same first-installation, backup, update and original-firmware restoration workflow as the Windows WPF app. Its adapter and native USB dependencies are built for Linux x64, macOS Intel or macOS Apple silicon. The package contains the reviewed firmware images, tools and license notices; no separate developer tool installation is required to use its firmware guide.

On Linux, install the build tools `build-essential`, `pkg-config`, `patchelf` and `xvfb` in addition to the common prerequisites. On macOS, install Xcode Command Line Tools. Run this on the matching host, using the 16 binaries produced by the reviewed firmware build:

```powershell
pwsh -NoProfile -File scripts/Build-DesktopRelease.ps1 `
  -Runtime linux-x64 `
  -FirmwareDirectory <reviewed-firmware-folder> `
  -OutputDirectory artifacts/linux-release
```

Use `osx-x64` or `osx-arm64` on the corresponding Mac. The script rejects a mismatched host architecture or firmware catalog. It builds pinned libusb 1.0.30 and dfu-util 0.11 sources, generates a native-tool catalog, publishes the adapter, prepares the setup assets and runs offline checks before packaging. All builds and tests in this script avoid device I/O.

Packages are named `INGLORIOUS-Advanced-<version>-<runtime>.tar.gz` and have matching SHA-256 files. macOS packages contain `INGLORIOUS Advanced.app`; Linux packages contain `application/INGLORIOUS Advanced` plus a USB-permission helper. macOS builds use ad-hoc signatures and are not notarized. Sign native tools before generating their catalog and the adapter before generating the setup-tool catalog; do not recursively re-sign pinned files afterward.

`Licenses/Desktop-ThirdParty.txt` preserves the locked desktop dependencies' license texts and notices. `reference/desktop-license-inventory.json` records their versions and provenance. After a deliberate dependency change, restore the locked packages, run `Collect-DesktopLicenses.ps1`, review the changes and rebuild the package. Runtime licenses must be retained in self-contained output.

Linux x64 and both macOS architectures have passed their native build, package and offline checks in CI for 0.6.47. macOS checks include strict bundle-signature verification and an offline launch from the extracted archive. Physical USB workflows remain untested on both platforms. A cross-published executable, screenshot or simulated test does not establish physical-device behavior. Record native build results separately from [physical acceptance](hardware-acceptance.md).

## Publishing checklist

1. Review the clean exported tree. Confirm there are no original firmware images, profiles, local paths, device serials or personal reports.
2. Run offline verification and package tests. Keep their results outside the normal user interface.
3. Complete and record the [hardware acceptance run](hardware-acceptance.md). Additional variants remain marked experimental until individually tested.
4. Create a version tag and a regular release, with short English notes from `CHANGELOG.md`. Keep it as a draft until its packages have been reviewed. Mark only the macOS and Linux packages as **Early Alpha**. Do not claim unperformed hardware or native-platform tests.
5. Attach `INGLORIOUS-Advanced-<version>-win-x64-Setup.exe`, validated native macOS/Linux archives, `INGLORIOUS-Advanced-<version>-Source.zip` and their checksums. Do not attach the internal Windows verification ZIP. State the tested platforms and remaining hardware limitations in the release notes.
6. Enable private vulnerability reporting and keep issue templates available.
7. Download the release assets again and check their hashes before announcing the release.

CI builds and tests are configured separately in `.github/workflows`. Native app jobs consume only the reviewed firmware outputs from the Windows build. Draft release creation must not be described as a public release. Windows builds are unsigned and macOS builds are not notarized: checksum verification detects altered files but does not authenticate a publisher.
