# Device setup and recovery

App 0.6.41 includes first installation, updates and original-firmware restoration on Windows, macOS and Linux without developer-device serials, fixed ports, workspace paths or pre-existing private backups. The entry points are App Settings and the connected device page. Firmware changes require a separate explicit confirmation. The native macOS/Linux package checks and physical-device acceptance runs are still pending; implementation is not evidence of a completed hardware test.

## Recognition and backup

The normal USB VID/PID is matched against the manufacturer catalog in `reference/glorious-stock-devices.json`. It identifies a model candidate, not an individual physical unit. PRO IDs shared by two MCU revisions stay ambiguous until the bootloader resolves the transport and flash geometry.

The wizard observes disconnection, then requires exactly one supported bootloader at the same complete physical USB location. Windows uses device-location paths; Linux and macOS use the USB bus and full physical port chain. Runtime HID endpoints are resolved through the operating system's device hierarchy, not matched by name or serial alone. Its libusb selection and independently checked fingerprint must match the transport, port, VID/PID, serial where available, interface and model geometry. Missing USB access, conflicting devices, unknown variants, protection or failed reads prevent installation. The app does not install drivers, elevate privileges or detach kernel drivers automatically.

Before confirmation, the adapter reads the entire internal flash twice. Both files must be complete and byte-identical. A structured USB descriptor inside the original image must agree with the originally observed stock VID/PID and selected model. This is a consistency check, not cryptographic proof of a manufacturer's firmware or physical board identity.

There are no user-specific identifiers in the distributed code or tool bundle. Each platform's app package includes the same reviewed firmware for 15 model/layout variants and a native adapter. The 13 additional variants remain visibly experimental; offline tests do not establish physical compatibility for untested revisions.

## Backup locations

The app stores original images and their recovery records together under the current user's local application-data directory, followed by `INGLORIOUS Advanced/Firmware/Devices`. Keep a separate copy of this entire folder, including its subfolders and records. A loose firmware file is not a replacement for the matching recovery archive.

| Platform | Default backup folder |
| --- | --- |
| Windows | `%LOCALAPPDATA%\INGLORIOUS Advanced\Firmware\Devices` |
| macOS | `~/Library/Application Support/INGLORIOUS Advanced/Firmware/Devices` |
| Linux | `~/.local/share/INGLORIOUS Advanced/Firmware/Devices` |

Linux installations using `XDG_DATA_HOME` store the folder there instead of `~/.local/share`. Backups stay on the computer; the app does not upload them.

## USB access

- Windows bootloaders require WinUSB. The setup guide offers the driver instructions only when needed; never replace the normal HID keyboard driver.
- Linux packages include `70-inglorious.rules` and `Install-UsbPermissions.sh`. The helper requires explicit administrator permission to install access rules for the supported USB IDs. Reconnect the device afterward and run the app as your normal user.
- macOS uses its native USB and HID interfaces. There is no Zadig or Windows driver installation. If the required interface cannot be opened or associated with one physical port, setup stops.

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

The app targets .NET 9: WPF on Windows x64 and Avalonia on Linux x64, macOS Intel and macOS Apple silicon. Both frontends use the same firmware coordinator and verification rules. The adapter source is in `tools/InGlorious.FirmwareAdapter`; its README describes the command contract and native binary provenance. The build process is described in [public-release.md](public-release.md):

```powershell
pwsh -NoProfile -File scripts/Build-PublicRelease.ps1 -Mode Windows -OutputDirectory artifacts/windows-release
# On a matching Linux or macOS host, with the reviewed firmware binaries:
pwsh -NoProfile -File scripts/Build-DesktopRelease.ps1 -Runtime linux-x64 -FirmwareDirectory <reviewed-firmware-folder> -OutputDirectory artifacts/linux-release
```

`Prepare-SetupAssets.ps1` requires all firmware hashes to match the reviewed firmware catalog. Updating the embedded tool catalog is an explicit build/review operation. It never accesses a keyboard. Use a new destination when preparing another asset bundle; pass `-p:SetupAssetsDirectory=<path>` to select it. Public source snapshots deliberately omit built images, original backups and native executables. Preparing release assets is therefore required before producing a distributable package.

`New-WindowsPackage.ps1` and `New-DesktopPackage.ps1` admit only reviewed firmware/tool files with matching lengths and SHA-256 values. The app installer itself performs no firmware operation. Hashes detect file changes; they are not publisher signatures. Release packages must include matching source and dependency license notices.

## Offline checks

```powershell
dotnet run --project tests/InGlorious.DiscoveryChecks -c Release
dotnet run --project tests/InGlorious.SetupChecks -c Release
dotnet run --project tests/InGlorious.PortableSetupChecks -c Release
dotnet run --project tools/InGlorious.FirmwareAdapter -c Release -- --self-test
```

The coordinator uses fake device-inventory and adapter responses and isolated filesystem fixtures. The portable suite runs the same coordinator cases against the shared desktop assembly; set `INGLORIOUS_TEST_FIRMWARE_DIRECTORY` to the reviewed firmware folder when running it separately. Adapter checks use emulated transports and flash memory, including interrupted writes, wrong identities and incomplete readbacks. They do not invoke native device I/O. Boot gestures and their primary sources are documented separately in `setup-boot-gestures.md`.
