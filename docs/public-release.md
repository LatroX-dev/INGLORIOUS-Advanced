# Build and release

This document is for maintainers. The [getting-started guide](getting-started.md) is for users.

## Clean source checkout

The public repository contains reviewed source, public reference definitions, documentation and licenses. It does not contain original firmware, personal profiles, device bindings, captures, local update scripts, toolchains or built binaries. Use the source export to produce that tree from a development workspace; do not push a development workspace wholesale.

Requirements: Windows x64 for the main package, PowerShell 7.2+, Git, Python 3, `tar` and the exact .NET SDK in `global.json`. Reserve several gigabytes of free disk space. The first full build downloads about 600 MB for QMK MSYS, plus other dependencies; extracting the toolchain and building all firmware variants takes time. Native firmware dependencies are pinned in `scripts/build-dependencies.json`; source archive pins are in `reference/source-distribution.json`.

```powershell
pwsh -NoProfile -File scripts/Build-PublicRelease.ps1 -Mode SourceChecks -OutputDirectory artifacts/source-checks
pwsh -NoProfile -File scripts/Build-PublicRelease.ps1 -Mode Windows -OutputDirectory artifacts/windows-release
```

Use new output directories. The full Windows build obtains the pinned dependencies, builds every firmware model, rejects binaries that differ from the reviewed firmware catalog, publishes the adapter and app, runs offline checks and prepares a package and checksum. It does not access keyboards. `-Offline` uses already prepared dependency files. `-FirmwareDirectory` accepts only binaries that match the reviewed catalog; it is not a way to substitute unreviewed images.

`Initialize-BuildDependencies.ps1 -Scope Native`, `-Scope Firmware` or `-Scope All` can prepare dependencies separately. Public builds do not require a private device, original backup or machine-specific path. Configuration files and generated tool catalogs belong to the build output.

The application uses the firmware included in its app package. GitHub release creation does not make an online app updater; runtime firmware version filtering remains local to the included catalog.

## Source export

```powershell
pwsh -NoProfile -File scripts/Export-PublicSource.ps1
pwsh -NoProfile -File scripts/Export-PublicSource.ps1 -Destination <new-public-folder> -Export
```

The exporter uses an exact reviewed allowlist, checks content for private local identifiers and verifies every copied file. The destination must be new and outside the development source root. Existing directories are never cleaned or merged. The manifest records the exported files. Review that manifest before creating a Git commit.

## Matching source package

```powershell
pwsh -NoProfile -File scripts/New-CorrespondingSource.ps1 `
  -PublicSourceDirectory <reviewed-public-folder> `
  -QmkDirectory firmware/qmk_firmware `
  -Destination artifacts/corresponding-source `
  -Download
```

The source package combines the reviewed export with exact upstream source archives. It includes QMK and its four required submodules, dfu-util, libusb and the WB32 protocol reference. It does not copy arbitrary working-tree files or private firmware. The output ZIP has a separate checksum and a manifest with source provenance. Source files remain available under their existing licenses; see [license details](license-audit.md).

Keep this source package downloadable beside the matching app package. A link to a changing upstream branch is not a replacement for the matching sources. Inspect the final firmware catalog, native binary pins and source versions together.

## Platform previews

The Avalonia project can be built from source on Windows, macOS and Linux. Preview packages are separate from the main Windows WPF app. They do not install or restore firmware. Building or taking screenshots on another host does not demonstrate native USB behavior on the target operating system.

`Licenses/Desktop-ThirdParty.txt` preserves the locked preview dependencies' license texts and notices. `reference/desktop-license-inventory.json` records their versions and provenance. After a deliberate dependency change, restore the locked packages, run `Collect-DesktopLicenses.ps1`, review the changes and rebuild the preview package. Runtime licenses must be retained in self-contained output.

## Publishing checklist

1. Review the clean exported tree. Confirm there are no original firmware images, profiles, local paths, device serials or personal reports.
2. Run offline verification and package tests. Keep their results outside the normal user interface.
3. Complete and record the [hardware acceptance run](hardware-acceptance.md). Additional variants remain marked experimental until individually tested.
4. Create a version tag and a **prerelease**, with short English notes from `CHANGELOG.md`. Do not claim unperformed hardware or native-platform tests.
5. Attach the Windows ZIP, matching source ZIP and checksums. Attach macOS/Linux packages only as clearly identified previews with their license files.
6. Enable private vulnerability reporting and keep issue templates available.
7. Download the release assets again and check their hashes before announcing the release.

CI builds and tests are configured separately in `.github/workflows`. Draft release creation must not be described as a public release. The current Windows build is unsigned: checksum verification detects altered files but does not authenticate a publisher.
