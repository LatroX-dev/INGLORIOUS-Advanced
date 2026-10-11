# Generic firmware adapter

This adapter is independent of the original device-bound updater wrappers. It contains no
user serial number, private port, original-image hash or private binding file. It uses the
fifteen source-backed board geometries in `Model.Get`; that is not a claim that all fifteen
boards have been tested on hardware. The coordinator must first establish the normal USB
model and observe its physical disconnect/reconnect into the bootloader at the same port.

No operation runs on launch or from help. The executable accepts only:

```
InGlorious.FirmwareAdapter --request REQUEST.json --response NEW_RESPONSE.json
InGlorious.FirmwareAdapter --self-test
InGlorious.FirmwareAdapter --discover
```

`--self-test` uses emulated transports and temporary files only. It neither loads libusb nor
runs dfu-util. Request/response use UTF-8 JSON with exact camelCase property names. Unknown
request, selection, bundle and capture-manifest properties are rejected. An operation failure,
missing response or nonzero exit must never be interpreted as successful flashing. `errorCode`
and the diagnostic files are technical evidence, not UI text.

`--discover` returns USB descriptors, bus, address and full port chains without opening or
claiming a device. The macOS and Linux coordinator uses this inventory for the observed
normal-device → disconnect → bootloader transition. Discovery is not proof of read/write
permission; a separate selected-device probe must succeed before capture becomes available.

## Request contract, schema 1

All requests contain `schema:1`, `operation`, `sessionDirectory` (an absolute, **not yet
existing** directory), and `confirmWrite`. The adapter creates exactly this evidence directory.
The separate response path must also be new. Ordinary operations use `confirmWrite:false`.

| Operation | Additional fields | Device changes |
|---|---|---|
| `list` | none; `deviceId` is optional and ignored | USB enumeration and serial string reads only |
| `probe` | `deviceId`, `selection` | USB descriptors and WB `GET_INFO` only |
| `capture` | previous fields, `expectedFingerprintSha256` | Two full internal-flash reads only |
| `install` | previous fields, `backupPath`, `bundlePath`, `expectedBackupSha256`, `expectedInstallationId`, `confirmWrite:true` | Two new before-reads, explicit full-image write, two full readbacks |
| `restore` | same as install except no bundle | Two new before-reads, exact own original, two full readbacks |
| `update` | same as install plus `expectedCurrentSha256` | Two fresh reads bound to the captured running image; preserves all 32KiB configuration bytes and the original recovery identity; two full readbacks |

Selection returned by `list.devices[]`:

```json
{"vid":13357,"pid":57248,"bus":1,"portPath":"2.3","serial":null,"transport":"wb32-dfu"}
```

VID/PID are JSON integers, bus is a byte, portPath is libusb's physical port chain without its
bus prefix. Transport is `wb32-dfu` or `stm32-dfuse`. The STM32 selector additionally requires
its observed twelve-hex-digit USB serial. Never manufacture a selector from a model name.

`expectedFingerprintSha256` is returned by probe and checked again by capture/install/update/restore.
It is SHA-256 of this adapter's serialized fingerprint, not a hardware UID. The fingerprint
includes transport, VID/PID, bus/port, serial, USB configuration/interface/alternate, device
revision, WB type code/protocol revision if present, and the exact MCU memory geometry.
`uniqueId` is currently null. WB `identityStrength` is `transport-session`; a type code is not
a unique specimen identifier. STM32 strength is `usb-serial-and-port`, without claiming that
the twelve-character bootloader serial is the full MCU UID.

Capture emits `original-1.bin`, `original-2.bin` and `capture.json`. The response artifacts have
`path` relative to `evidenceDirectory`, `bytes`, `sha256`, `kind`; the two images have kind
`original-read`. The manifest's artifacts contain **only** these two entries. The capture can
also read already-installed firmware for selecting a restore record; its filename/kind does
not certify that the bytes are manufacturer firmware. Initial installation must additionally
pass the coordinator's stock descriptor/model check. The adapter rejects an IGIDENT1-bearing
image when it is supplied as the archived *original* for a write.

`backupPath` identifies the existing original `capture.json`; `expectedBackupSha256` is the
uppercase SHA-256 of **both identical original flash images**, not of that JSON file.
Both originals, the manifest, the bundle and the exact write image are rechecked and held with
file locks for the write (Windows read sharing; POSIX exclusive advisory locks). Fresh
before-images are also locked. On POSIX the verified STM32 image is streamed to dfu-util's
standard input so renaming a pathname cannot replace the bytes it writes.

## Bundle and installation identity

The coordinator validates its embedded source catalog, builds a full image, then supplies:

```json
{
  "schema":1,"deviceId":"numpad","boardId":2,"transport":"wb32-dfu",
  "flashBase":134217728,"flashBytes":131072,"sramBytes":28672,
  "image":{"path":"installation.bin","bytes":131072,"sha256":"UPPERCASE_SHA256"},
  "releaseId":"0.11.0"
}
```

The adapter revalidates geometry, ARM vectors, byte length, checksum and image paths; paths
must remain below the bundle directory and must not be reparse points. Both the coordinator
and adapter additionally require the target application's immutable bytes to match the
embedded `KnownFirmwareCatalog.json` release history. Stock model discovery belongs to the
coordinator. A checksum alone is not a signature.

`InstallationIdentity.cs` is linked directly from Core: `IGIDENT1`, schema1, board ID, reserved
zeroes, random installation GUID, original flash SHA-256, IEEE CRC32. The 64-byte record starts
at `flashBytes - 32768 - 256`. Its remaining 192-byte page tail and the whole 32KiB configuration
area must be FF on initial installation. This modifies the composed installation image, not
the frozen firmware binary or any original backup. It is a recovery association, **not** an
immutable MCU UID. Flashing unrelated software or copying the record can remove/clone it.

For install, the new pair must equal the archived original byte-for-byte. For restore, the
new pair must contain the expected GUID, board ID and original checksum; the archived STM32
serial must also match. Original vector plausibility is checked before restore. The adapter
never selects an arbitrary backup solely by USB model or port.

For update, the fresh pair must additionally match `expectedCurrentSha256`, the full-image
hash captured by the coordinator. Both current and target application bytes must match an
approved same-model release in `KnownFirmwareCatalog.json`; the unused application gap and
identity-page padding must remain erased. Downgrades are rejected. The exact final 32KiB
configuration range and installation identity must be equal before and after. The original
capture manifest and its two original reads are still mandatory and locked; an update never
creates a manufacturer-original record from INGLORIOUS bytes. Existing installations without
the recovery identity are deliberately not eligible for this path. Recovery after an interrupted
full-image update is subject to the same transport and identity limits below; retaining bytes
in the target image does not make writes power-loss atomic.

The write response is `status:"verified"`, `writeAttempted:true`, `verified:true` only after two
complete matching readbacks, rechecked identity, command logging and device disposal. The
readbacks have kind `verified-read`; before-images have kind `pre-write-read`. A successful
install additionally leaves `installation.json`, and returns `installationId`. The user must
physically reconnect; there is no reset, jump or leave command.

## Transport boundaries and recovery limits

* WB32: selected libusb bus/port/VID/PID (and serial if supplied), one configuration, interface0,
  alternate0, FF/FF/00 interface and 64-byte bulk endpoints01/81. The same handle remains open
  through capture/write/verification. Exact manufacturer/product strings are required.
  `GET_INFO` uses the upstream Westberry family mask `(chipId & 0x3FFF)==0x2980`, protocol
  revision3, 128KiB flash and 28KiB SRAM; there is no Numpad-only complete type-code comparison.
  Only INFO0, bounded READ, one aligned 256-byte page erase and full-page programming exist in
  the packet allowlist. A protected/error response stops immediately; no unlock, retry, mass
  erase, option writes, reset or jump is available. Installation writes the checked identity
  page first; restore preserves it until last. Each page is read back, followed by two complete
  reads. A failure during the identity page itself remains an uncertain result.
* STM32: pinned dfu-util0.11, exact serial and bus/port, configuration1/interface0/alternate0.
  The internal-flash map must be 128 writable/erasable/readable sectors of 2KiB at0x08000000.
  Only `-l`, bounded `-U` and one exact complete `-D` are generated. No `leave`, `force`,
  `unprotect`, option-byte operation or reset is generated. STM32's full-image download does
  not promise identity-page-first ordering.
* Operations have a 300-second device deadline; raw failures are preserved without automatic
  retry. A partial write before the recovery identity is intact cannot be claimed recoverable
  by the generic marker-based path after process loss. The archived original remains intact.
* Internal flash is captured. ROM, option/protection bytes, external SPI/EEPROM and wireless
  module storage are explicitly excluded. No driver is installed or switched.

The FQ95 reference manual documents a 96-bit UID at0x1FFFF7E8, but ROM DFU read permission for
that address is not established. The adapter intentionally makes no speculative reads outside
internal flash. [Westberry FQ95 reference manual](https://www.westberrytech.com/uploads/file/WB32FQ95xx/EN_RM2905025_WB32FQ95xx_V01.pdf)

The WB command encoding is derived from the pinned [Westberry updater source](https://github.com/WestberryTech/wb32-dfu-updater/tree/656f6a56354de86a8213b0d2dc8349b0aeb4b67e).
The old project-specific reader/writer and private update wrappers remain unchanged.

## Build and package

```
dotnet build tools/InGlorious.FirmwareAdapter/InGlorious.FirmwareAdapter.csproj -c Release
dotnet tools/InGlorious.FirmwareAdapter/bin/Release/net9.0/InGlorious.FirmwareAdapter.dll --self-test
```

Update self-tests use reviewed firmware files. An exported build can supply the directory
explicitly with `--self-test --firmware-directory PATH`; otherwise the test runner searches
the installed `Firmware` folder or this repository's reviewed candidate output. It accesses
only these local files, fake transports and disposable temporary directories.

The tool has no third-party NuGet dependency. Copy its apphost, DLL, deps/runtimeconfig files
and `native/` together under `Tools/FirmwareAdapter/`, or publish self-contained for the target runtime.
All native assets are built from pinned official libusb 1.0.30 and dfu-util 0.11 source archives.
The Windows builder uses pinned QMK-MSYS 1.12.0 / GCC 15.2.0 and verifies reproducible native
hashes against `build-dependencies.json`; the adapter also embeds those reviewed Windows hashes.
Linux x64 and macOS x64/arm64 builds embed a `NativeToolCatalog.json` for their exact runtime,
with the byte size and SHA-256 of both native files. A missing, changed or wrong-runtime
catalog blocks POSIX native access. Tools are loaded
only from the package's `native` directory, never from PATH or a system-installed fallback.
Keep upstream licenses with any distributed package. None of the synthetic checks demonstrates physical acceptance of the
thirteen experimental board models.

The portable coordinator also pins the complete adapter package before each invocation.
Linux HID runtime identity follows the exact HidSharp sysfs path to its USB parent. macOS
identity follows the exact HidSharp IOKit service to its USB ancestors, using the same
`PortNum`/`port` hierarchy and `locationID` bus namespace as libusb's Darwin backend.
Missing port identity, inaccessible interfaces and ambiguous devices stop the operation.
No automatic privilege escalation, kernel-driver detachment or protection unlock is used.

USB namespace references:
[libusb device handling](https://libusb.sourceforge.io/api-1.0/group__libusb__dev.html),
[libusb Darwin backend](https://github.com/libusb/libusb/blob/v1.0.30/libusb/os/darwin_usb.c),
[HidSharp macOS device paths](https://github.com/IntergatedCircuits/HidSharp/blob/master/HidSharp/Platform/MacOS/MacHidDevice.cs),
[HidSharp Linux device paths](https://github.com/IntergatedCircuits/HidSharp/blob/master/HidSharp/Platform/Linux/LinuxHidDevice.cs).
