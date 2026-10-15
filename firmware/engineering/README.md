# Offline engineering target

`targets.json` contains one opt-in derivative: `gmmk3-pro-100-iso`, board 16,
runtime USB `FEED:1C10`. The normal `firmware/targets.json` now also includes this
sixteenth target with the same image layout and identity. The optional engineering
selector still requires this exact board and a separate artifact directory.
It does not certify physical acceptance or publish a release.

The build copies the provenance-checked GMMK3 p100 ISO matrix, physical key map,
encoder and AW20216S LED tables into `gmmk/inglorious3pro/p100/iso`. The normal
`gmmk/inglorious3` prefix and preserved manufacturer inputs are not overwritten.
Vendor binary comparison supports this derivative; it does not establish PCB
equivalence or validate its physical behavior.

Board 16 uses the pinned `WB32FQ95xC` linker template: 36 KiB RAM, a protected
28 KiB resident-loader prefix, 200,448 application bytes at `0x08007000`, a
256-byte identity page at `0x08037F00`, and 32 KiB trailing internal storage at
`0x08038000..0x0803FFFF`. Its two logical
storage slots are 7,680 bytes each. Both storage and the WB32 bulk writer require
`IG_RETAINED_LOADER_BUILD=1`; the bulk writer additionally requires 256 KiB flash.
Existing WB32 board IDs retain the 128 KiB compile requirement.

Build only by naming the exact target and a separate artifact directory:

```powershell
pwsh -NoProfile -File firmware/Build-Firmware.ps1 -Engineering -Board gmmk3-pro-100-iso -OutputDirectory artifacts/engineering/gmmk3-pro-100-iso
python firmware/Verify-CandidateBuild.py artifacts/engineering/gmmk3-pro-100-iso --engineering --board gmmk3-pro-100-iso
pwsh -NoProfile -File firmware/tests/Run-EngineeringChecks.ps1
pwsh -NoProfile -File firmware/tests/Run-BulkChecks.ps1
pwsh -NoProfile -File firmware/tests/Run-StorageChecks.ps1
```

The static verifier also needs the separate offline C# fixtures in the build
directory's `fixtures` folder. The engineering port harness accepts that folder
with `-Engineering -Board gmmk3-pro-100-iso -FixtureDirectory <path>`.
These fixtures exercise the C# configuration encoder and do not authorize hardware access.

The target revision is 0.11.4, build ID `000B0401`, with a pure-red fallback
when no valid INGLORIOUS configuration is saved. Saved configurations retain their
colors; this fallback does not import Glorious CORE settings. The separate
`../boards/gmmk3-pro-100-iso/ig_board_version.h` is selected only for board 16.
All sixteen current targets use 0.11.4; earlier released artifacts remain immutable. When explicitly using
`-Engineering`, the build manifest records `engineering=true`, `releaseCandidate=false`,
`hardwareFlashed=false` and `hardwareAcceptanceTested=false`. The output is not
added to a release catalog. Building and checking it performs no device I/O.
The application BIN starts with its own vectors; it has no prefix padding or
vendor-loader bytes. ELF and HEX load records also exclude the protected prefix.
An installation workflow must independently preserve the resident loader exactly;
this build path does not implement or authorize that workflow.

The generated physical recovery chord is Space `(9,4)` plus B `(4,5)`, independent
of remapped bindings. Entering ROM with this chord was observed on the original
firmware. An earlier retained-loader image passed a basic install/recovery/restore
round trip on one unit; this 0.11.4 revision and full keyboard behavior still need
physical acceptance. Private hardware evidence and limitations belong in the
artifact report, not in public firmware manifests.

The replacement image's own chord runs in a software post-initialization hook;
a failure earlier in startup can prevent that hook from running. The relocated
layout leaves the existing resident loader intact so its separate pre-application
recovery logic can remain available. Private static inspection supports this
architecture, and the earlier replacement passed a recovery round trip. Recovery
from an intentionally failing application has not been physically tested. No
hardware boot pin/button procedure is established here.
