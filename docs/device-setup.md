# Generic Windows device setup

App 0.6.40 provides first installation, updates and original-firmware restoration without developer-device serials, fixed ports, workspace paths or pre-existing private backups. The entry points are App Settings and the connected device page. Firmware changes require a separate explicit confirmation.

## Recognition and backup

The normal USB VID/PID is matched against the manufacturer catalog in `reference/glorious-stock-devices.json`. It identifies a model candidate, not an individual physical unit. PRO IDs shared by two MCU revisions stay ambiguous until the bootloader resolves the transport and flash geometry.

The wizard observes disconnection, then requires exactly one supported bootloader at the same complete Windows USB location. Its libusb selection and independently checked fingerprint must match the transport, port, VID/PID, serial where available, interface and model geometry. A missing WinUSB driver, conflicting devices, unknown variant, protection or failed read prevents installation. Drivers are not installed automatically.

Before confirmation, the adapter reads the entire internal flash twice. Both files must be complete and byte-identical. A structured USB descriptor inside the original image must agree with the originally observed stock VID/PID and selected model. This is a consistency check, not cryptographic proof of a manufacturer's firmware or physical board identity.

Original images and recovery records are stored under `%LOCALAPPDATA%/INGLORIOUS Advanced/Firmware/Devices`. There are no user-specific identifiers in the distributed code or tool bundle. The app packages reviewed firmware for 15 model/layout variants and the bounded Windows adapter. The 13 additional variants remain visibly experimental; offline tests do not establish physical compatibility for untested revisions.

## Installation and recovery association

Firmware 0.11.0 binaries remain unchanged. The installer prepares a full flash image, adds a checked per-installation identifier in a reserved gap, and starts with an empty 32-KiB configuration area. The identifier contains the board number, a random installation UUID and the original-image SHA-256. It is not a hardware UID and can be cloned with a complete image.

Immediately before writing, two fresh reads must still match the captured original. After writing, two full reads must match the exact target image. The app independently checks the returned files and their content before asking for a normal reconnect. Neither the wizard nor adapter retries a write automatically.

Restoration requires the installed identifier, matching local original archive and, for STM32, the original USB serial. The WB32 transport exposes no independently established immutable per-unit identifier. Physical substitution by an identical or cloned device cannot be ruled out by USB metadata. The cable/port observation and installation record reduce accidental mismatches; they are not hardware authentication.

The backup covers internal flash. ROM, protection/option bytes, external profile storage and wireless-module data are excluded. A partial write, especially damage to the identity page, may require separate recovery; unconditional recoverability is not promised. The original archive is retained even after failure.

## Updating an existing installation

The normal-mode RAW-HID handshake and explicit version query must identify the connected board before the app offers a newer included version. Missing, equal or newer version reports do not produce an update offer. The writer separately checks the captured code against `KnownFirmwareCatalog`, the existing installation identifier and the matching original archive. A reported version alone never authorizes a flash.

Two complete current reads must agree. The update keeps the installation identifier and the full 32-KiB configuration tail byte-for-byte, and binds the write to the freshly captured image hash. Two new before-reads must still agree immediately before writing; two full readbacks must match afterwards. There is no downgrade or fallback to an arbitrary raw image.

The first public catalog starts with 0.11.0. Unmarked development firmware and code absent from the reviewed catalog are not update sources. Such installations need their existing separate recovery procedure to return to original firmware before using the general first-installation flow. Private development update scripts and device bindings are not part of the public repository.

## Build and package

The app targets Windows x64/.NET 9. The cross-platform Avalonia preview currently does not contain this installer. The adapter source is in `tools/InGlorious.FirmwareAdapter`; its README describes the command contract and native binary provenance. The complete reproducible setup is described in [public-release.md](public-release.md):

```powershell
pwsh -NoProfile -File scripts/Build-PublicRelease.ps1 -Mode Windows -OutputDirectory artifacts/windows-release
```

`Prepare-SetupAssets.ps1` requires all firmware hashes to match the reviewed firmware catalog. Updating the embedded tool catalog is an explicit build/review operation. It never accesses a keyboard. Use a new destination when preparing another asset bundle; pass `-p:SetupAssetsDirectory=<path>` to select it. Public source snapshots deliberately omit built images, original backups and native executables. Preparing release assets is therefore required before producing a distributable package.

`New-WindowsPackage.ps1` admits only the exact reviewed firmware/tool files with matching lengths and SHA-256 values. The app installer itself performs no firmware operation. Hashes detect file changes; they are not publisher signatures. The release includes matching source and dependency license notices.

## Offline checks

```powershell
dotnet run --project tests/InGlorious.DiscoveryChecks -c Release
dotnet run --project tests/InGlorious.SetupChecks -c Release
dotnet run --project tools/InGlorious.FirmwareAdapter -c Release -- --self-test
```

The coordinator uses fake PnP and adapter responses and isolated filesystem fixtures. Adapter checks use emulated transports and flash memory, including interrupted writes, wrong identities and incomplete readbacks. They do not invoke native device I/O. Boot gestures and their primary sources are documented separately in `setup-boot-gestures.md`.
