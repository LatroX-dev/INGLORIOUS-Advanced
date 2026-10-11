# Contributing

INGLORIOUS Advanced is an alpha. Changes should make keyboard configuration easier without weakening model, backup or write checks.

## Build

Install Git, PowerShell 7.2+, Python 3, `tar` and the exact .NET SDK in `global.json`. The full Windows x64 build needs several gigabytes of free disk space; its first firmware-toolchain download is about 600 MB. Native Linux/macOS packages require a matching host and the reviewed firmware outputs. Use the clean-build instructions in [docs/public-release.md](docs/public-release.md). The Windows WPF app is in `src/InGlorious.Core`; the Avalonia app for macOS/Linux is in `src/InGlorious.Desktop`. Both use the shared firmware coordinator.

Dependencies and native binaries are pinned. Do not silently update a dependency, accept a different firmware hash or replace an unknown device with a similar model. Changes to those pins need explicit review and a new source package.

Package restore audits direct and transitive NuGet dependencies. Known vulnerabilities fail the build; CI also fails if the advisory feed cannot be checked. Investigate and fix an advisory instead of suppressing it to complete a release.

The product name is **INGLORIOUS Advanced**. Internal project directories and .NET namespaces, such as `InGlorious.Core`, retain their technical identifiers; the paths below are the actual source paths. The Windows application is `INGLORIOUS Advanced.exe`, its installer is `INGLORIOUS-Advanced-<version>-Setup.exe`, and the matching source archive is `INGLORIOUS-Advanced-<version>-Source.zip`.

## Test

Run the repository's offline verification script before proposing a release. Focused checks include:

```powershell
dotnet run --project tests/InGlorious.ModelChecks -c Release
dotnet run --project tests/InGlorious.DiscoveryChecks -c Release
dotnet run --project tests/InGlorious.SetupChecks -c Release
dotnet run --project tests/InGlorious.PortableSetupChecks -c Release
dotnet run --project tests/InGlorious.AutostartChecks -c Release
dotnet run --project tests/InGlorious.StartupGuardChecks -c Release
dotnet run --project tests/InGlorious.BackgroundChecks -c Release
dotnet run --project tools/InGlorious.FirmwareAdapter -c Release -- --self-test
```

These commands use files and simulated devices. They do not flash hardware. The setup suites require the reviewed firmware binaries; set `INGLORIOUS_TEST_FIRMWARE_DIRECTORY` to that folder when running the portable suite separately. Describe automated results separately from physical hardware observations. Use [the hardware acceptance checklist](docs/hardware-acceptance.md) for a new model, operating system or firmware workflow.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/InGlorious.Core` | Windows app, shared configuration models and firmware coordinator |
| `src/InGlorious.Desktop` | Avalonia app, firmware guide and portable USB backend |
| `firmware/inglorious` | On-device configuration, effects, storage and input behavior |
| `firmware/ports` | Additional keyboard definitions and normalization records |
| `tools/InGlorious.FirmwareAdapter` | Bounded USB firmware read/write adapter |
| `tests` | App and adapter tests using simulated devices |
| `firmware/tests` | Native firmware checks without device writes |
| `reference` | Pinned public hardware definitions and dependency catalogs |
| `scripts` | Dependency setup, builds, package verification and source export |
| `docs` | User guides and separate developer documentation |
| `Licenses` | Preserved dependency licenses and notices |

Generated files go under ignored build/dependency directories. Personal profiles and firmware backups are never source files.

## Changes

- Keep customer text short and state a clear next step.
- Update all supported languages for UI changes. Keep changelogs short and in English.
- Include before/after screenshots for visible changes, without private profiles or identifiers.
- Preserve existing license and copyright notices. See `COPYRIGHT.md` for component scope.
- Keep personal firmware, profiles, device serials, machine paths and diagnostic logs out of commits.
- New public files must be reviewed and added to the source-export allowlist.

A hardware report should name the exact model, physical layout, app version, firmware version and checks performed. Do not attach a manufacturer's firmware image. Fixing one model does not automatically establish support for another revision.
