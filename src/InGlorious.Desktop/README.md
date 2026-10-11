# INGLORIOUS Advanced desktop app

Avalonia app for macOS and Linux, app 0.6.44 with firmware 0.11.0. The separate Windows WPF app uses the same profiles, configuration models, firmware coordinator and validation rules. Native package and physical-device validation are still pending; a successful build or screenshot is not a hardware test.

## Features

The app shows connected devices and reads their current configuration before editing. It includes per-key and side lighting, separate logo lighting where present, effects with custom color sequences, layers, key assignments, encoder and slider controls, macros, shared libraries, profiles, standby and the optional screensaver. Stored settings run on the device with the app closed. Optional shared dimming between devices requires the app to stay open.

First installation, firmware updates and original-firmware restoration use the same guided workflow as Windows. Setup identifies the stock model, observes the physical disconnect/reconnect, checks the bootloader at the same USB port and saves two matching original-firmware reads before asking for permission to write. Updates and restores require the installation's matching local recovery archive. Missing access, ambiguous devices or failed verification stop the operation. There is no automatic write retry, privilege elevation or driver replacement.

The catalog includes 15 model/layout variants. GMMK PRO revision 1 ISO and GMMK Numpad have historical hardware-use evidence; the other 13 variants remain visibly experimental. See [model support](../../docs/model-support.md) and [hardware acceptance](../../docs/hardware-acceptance.md). The Numpad is **USB only — Bluetooth does not work** with INGLORIOUS firmware.

The device page uses the keyboard's explicit firmware-version response. Unknown, current or newer versions do not produce an update offer. Update notes are filtered by device and installed version. The app does not search online for firmware files.

For configuration transfers, the app saves and checks the current device configuration before writing, then checks a complete readback. The transfer dialog closes after confirmed success. A single matching device is selected automatically; uncertain results remain visible and are not retried.

## Install and run

Use the complete package for your operating system; individual executables or libraries are not sufficient. Self-contained packages include the .NET runtime. Configure a device with only one INGLORIOUS app at a time.

- **macOS:** use `osx-arm64` for Apple silicon or `osx-x64` for Intel. Extract the archive and drag **INGLORIOUS Advanced.app** to Applications. The bundle is ad-hoc signed, not Apple-notarized; macOS may require approval in Privacy & Security.
- **Linux x64:** extract the archive and run **application/INGLORIOUS Advanced**. If USB access is blocked, run `sudo bash Install-UsbPermissions.sh` from the extracted package, confirm the prompt and reconnect the keyboard. This installs access rules for supported USB IDs. Run the app as a normal user.
- **Windows:** the public Windows package uses the WPF app. Avalonia can also be built locally on Windows for development and offline UI checks.

The [getting-started guide](../../docs/getting-started.md) covers setup, updates and recovery. [Device setup](../../docs/device-setup.md) lists the backup locations and technical checks. Keep the complete recovery folder separately before reinstalling or replacing your computer; it is not part of the app package.

Profiles and preferences use `INGLORIOUS Advanced/data` under the platform's local application-data directory when running outside the source workspace. In a source workspace, the local `data` directory is used. The Settings dialog can open the data folder. Profiles, macros and lighting templates have separate import/export actions. Language follows the system by default, with English as fallback and twelve supported choices.

## Build and offline checks

Use the exact SDK from `global.json` and the locked dependencies. [Build and release](../../docs/public-release.md#native-macos-and-linux-packages) describes native prerequisites and complete packages.

```powershell
dotnet build src/InGlorious.Desktop/InGlorious.Desktop.csproj -c Release
dotnet run --project src/InGlorious.Desktop -c Release -- --smoke artifacts/desktop-smoke.txt --screenshot artifacts/desktop-pro.png
dotnet run --project src/InGlorious.Desktop -c Release -- --minimum --screenshot artifacts/desktop-minimum.png
# On the matching native host, using the reviewed firmware binaries:
pwsh -NoProfile -File scripts/Build-DesktopRelease.ps1 -Runtime linux-x64 -FirmwareDirectory <reviewed-firmware-folder> -OutputDirectory artifacts/linux-release
```

Use `osx-x64` or `osx-arm64` for a matching Mac. A plain `dotnet publish` does not prepare the native firmware tools and reviewed asset catalogs; use the release script for a complete package.

`--smoke` and `--screenshot` disable hardware discovery, HID access, user-data writes, file/recording dialogs and background synchronization. They write only the explicitly requested reports or images. Offline checks cover the model layouts, editor state, configuration roundtrips, firmware-guide states and accepted/rejected device descriptors. The portable setup suite uses simulated devices and the shared coordinator; it does not flash hardware.

Preview the firmware guide offline with `--device-setup`, optionally adding `--setup-step select|disconnect|connect|backup|confirm|reconnect|complete` and `--setup-update` or `--setup-restore`.

Native macOS/Linux launches, USB permissions, physical-port association, first installation, update, restore and post-flash typing/lighting still require a recorded acceptance run on those platforms. Historical Windows firmware results do not establish Avalonia transport behavior.

Technical foundations: [Avalonia](https://docs.avaloniaui.net/docs/get-started), [HidSharp](https://github.com/IntergatedCircuits/HidSharp). Oxanium SemiBold is embedded; no system font installation is needed. Its SIL Open Font License is included in `Licenses/Oxanium-OFL.txt`.
