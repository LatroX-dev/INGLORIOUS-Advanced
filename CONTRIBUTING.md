# Contributing

INGLORIOUS Advanced is an alpha. Changes should make keyboard configuration easier without weakening model, backup or write checks.

## Build

Install Git, PowerShell 7.2+, Python 3, `tar` and the exact .NET SDK in `global.json`. The full Windows x64 build needs several gigabytes of free disk space; its first firmware-toolchain download is about 600 MB. Use the clean-build instructions in [docs/public-release.md](docs/public-release.md). The Windows app is in `src/InGlorious.Core`; the cross-platform preview is in `src/InGlorious.Desktop`.

Dependencies and native binaries are pinned. Do not silently update a dependency, accept a different firmware hash or replace an unknown device with a similar model. Changes to those pins need explicit review and a new source package.

## Test

Run the repository's offline verification script before proposing a release. Focused checks include:

```powershell
dotnet run --project tests/InGlorious.ModelChecks -c Release
dotnet run --project tests/InGlorious.DiscoveryChecks -c Release
dotnet run --project tests/InGlorious.SetupChecks -c Release
dotnet run --project tools/InGlorious.FirmwareAdapter -c Release -- --self-test
```

These commands use files and simulated devices. They do not flash hardware. Describe automated results separately from physical hardware observations. Use [the hardware acceptance checklist](docs/hardware-acceptance.md) for a new model or firmware workflow.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/InGlorious.Core` | Windows app, shared configuration models and firmware coordinator |
| `src/InGlorious.Desktop` | Avalonia desktop preview and portable USB backend |
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

- Keep customer text short and state a clear next step; follow `AGENTS.md`.
- Update all supported languages for UI changes. Keep changelogs short and in English.
- Include before/after screenshots for visible changes, without private profiles or identifiers.
- Preserve existing license and copyright notices. See `COPYRIGHT.md` for component scope.
- Keep personal firmware, profiles, device serials, machine paths and diagnostic logs out of commits.
- New public files must be reviewed and added to the source-export allowlist.

A hardware report should name the exact model, physical layout, app version, firmware version and checks performed. Do not attach a manufacturer's firmware image. Fixing one model does not automatically establish support for another revision.
