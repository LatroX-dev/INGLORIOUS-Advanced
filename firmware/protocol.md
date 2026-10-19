# INGLORIOUS firmware protocol 1 / configuration schemas 1 through 7

This documents protocol 1 and configuration schemas 1–7, including the OS-switch extension introduced in firmware 0.11.9 and **board-16 hardware profiles in firmware 0.11.11** (build ID `0x000B0B01`), bundled with app 0.7.0. Physical acceptance covers independent profile changes, switching, reconnecting and app loading on one GMMK 3 PRO 100% ISO under Windows; further recovery and firmware-update checks remain open. Existing model/layout support is tracked separately in [the model overview](../docs/model-support.md). All multi-byte integers are little-endian. No packet enters a bootloader, resets the MCU, writes application firmware, changes protection, or exposes arbitrary memory. Command 8 explicitly schedules a USB re-enumeration to apply a saved keyboard polling interval. Hardware-profile activation has the separately bounded timing behavior documented below.

## Identity and transport

Development USB VID `0xFEED`, PRO PID `0x1C01`, Numpad PID `0x1C02`; these are private development identifiers, not a registered vendor allocation. Manufacturer `INGLORIOUS`, product `INGLORIOUS PRO ISO` / `INGLORIOUS Numpad`. Raw HID usage page `0xFF60`, usage `0x0061`, fixed 32-byte reports, report ID zero (Windows host buffer is 33 bytes including the leading zero). Require the identity and successful protocol INFO handshake; stock Glorious identities must never receive these packets.

Candidate0.10 adds board IDs3..9 and corresponding development PIDs1C03..1C09. The exact dimensions, products and capabilities are in [`targets.json`](targets.json). IDs3/4/5 are PRO rev1 ANSI, rev2 ANSI, rev2 ISO; IDs6/7/8/9 are GMMK2 65 ANSI,65 ISO,96 ANSI,96 ISO. All keyboards return lighting pattern1; only Numpad returns pattern2. The reserved encoder area must be zero on GMMK2; slider mode must be zero on all boards except Numpad. Runtime support is independent of stock recognition and firmware installation permission.

| Board | Schema1 bytes | Schema2/3/4/5 bytes | Schema3/4/5 macro events |
| --- | ---: | ---: | ---: |
| 1 | 5140 | 6360 | 668 |
| 2 | 3780 | 4184 | 702 |
| 3,4 | 5128 | 6336 | 669 |
| 5 | 5140 | 6360 | 668 |
| 6 | 4868 | 5944 | 674 |
| 7 | 4880 | 5968 | 674 |
| 8 | 5572 | 7032 | 658 |
| 9 | 5584 | 7056 | 658 |
| 10 | 5164 | 6216 | 675 |
| 11 | 5176 | 6240 | 675 |
| 12 | 5356 | 6600 | 667 |
| 13 | 5368 | 6624 | 667 |
| 14 | 5644 | 7176 | 655 |
| 15 | 5656 | 7200 | 655 |
| 16 | 5656 | 7200 | 655 |

Schema6 adds exactly8 bytes to the extended size above on encoder-equipped boards; schema7 adds another64 bytes after the order records. Board IDs6..9 have no encoder and reject schemas5/6/7. All older lengths, event capacities, slot locations and existing field offsets remain unchanged.

Candidate 0.11 adds GMMK 3 wired, non-HE board IDs 10..15 / PIDs 1C0A..1C0F in 65 ANSI, 65 ISO, 75 ANSI, 75 ISO, 100 ANSI, 100 ISO order. Each has an encoder and no slider. Their slot capacity is 7,648 payload bytes rather than the 7,136 bytes on IDs 1..9. Slots start at 1,024 and 8,704, with a 32-byte header. The second ends at 16,384. The 75/100 logo LED is an independent RGB LED, excluded from side pairing. It uses the existing per-LED color/effect fields without a wire-format extension.

Targets without the hardware-profile capability retain their wire/persistent layouts and slot addresses. Upload staging borrows the inactive slot payload in the existing16KiB RAM cache. BEGIN/WRITE_STAGE perform no backing writes. Ordinary EEPROM writes/erase/init are blocked while borrowed. ABORT replays the backing strictly read-only and verifies the original full cache hash. Only a semantically validated COMMIT can perform the ordered invalid-marker, forced-payload, header, valid-marker writes. The forced flush is necessary because staged bytes already equal the cache. Driver errors latch a fault. Consolidation retains the existing power-loss limitation; this is not a new atomicity guarantee. The board-16 bank instead uses the fixed A/B records described under [hardware profiles](#private-board-16-hardware-profiles-profile-pairs-v1).

Board 16 is the separate GMMK 3 PRO 100% ISO wired, non-HE target, PID `1C10`. It has the same configuration dimensions and slot sizes as board 15 but requires its own board identity. Its 256 KiB flash image preserves a 28 KiB resident-loader prefix; the application starts at `0x08007000`, the installation identity page at `0x08037F00`, and the 32 KiB settings region at `0x08038000`. These image-layout differences do not expose arbitrary memory through Raw HID.

These uppercase USB display names are present from release0.7 onward. Frozen release0.6 retains its original display strings and hashes. Device identifiers and protocol bytes are unchanged.

Common packet: bytes 0..1 ASCII `IG`, byte 2 protocol `1`, byte 3 command, byte 4 request sequence (echoed), byte 5 status (request must zero), byte 6 payload length 0..24, byte 7 zero, bytes 8..31 payload/padding (unused padding must zero). Responses have the same envelope. Status: 0 OK, 1 malformed, 2 range, 3 busy, 4 no upload, 5 CRC mismatch, 6 invalid config, 7 storage failure, 8 stale generation or ownership conflict, 9 unsupported command. Validation/range/CRC/generation failures precede persistence. A storage error or timeout can have an uncertain commit result; read the explicitly bound profile on bank firmware, or INFO and the active configuration on legacy firmware, instead of retrying blindly. A failed physical save disables further uploads for this boot; the bank's previous valid records remain available for readback, with storage health reported separately.

| Command | Request payload | Successful response payload |
| --- | --- | --- |
| `0x01 INFO` | empty | 24 bytes described below |
| `0x02 READ_ACTIVE` | offset u16, count u8 (1..20), reserved zero | offset u16, count u8, reserved zero, data[count] |
| `0x03 WRITE_STAGE` | offset u16, count u8 (1..20), reserved zero, data[count] | next expected offset u16 |
| `0x04 BEGIN` | total blob size u16, expected active generation u32, expected CRC32 u32 (10 bytes) | next expected offset u16 = 0 |
| `0x05 COMMIT` | expected CRC32 u32, expected active generation u32 (8 bytes) | new generation u32, active CRC32 u32 |
| `0x06 ABORT` | empty | empty |
| `0x08 APPLY_USB_INTERVAL` | empty on legacy firmware; 16-byte profile binding on bank-capable board16; idle device required | active interval byte, saved interval byte, queued byte(0/1), reserved zero |
| `0x09 READ_RUNTIME_BRIGHTNESS` | empty | brightness byte, active slider mode byte, two reserved zero bytes |
| `0x0A SET_RUNTIME_BRIGHTNESS` | one brightness byte0..255 | the exact accepted brightness byte |
| `0x0B GET_EXTENSIONS` | empty | version u16=1, flags u16(bit0=bounded manual light test, bit1=standby screensaver, bit2=board16 diagnostics, bit3=encoder presets/cycling, bit4=ordered encoder presets, bit5=read-only live knob status, bit6=per-function knob press, bit7=physical OS switch, bit8=board16 hardware profiles), durationMs u16=21000, reserved u16=0 |
| `0x0C LIGHT_TEST` | action byte: 0=status, 1=start, 2=stop | active byte0/1, pattern byte0=idle/1=keyboard/2=Numpad, remainingMs u16 |
| `0x0D GET_FIRMWARE_VERSION` | empty | 16-byte release identity described below; no state changes |
| `0x0E PHYSICAL_DIAGNOSTICS` | action byte: 0=status, 1=start/refresh, 2=stop, 3=refresh-only | 24 bytes described in board 16 diagnostics below |
| `0x0F DIAGNOSTIC_LEDS` | mode, LED index, R, G, B | echoed request, reserved zero, remainingMs u16; requires active session |
| `0x10 READ_KNOB_STATUS` | empty | 24 bytes described in live knob status below; encoder boards only |
| `0x11 READ_HARDWARE_CONTROLS` | empty | 8-byte OS-switch status described below |
| `0x12 PROFILE_STATUS` | query slot byte0=active or1..3 | 24-byte profile/OS status described below |
| `0x13 PROFILE_READ` | 16-byte slot, range and snapshot binding | echoed slot/range/generation plus up to12 data bytes |
| `0x14 PROFILE_BEGIN` | 24-byte explicit destination, expected/new snapshot and owner token | exact request echo |
| `0x15 PROFILE_WRITE` | 8-byte token/slot/range header plus1..16 data bytes | 8-byte token/slot/received count |
| `0x16 PROFILE_COMMIT` | 20-byte token/slot/generation/CRC binding | 16-byte verified slot/length/generation/CRC |
| `0x17 PROFILE_ABORT` | 8-byte token/slot binding | exact request echo |
| `0x18 PROFILE_INSTALLATION_IDENTITY` | empty | 20-byte format/board/installation GUID |

Commands decimal18–24 (`0x12`–`0x18`) require `GET_EXTENSIONS` bit8 and board16. The earlier private diagnostic build0.11.10 used decimal18 for an unrelated read-only matrix probe; a version number or board match alone is insufficient to select the hardware-profile protocol.

INFO payload: board byte0 (1 PRO, 2 Numpad), rows byte1, cols byte2, layers byte3=4, LEDs byte4, macro slots byte5=32, max events per macro byte6=128, flags byte7 (bit0 persistent storage available, bit1 slider present, bit2 staging active, bit3 schema2 effects supported, bit4 runtime timing and command8 supported, bit5 schema3 color sequences supported, bit6 volatile brightness commands9/10 supported, bit7 schema4 autonomous standby supported), **active** blob size u16 at8, **active** event capacity u16 at10 (768 in schema1/2; PRO668 or Numpad702 in schema3/4/5/6/7), active generation u32 at12, active CRC32 u32 at16, received staging bytes u16 at20, **active** config schema u16 at22 (1,2,3,4,5,6,7). Firmware0.6 sets bits3..7 even while preserving an active schema1 blob. Current firmware accepts each supported size at BEGIN; schema2/3/4/5 have the same length, schema6 adds8 bytes and schema7 adds72 bytes on encoder boards. The active size is not an upload-capacity restriction. An older host that rejects unknown capability bits must stop before writing; a newer host must check the relevant capabilities before uploading settings. In particular schema3 requires bit5 even if its stored color sequences are currently unused by the chosen effects.

`GET_FIRMWARE_VERSION`, added in release0.8.0, reports the firmware's own explicit release identity in normal USB mode. Its request is empty. The successful16-byte payload for release0.9.1 is: format u16 at0=`1`; board u8 at2=`1` PRO or`2` Numpad; flags u8 at3=`0` (reserved); major u16 at4=`0`; minor u16 at6=`9`; patch u16 at8=`1`; reserved u16 at10=`0`; build identifier u32 at12=`0x00090101`. Release0.8.0 retains minor8 and build`0x00080001`. The build identifier is an assigned release identifier, **not** a binary hash, signature or authenticity proof. Hosts validate format, board, flags, reserved fields and a nonzero build, then compare the three numeric version components. A valid unsupported-command status9 means an older firmware cannot report its version; a timeout or malformed answer means the query failed and must not be treated as a version. USB `bcdDevice`, INFO capabilities, active config schema and extension-protocol version are not release numbers.

RAW13 is read-only and available during a staged upload, running macro, held key, lighting test, screensaver, pending USB re-enumeration or storage fault. It does not reload or write persistent storage, change staging/generation/CRC, wake standby, affect a lighting test, send input, or schedule/reset anything. It shares INFO's initialization gate and ordinary packet/padding validation. INFO, GET_EXTENSIONS and configuration schemas1..4 retain their layouts; GET_EXTENSIONS reports the new screensaver capability and test duration. App 0.6.25 uses this explicit runtime report for version comparison; an unavailable report leaves the version unknown. Setup from identified original firmware remains separate. A confirmed firmware write still requires fresh matching full before-captures, exact approved code hashes and two complete post-write readbacks; an update preserves the 32 KiB legacy configuration backing byte-for-byte, plus the additional bank when the declared image layout contains hardware profiles.

Legacy commands3–6 use sequential uploads: first offset0, then next expected offset. An exact duplicate of already-received bytes is acknowledged idempotently; gaps, conflicting duplicates and out-of-range writes fail. BEGIN clears old staging; generation must match. COMMIT requires all bytes, matching IEEE CRC32 and semantic validation, no pressed matrix keys and no running macro. Generation mismatch aborts commit. On success select the verified persisted slot; ABORT drops staging. Empty/default configuration has generation0 and a computed CRC. Host reads the complete blob back after COMMIT and compares it byte-for-byte. Do not automatically retry an uncertain COMMIT: query INFO/readback first. Bank-capable board16 rejects commands3–6 and uses the explicitly owned uploads below; another BEGIN never replaces an existing owner.

CRC32 is reflected IEEE, polynomial `0xEDB88320`, initial value `0xFFFFFFFF`, final XOR `0xFFFFFFFF`, computed over the entire fixed-size config blob including zero padding.

## Config blob layout

Schema1: PRO rows11, cols8, LEDs99, bytes **5140**; Numpad rows5, cols4, LEDs31, bytes **3780**. Formula `32 + 4*rows*cols*2 + 4*2*2 + 4*leds*3 + 32*4 + 768*4`. Schema2 preserves every keymap/RGB/macro offset and appends `32 + 4*leds*3` bytes: PRO **6360**, Numpad **4184**. Schema3, schema4 and schema5 have exactly the schema2 length and effect offsets; it dedicates the end of the original macro-event area to color sequences, reducing event capacity as documented below. No firmware boot automatically rewrites a legacy configuration.

Header, 32 bytes:

| Offset | Value |
| --- | --- |
| 0..3 | ASCII `IGCF` |
| 4..5 | schema u16 =1,2,3,4,5,6,7, must match exact blob length |
| 6 | board1..16 from targets.json |
| 7 | layers =4 |
| 8,9,10 | rows, cols, LEDs |
| 11 | macro slots =32 |
| 12..13 | event capacity u16: schema1/2=768; schema3/4/5/6/7 PRO668/Numpad702 |
| 14..15 | used event count u16, 0..event capacity |
| 16 | slider mode: 0 disabled, 1 MIDI CC, 2 relative media volume, 3 lighting brightness |
| 17 | schema5/6/7: enabled encoder-cycle layers, bit0..3; schema7 additionally enables per-function press for layers0..3 in bit4..7. Encoder required. Older schemas: zero |
| 18..19 | slider calibration minimum u16 (default0) |
| 20..21 | slider calibration maximum u16 (default1023), strictly greater than minimum |
| 22 | MIDI channel 0..15 (default0) |
| 23 | MIDI controller0..127 (default62) |
| 24 | schema1 zero; schema2/3/4/5/6/7 USB keyboard polling interval in ms:1,2,4,8 |
| 25 | schema1 zero; schema2/3/4/5/6/7 debounce in ms:0..40 |
| 26 | schema4/5/6/7 standby enabled0/1; otherwise zero |
| 27..28 | schema4/5/6/7 standby timeout seconds u16:10..3600; otherwise zero |
| 29 | schema4/5/6/7 standby mode0=off,1=dim,2=screensaver; mode2 requires GET_EXTENSIONS bit1, even if byte26 is0; otherwise zero |
| 30 | schema4/5/6/7 dim brightness percent1..100; otherwise zero |
| 31 | schema5/6/7: Command instead of Ctrl for zoom/undo on cycling layers, bit0..3; must be a subset of byte17 low four bits. Older schemas: zero |

After header, in order:

1. Keymap `u16[4][rows][cols]`, row-major. Missing physical positions must be KC_NO0. Allowed basic QMK keycodes0..255, QMK modded basic codes0x0100..0x1FFF, TO(layer)0x5200..0x5203, MO(layer)0x5220..0x5223, TG(layer)0x5260..0x5263, macro0..31 as0x7700..0x771F, and from firmware0.11.5 brightness up0x7849/down0x784A. Encoder-equipped boards also accept shortcut codes0x7E42..0x7E4B from0.11.6. KC_TRNS1 falls through. Other QMK functions, boot/reset codes and arbitrary custom actions are rejected.
2. Encoder `u16[4][2]`, index0 counterclockwise, index1 clockwise. Same keycode vocabulary; momentary layers are rejected for encoder impulses. Encoder press is an ordinary matrix key.
3. Per-layer LED RGB bytes `[4][leds][3]` in QMK LED index order, R/G/B. Includes side lights; no missing-LED placeholders. Configured values are raw0..255, multiplied by a runtime brightness scale. No host animation stream is required.
4. Macro descriptors `[32]`, each start-event u16 and event-count u16. Each count <=128; ranges must lie within used event count. Empty descriptors have start0,count0. Sharing immutable event ranges is allowed.
5. Event pool `[768]`, each type byte, reserved byte0, value u16. Types1 key-down,2 key-up,3 delay-ms. Key values must be basic HID keyboard usages4..0xA4 or modifiers0xE0..0xE7; delay0..60000ms. Each macro must have balanced down/up events, no duplicate down/unmatched up, total delay<=300000ms. Unused events are all zero. Macro triggers do not recurse. One macro runs at a time, without blocking matrix scanning; a second macro trigger while running is ignored. Any new ordinary key press (including Escape) cancels and releases macro-held keys before the physical input is processed. A macro starts only without other host-reporting physical keys held; encoder-triggered macros require no host-reporting matrix keys held. Firmware revision 0.2 permits already-held MO(0..3) layer switches for both triggers; actual modifiers and normal keys still block start. The temporary layer-switch mask is cleared on release, non-layer remapping at that position, restart and suspend. Revision 0.1 blocked Fn-triggered macros too; the stored format is unchanged.

Slider mode2 sends bounded relative consumer volume steps after physical movement; it cannot determine the OS absolute volume, and startup position sends no volume changes. MIDI requires a MIDI-aware receiver. Brightness mode changes autonomous lighting live without continual EEPROM writes. Slider configuration must use mode0 on PRO.

## Board 16 physical diagnostics (0.11.3)

GET_EXTENSIONS format 1 adds flag `0x04` only for board 16. Release0.11.6 reports combined flags15 for board16,11 for other encoder boards and3 for encoderless boards. Release0.11.7 also advertises ordered presets with flag `0x10`: combined flags31,27 and3 respectively. Hosts must check the diagnostic flag before using commands14 or15.

RAW14 takes one action byte: 0 reads status, 1 starts or refreshes an active session, 2 stops, and 3 refreshes only an already-active session. Action 3 returns status 8 (conflict) if the session expired, stopped or was suspended; it never restarts or clears observations to satisfy a late poll. Hosts use action 1 only for explicit user start and action 3 for periodic refresh. The successful 24-byte payload is format 1 at byte 0, active boolean at 1, matrix rows 14 at 2, columns 8 at 3, fourteen cumulative row bitmasks at 4..17, clockwise counter u16 at 18, counterclockwise counter u16 at 20, and watchdog milliseconds remaining u16 at 22. Row bits latch physical pressed positions since session start, exclude matrix holes, and survive release, remapping and refresh. Encoder counters count pressed encoder events before keycode processing, including unmapped bindings, and wrap modulo 65536. Stop, expiry and suspend clear the session observations.

RAW15 takes five bytes: mode at 0 (0 clears the overlay, 1 colors all LEDs, 2 colors one LED), LED index at 1 (zero unless mode 2), and sRGB R/G/B at 2..4 (all zero for mode 0). The successful eight-byte response echoes those five bytes, reserves byte 5 as zero and reports remaining milliseconds at 6. One-LED mode makes all other LEDs black. Colors pass through the normal sRGB-to-PWM conversion once and temporarily override the saved brightness/effect. Clearing the overlay restores normal rendering without stopping the session. RAW15 requires an active session and never refreshes its deadline.

The session lasts at most 10000 ms without RAW14 action 1 or 3, using wrap-safe elapsed time. Housekeeping, rendering and status enforce expiry; USB suspend and explicit stop also restore normal profile rendering. A new session rejects held keys, staged uploads, running macros, an existing manual lighting test, pending USB re-enumeration or suspend. BEGIN, COMMIT, reconnect, volatile brightness and starting the old manual test return busy while active. Configuration reads and identity queries remain available. No diagnostic action writes configuration storage. Keys and the encoder retain their normal operating-system behavior; the app must explain that limitation. A lost host connection while the keyboard stays powered is bounded by the watchdog.

## Schema2 lighting extension

At the schema1 end offset, four 8-byte global effect records appear in layer order. Each contains mode0..20, speed0..255, brightness0..255, target1=keys/2=sides/3=all, flags (bit0 reverse, bit1 synchronized side coordinates), and three reserved zero bytes. In schema3/4/5/6/7, record byte5 is a color-sequence ID0..8. Bytes6/7 remain zero in schema3/4; schema5/6/7 uses them for the knob selection mask and packed step/undo layout described below. Default record: `00 80 FF 03 00 00 00 00`.

After these32 bytes are per-LED overrides `[4][leds][3]`, layer-major in the same physical LED order as RGB. An inherited record is exactly `FF 00 00`. Otherwise byte0 contains mode0..20 in bits0..4 and reverse in bit7; bits5/6 must be zero. Byte1 is speed, byte2 brightness. An explicit override applies regardless of the global target; its side-sync geometry flag comes from the global layer record. All LEDs including side strips can be configured independently. The host copies paired side colors/overrides when the user enables mirroring; firmware's side-sync flag aligns effect phase by mapping right-side X to the corresponding left-side X, without silently copying colors.

| ID | Effect | Color source | Direction |
| --- | --- | --- | --- |
| 0 | Static | stored per-LED RGB | — |
| 1 | Breathing | stored RGB | — |
| 2 | Hue cycle | generated rainbow hue; schema3 optionally uses a stored color sequence | — |
| 3/4 | Rainbow X/Y | generated rainbow hue | yes |
| 5/6 | Brightness wave X/Y | stored RGB | yes |
| 7 | Scanner, horizontal on keys/vertical on sides | stored RGB | yes |
| 8 | Twinkle | stored RGB | — |
| 9 | Key reaction, fading linearly | stored RGB | — |
| 10 | Expanding ripple | stored RGB | — |
| 11 | Expanding rainbow ripple | generated rainbow hue | — |
| 12 | Colored rain | generated rainbow hue | yes |
| 13 | Static rainbow gradient | generated rainbow hue | yes |
| 14 | Glitter over a dim base | stored RGB | — |
| 15 | Soft quadratic afterglow with nearby LEDs | stored RGB | — |
| 16 | Moving snake segment | stored RGB | yes |
| 17 | Moving checkerboard | stored RGB | yes |
| 18 | Double pulse | stored RGB | — |
| 19 | Fire | generated red/orange | — |
| 20 | Cross wave from a key press | stored RGB | — |

Effects execute locally without host streaming or dynamic allocation. Speed maps to a period from10000ms (0) to400ms (255), or reactive lifetime4000..400ms. Brightness scales effect output; inherited LEDs outside the target retain stored RGB. The Numpad runtime brightness slider scales the final result, including those outside a target. Static gradient ignores speed. Reverse is meaningful only for the modes listed; setting it on another mode does not introduce a second behavior. Reactive modes retain the newest eight physical presses with an associated LED; releases and encoder impulses do not add hits. Mode9 also pulses side LEDs on any key press. Ripple distances use integer octagonal approximation and cross waves use distance to the nearest axis. Suspend and a successful config commit clear hit history. Rendering uses one timestamp across QMK's partial LED-frame batches.

### Stored configuration compatibility

Firmware0.4 validates both legacy and extended slots, including each slot's own length and CRC. The `IGS1` slot-header format remains version1; the stored blob itself identifies schema1/2. Startup does not rewrite a schema1 slot, increment its generation or change its CRC. An explicit schema2 upload commits to the other slot. A valid schema1 upload may also replace active schema2 deliberately. Semantic failure happens before any storage call; a failed storage operation retains the existing fault/reload rules. Header length/schema mismatches and noncanonical inherited overrides are rejected.

Factory defaults remain schema1. A host can use schema1 when all effects inherit static defaults and device timing remains1ms/5ms; changing either requires schema2. Merely editing an app project must not imply firmware deployment or a device write.

## Schema3 color sequences (firmware0.5)

Each global effect and explicit per-LED override can retain an ordered sequence of1..8 RGB colors. Empty means the previous generated hue behavior. Only mode2 renders these colors; switching to another mode preserves them without changing that mode's output. Repeated colors inside a sequence are permitted. The entire configuration holds up to eight distinct sequences, shared across all four layers. Colors are compared as RGB bytes; order and repetition matter. Exceeding the color, unique-sequence or macro-event limits is an error, never truncation.

Let `L` be the board's LED count and `V1Size` the schema1 size. Reserve `R = (200 + 2*L + 3) & ~3` bytes at `P = V1Size - R`. PRO has R400, P4740 and668 macro events; Numpad has R264, P3516 and702 events. Header12 must report that exact capacity. Event storage ends at P; unused event bytes before P are zero. Schemas2–5 use6360/4184 bytes, unchanged from schema2; schema6 appends8 bytes and schema7 appends72 bytes while retaining the same palette and macro-event capacity. No additional RAM-resident palette or active-config blob is allocated.

At P is an eight-entry pool. Each25-byte entry contains a color count0..8 followed by eight RGB triples. Bytes beyond count are zero. Empty entries are all zero and follow all nonempty entries; duplicate entries, holes and unreferenced nonempty entries are invalid. ID0 means no custom sequence; IDs1..8 select pool entries0..7.

At P+200, packed4-bit IDs cover all LEDs on all four layers in layer-major order. Even flattened indices use the low nibble, odd indices the high nibble. An inherited `FF 00 00` LED effect must have ID0 and obtains the global sequence. An explicit override with ID0 retains the legacy generated colors, independently of any global sequence. Remaining bytes up to V1Size are zero. Global IDs live at effect-record byte5, as above. Pool IDs follow canonical first occurrence: for layer0..3 visit its global ID and then each LED ID0..L-1; the next unseen nonzero ID must be1, then2, and so on. This makes serializer/decode/re-encode byte-exact.

Mode2 uses the existing period `10000 - speed*9600/255` ms. `phase = (time % period)*256/period`; multiply phase by color count, select index from the high byte, and interpolate to the next entry (wrapping last to first) using the low byte as weight. Each component is `(a*(256-weight)+b*weight)>>8`. One color remains constant. Effect brightness and the runtime dimmer are then applied using the same integer scaling as the other modes. The desktop preview uses the same arithmetic and is checked against the actual C helper.

Firmware0.5 reads valid schema1/2 slots without changing their bytes, generation or CRC. Schema3 is written only by an explicit upload whose host checks INFO bit5. Older firmware cannot accept schema3. Downgrading firmware while a schema3 slot remains active is not a compatible configuration migration; first explicitly save a supported legacy configuration if such a downgrade is intended.

## Volatile brightness (firmware0.5)

INFO bit6 gates commands9/10. Command9 only reads the existing brightness scale and current slider mode; it does not sample ADC, write EEPROM or change generation. PRO always reports slider mode0. Command10 changes only the runtime0..255 scale and echoes it. It rejects a pending upload, USB re-enumeration or active manual lighting test with status3; key presses and the automatic screensaver do not block it. During screensaver output the new value is retained for normal lighting, without scaling the overlay. Invalid lengths, reserved bytes and nonzero padding are rejected before the assignment. These commands never persist configuration, allocate storage, change timing, reset the MCU or enter a bootloader. Brightness starts at255 after a cold power cycle; autonomous Numpad brightness mode then uses the physical slider position.

An optional desktop relay can read Numpad brightness and set PRO brightness only when it changes. That relay requires the desktop app to remain running. Local Numpad dimming and all stored lighting continue without the app; there is no direct USB device-to-device link. A timeout must not be treated as a confirmed set, and these calls do not use the persistent COMMIT API.

Firmware 0.11.5 also accepts direct key and encoder bindings `RM_VALU` (`0x7849`) and `RM_VALD` (`0x784A`). Hosts must verify `GET_FIRMWARE_VERSION` is at least 0.11.5 before uploading a configuration containing either code; the protocol and configuration schemas are unchanged. Each press raises or lowers the same runtime dimmer by 17, saturated at 0 and 255. Releases are consumed without a second step, and modifiers do not reverse the direction. The runtime consumes both events before QMK's RGB processing, so these actions never schedule an EEPROM write. Bindings persist with the profile; the resulting dimmer level does not. Physical activity retains the existing standby wake, light-test cancellation and macro-release behavior. A later movement of the Numpad brightness slider replaces the current dimmer value with its physical position.

## Encoder presets and long press (0.11.6)

`GET_EXTENSIONS` bit3 advertises this feature on encoder-equipped boards. Hosts must also verify release0.11.6 or later before uploading schema5 or custom shortcut bindings. Schema5 has the same length, offsets, event capacity, color-sequence rules and standby fields as schema4. Header byte17 enables cycling per layer and byte31 selects Mac zoom/undo on those cycling layers. Byte17 must be0..15; byte31 must be a subset of it. Encoderless boards reject schema5. Firmware boot never rewrites an old profile.

Each layer's8-byte global effect record repurposes its final two reserved bytes in schema5. Offset6 is a nonempty preset selection mask: bit0 volume, bit1 brightness, bit2 tracks, bit3 vertical scroll, bit4 zoom, bit5 horizontal scroll, bit6 tabs, bit7 undo/redo. Default selection is31, the original five modes. Offset7 packs the multiplier1..5 in bits0..2 and the undo key layout in bits3..4:0 QWERTY,1 QWERTZ,2 AZERTY. Layout3 and bits5..7 are rejected. The default packed byte is1. Schema1..4 require both bytes zero. Hosts select schema5 when any cycle is enabled or any mask/step/layout differs from its default; firmware rejects a redundant schema5 configuration with none of these changes.

Each layer has a volatile current mode. A700ms hold of the physical knob advances through the selected modes in the mask order above. A saved pair matching a selected preset appears only once; a saved custom pair remains an additional choice. An unselected preset is skipped after the initial saved mode. Boot or profile commit restores the saved pair. Mode changes and rotations never write EEPROM/flash and do not change readback bytes. Clockwise/right increases volume/brightness, selects next track/tab, scrolls down/right, zooms in or performs redo; counterclockwise/left does the reverse. Scroll uses QMK mouse-key codes `MS_WHLU=0x00D9`, `MS_WHLD=0x00DA`, `MS_WHLL=0x00DB` and `MS_WHLR=0x00DC`; the generated overlay verifies mouse-key support in each pinned encoder board definition.

Zoom codes are `IG_ZOOM_IN=0x7E42`, `IG_ZOOM_OUT=0x7E43`, `IG_ZOOM_IN_MAC=0x7E44`, `IG_ZOOM_OUT_MAC=0x7E45`. The first pair sends Ctrl with keypad plus/minus, the second sends GUI/Command with keypad plus/minus. Keypad codes avoid punctuation-layout dependence. Tabs use `IG_TAB_PREVIOUS=0x7E46` for Ctrl+Shift+Tab and `IG_TAB_NEXT=0x7E47` for Ctrl+Tab. Undo/redo use `IG_UNDO=0x7E48` and `IG_REDO=0x7E49` for Ctrl+Z/Ctrl+Shift+Z, or `IG_UNDO_MAC=0x7E4A` and `IG_REDO_MAC=0x7E4B` for the corresponding GUI/Command shortcuts. The chosen host layout translates Z to HID KC_Z, KC_Y or KC_W. Each operation retains existing real modifiers and restores existing weak modifiers exactly; if its target key is already held, it sends nothing rather than releasing someone else's key. Key-up never repeats the shortcut. Application support for these shortcuts is host-dependent.

The multiplier applies only to real encoder events whose complete current direction pair is a recognized volume, brightness, vertical/horizontal wheel or zoom preset. Transparent directions resolve through the active/default layer stack, with QMK's layer-zero fallback; the highest active layer selects the multiplier. Tracks, tabs, undo/redo, ordinary physical key assignments and custom mixed direction pairs always use one action. The maximum multiplier is5; brightness saturates at0/255. Mouse and volume repeats use the existing QMK report path. No extra persistent state or host timer is used. An inherited undo binding uses the undo layout of the layer that supplied the action, including physical keys and deferred knob taps; a runtime cycle mode uses its owning layer.

Cycling changes neither the saved press binding nor its configured direction pair. A short press emits the original action on release; a long press changes mode once and suppresses that short action. Enabled layers require a press code below0xE0, a macro, brightness or supported shortcut action. Modifier-only, modded/chord and TO/MO/TG layer bindings are rejected. For a transparent press every potentially active lower layer is checked, including layers that could be skipped in the current stack; the base press must provide a nontransparent fallback. Macro taps use the existing bounded runner and overlap checks.

The active highest layer owns the gesture. A layer change, another physical key, suspension or an upload/reconnect/diagnostic operation cancels a pending gesture without emitting a late action. An already-held external MO/Fn key is allowed when its layer remains active. During an existing diagnostic/upload/reconnect operation, or when another ordinary key or modifier was already held, knob events retain their ordinary QMK behavior; they do not start a deferred gesture. The physical knob position is generated from pinned board geometry and cannot be changed by remapping.

## Ordered encoder presets (0.11.7)

Schema6 preserves the entire schema5 prefix and appends four little-endian `u16` permutation ranks, one per layer, at `Schema5Size + layer * 2`. Its exact payload length is `Schema5Size + 8`. It does not move the effect records, per-LED overrides, color palette, standby settings or macro events. Hosts require both a fresh firmware version of at least0.11.7 and `GET_EXTENSIONS` bit4 before uploading schema6. Encoderless boards reject this schema.

Each rank is a standard lexicographic Lehmer rank in0..40319 for the eight preset bits `[1,2,4,8,16,32,64,128]`. Rank0 is that default order; rank40319 is its reverse. Decode using factorial divisors `[5040,720,120,24,6,2,1,1]`, selecting and removing the indexed remaining item at each position. Ranks outside the range are rejected before persistence. At least one layer must have a nonzero rank; otherwise the host uses the appropriate older schema and firmware rejects redundant schema6. A custom order may be saved while cycling is disabled, without requiring another nondefault knob setting.

The existing per-layer selection mask filters this complete permutation; unchecked modes are skipped without losing their relative positions. The current saved preset remains the initial mode. A custom saved direction pair occupies one additional slot before/after the ordered presets. Holding the knob advances to the next selected mode in that layer's order, and releasing after a long press still sends no short action. Reboot or profile transfer resets the volatile mode to the saved pair while retaining the stored order. Existing schema1–5 profiles keep their original sequence and are never rewritten on boot.

## Live knob state and feedback (0.11.8)

Encoder targets add extension bits5 (`0x20`, live status) and6 (`0x40`, per-function press). Combined flags are127 on board16,123 on other encoder targets and3 on encoderless targets. Bit5 requires the earlier encoder/order flags; bit6 additionally requires bit5. Hosts check a fresh release identity of at least0.11.8 and the relevant extension before using these features. Unsupported targets return status9 for RAW16; the host must not infer a live mode from an editor profile.

RAW16 returns24 bytes: format1 at0; actual highest active layer at1; recognized preset bit at2 (zero for a custom pair); flags at3 (bit0 cycling enabled, bit1 visual signal active); resolved counterclockwise and clockwise keycodes as u16 at4/6; long-press change counter u32 at8; active persisted generation/CRC u32 at12/16; source layers for those two bindings at20/21; effective encoder multiplier1..5 at22; reserved0 at23. Preset bits use the schema5 selection-mask vocabulary. Mac and Ctrl variants share the zoom/undo bit, while the actual keycodes distinguish them. Transparent mappings resolve through the currently active/default layer stack. Custom pairs report multiplier1.

The counter advances only when a completed long hold changes the actual direction pair. A single selected mode that resolves to the same pair produces no change event. Layer changes, short presses, reads and configuration transfer do not increment it. Reboot resets the counter; configuration transfer resets the temporary mode but retains the counter. Reading does not send input, wake standby or write storage. Generation and CRC identify the persisted configuration separately from the live function.

A real long-press function change overlays a600-ms pulse on normal lighting:100-ms fade-in and500-ms fade-out. RGB signals are white for custom, cyan for volume, yellow for brightness, purple for tracks, blue for vertical scroll, green for zoom, indigo for horizontal scroll, orange for tabs and pink for undo/redo. The existing runtime dimmer also scales the pulse, so a zero brightness remains dark. The pulse never changes saved colors or effects. Uploads, diagnostics, suspend, lighting tests, standby and layer changes suppress/cancel it; existing test output retains priority. A partial RGB frame whose timestamp predates the switch cannot prematurely expire the new signal.

## Per-function short press (schema7, firmware0.11.8)

Schema7 retains the complete schema6 prefix and appends64 bytes: four layer records, each eight little-endian u16 keycodes in fixed preset-bit order `[1,2,4,8,16,32,64,128]`. Its size is `Schema5Size +72`. Header17 bits4..7 enable per-function press for the corresponding layers; disabled tables are preserved. A table value1 (`KC_TRNS`) means inherit the ordinary physical knob press, while0 (`KC_NO`) disables that function's press. Missing app dictionary entries encode as1. Schema7 is required exactly when a mode is enabled or at least one table value differs from1; otherwise the host selects an older canonical schema. Unlike schema6, all order ranks may be zero in schema7.

Explicit entries permit bounded taps only: ordinary nonmodifier keys, consumer actions, wheels, supported brightness/shortcut actions and existing nonempty macros. Modifier holds, chords and layer-switch actions are rejected. Inherited general bindings retain their original QMK down/up behavior when cycling is disabled, including modifier/Fn holds. The existing stricter cycle-safety checks still apply when cycling is enabled. Every table is validated even when disabled; Mac flags remain a subset of only the cycle-enable low nibble.

The actual resolved direction pair selects the function entry at physical key-down. Both OS variants of zoom or undo use their common function entry, even when one is an extra saved-mode slot. An unrecognized custom pair falls back to general press. The selected action and source layer are latched until release, so rotating or changing layers cannot replace the pending short action. A long cycle suppresses the short action. Suspension and conflicting physical input cancel pending taps; no late release action is emitted. An ordinary basic key already held elsewhere is not tapped or released by an override. These mappings persist on the keyboard and require no app process.

## Schema4 autonomous lighting standby (firmware0.6; screensaver added in0.9)

INFO flag bit7 (`0x80`) advertises standby, in addition to all earlier feature capabilities. Schema4 has exactly the schema2/3 size and the same key/effect/sequence offsets. It reserves the same sequence pool and uses the same reduced macro capacity as schema3, even when its palette is empty. A canonical entirely empty pool and zero references are valid in schema4; schema3 continues to require a used color sequence. All timeout/mode/brightness fields are validated even when standby is disabled. Modes are0 off,1 dim and2 screensaver. Dim brightness remains1..100 and is preserved but unused by modes0/2. App defaults are disabled,300 seconds,mode0 off,10% dim brightness. The host selects schema4 only when these standby settings differ from the defaults; otherwise it can still emit a legacy schema. Uploads require INFO bit7 before BEGIN. Mode2 additionally requires a fresh GET_EXTENSIONS response with bit1 (`0x02`) before BEGIN, even when standby is disabled. Firmware0.8 supports schema4 but rejects mode2; no mode substitution is allowed. Existing schema1/2/3 slots are not rewritten on startup and have idle standby disabled.

Activity is local to each device. An enabled device turns off, dims every key and side LED, or starts the screensaver after its configured interval without a physical key/encoder event or meaningful slider movement. Press and release both restart the interval; held matrix keys keep the device awake. The wake event retains its normal key/encoder action. Since 0.9.1, the slider takes the median of three raw samples before applying its calibration. Three real samples initialize the action and wake baselines without reporting movement. Actions retain the two-step deadband; waking requires a separate three-step displacement confirmed in the same direction by two consecutive filtered samples. Mode/configuration/reboot changes restart initialization. Isolated spikes and small jitter do not count as activity. Even a slider with its assigned action disabled can wake lighting. No app, host activity stream or flash write is required. A key press on the PRO cannot autonomously wake the separate Numpad, or vice versa.

Idle brightness is a temporary final scale over existing effect and runtime brightness. For dim mode the runtime scale becomes `runtimeBrightness * percent / 100`; off mode uses zero. A full RGB frame uses one scale snapshot. The stored profile, current layer, macro state and RAW9/10 dimmer value remain unchanged; INFO/READ/brightness-relay traffic is not physical activity and cannot keep lighting awake. An idle latch survives subsequent32-bit timer wraps. Physical activity, USB resume and a configuration reload restart the inactivity interval. Effects continue with their existing animation clock; wake does not reset their phase.

Mode2 renders the complete21-second sequence described below, repeating from its white opening until physical activity resumes. Its phase is `(frameTime - lastActivity - timeoutMs) % 21000`. It uses the same pixel renderer as the explicit manual test, including side LEDs, and bypasses stored effect/idle/runtime brightness for visibility. It does not set the manual-test active flag or allocate a new timer/buffer. Consequently BEGIN/COMMIT, RAW8 and RAW10 remain available during an automatic screensaver. A successful configuration reload resets inactivity; RAW10 alone stores a runtime value for use after the overlay without waking the device. No persistent setting is changed by starting, looping or waking the screensaver.

USB suspend always switches lighting off on both boards, independently of standby settings, using QMK `RGB_MATRIX_SLEEP` plus the user-hook output guard. Resume restores normal brightness and restarts the idle interval. This is distinct from waking a sleeping computer: existing QMK/host USB remote-wakeup behavior is unchanged; slider or encoder actions are not promised to wake a suspended OS. LED off here is not a claim of deep MCU sleep or measured suspend current. Hardware acceptance remains outstanding.

## Manual lighting test and shared screensaver sequence (firmware0.9)

All original INFO flag bits are occupied, so command11 supplies a separate, versioned extension record without changing INFO or any stored configuration layout. Version1 flags are bit0 (`0x01`) bounded manual test and bit1 (`0x02`) standby screensaver. Release0.9 reports flags3, duration21000 ms and reserved0. Hosts reject unknown flag bits and accept a manual-test duration only in1..60000 ms; without bit0 the duration must be0. Hosts discover the bound before start and reject status remaining time beyond that bound. Release0.8 still reports flags1 and4250 ms and remains valid for its manual test, but cannot accept mode2 standby. A pre-extension firmware returns unsupported-command status9 for command11; this never authorizes substitute configuration writes or dimmer commands.

An explicit command12 action1 starts one21-second run. Screensaver mode2 repeats the identical sequence autonomously. Both use physical matrix/LED coordinates independently of remapping or layer:

| Elapsed milliseconds | Output |
| --- | --- |
| 0..1999 | All physical LEDs, including sides, white RGB(160,160,160) |
| 2000..2499 | All LEDs dark |
| 2500..12499 | Ten1000-ms steps: PRO I,N,G,L,O,R,I,O,U,S; Numpad1,2,3,4,5,6,7,8,9,0. Each step is700 ms RGB(160,0,0) on that physical key, then300 ms dark; other LEDs remain dark. |
| 12500..18999 | Red drops fall from the top in staggered physical columns; after the first1000 ms of this phase, a red pool rises from the bottom and reaches the top before the phase ends. Key and side coordinates use the same renderer. |
| 19000..20999 | All physical LEDs red RGB(160,0,0) |

The output overlay bypasses stored black colors, effect brightness, idle dimming and the volatile dimmer for visibility, but changes none of those values. USB suspend always wins: it cancels a manual test and suppresses the screensaver. The ordinary effect clock continues. A completed/cancelled manual test returns to the configured lighting state; an idle configured screensaver can resume. Physical input clears the idle latch and returns to normal lighting. The historical0.7/0.8 manual pattern remains4250 ms: ten250-ms red/100-ms dark steps, Numpad7,8,9,6,3,2,1,4,5,0, followed by750 ms all red. Frozen releases are not changed.

The firmware ends the manual overlay after21000 ms using unsigned timer differences, including across timer wrap. Housekeeping enforces this without host traffic or an active RGB frame. Start while already active returns busy and cannot extend the deadline. Start also rejects active upload, macro, held physical key, pending USB re-enumeration and USB suspend. BEGIN, COMMIT, command8 and command10 return busy while the manual test is active; the automatic screensaver does not impose those locks. INFO, READ_ACTIVE, command9, extension discovery, test status and idempotent stop remain available. Malformed lengths/actions/padding fail before test-state changes.

Action0 reads manual-test status; action2 cancels that test immediately and is safe when already idle. The screensaver is not reported as an active manual test, and action2 does not change its saved enable/mode. Active responses have pattern1/2 matching the board and remainingMs1..21000 on0.9; inactive responses have all four bytes zero. Physical key/encoder activity cancels the manual test and wakes the screensaver while retaining the input's normal action. Since 0.9.1, slider samples wake standby and drive the configured slider action without cancelling a manual test; an ADC fluctuation cannot terminate its 21-second sequence. A host can observe an early manual stop but cannot infer its cause. A successful protocol exchange confirms test state, not visible light output or functioning switches. No test command emits key/media/MIDI/macro actions, changes active configuration bytes/generation/CRC, writes/reloads EEPROM, changes USB timing or enters a bootloader.

## Device timing

Schema1 uses the existing1ms USB keyboard interval and5ms global deferred debounce. Schema2 header24 selects1/2/4/8ms (1000/500/250/125Hz advertised USB keyboard polling); header25 selects0..40ms deferred debounce. These are configuration values, not a measurement or guarantee of end-to-end input latency. Debounce0 bypasses settling; all other values use QMK's global deferred policy with a runtime delay.

On legacy-layout firmware, boot loads and validates the active slot in the early keyboard hook before USB descriptor initialization. A verified COMMIT applies debounce and records the desired interval, but does **not** disconnect USB. The host must finish its complete byte-for-byte readback before explicitly requesting command8. Command8 rejects a storage fault, active upload, running macro or held key. It acknowledges current/saved intervals before scheduling any action. If different, the device waits at least100ms and until no key/macro/upload is active and USB is not suspended, then clears keyboard reports and restarts its USB interface. If already equal, it is a no-op. A later verified config reload cancels an earlier queued reconnect. RAW-HID/MIDI endpoint intervals are unchanged. The saved interval also applies on the next physical reconnect; a USB restart never jumps to a bootloader or modifies EEPROM. Bank-capable board16 requires the full profile binding for command8 and distinguishes a physical profile activation from a COMMIT refresh, as documented below.

## Persistence and safety scope

Legacy-layout targets use QMK embedded-flash wear levelling, **not external SPI EEPROM**. The final32KiB physical flash is reserved by the linker, with a16KiB logical EEPROM shadow. The first1024 logical bytes remain QMK-owned. Boards1..9 use two7168-byte slots at1024 and8192; boards10..16 use two7680-byte slots at1024 and8704. Each has a32-byte header recording magic/schema/board/length/generation/CRC and a final valid marker. Validate and read back before selecting a new slot. At startup choose the newest valid CRC slot; otherwise compile-time defaults. Staging borrows the inactive slot's payload area in the existing cache rather than allocating another maximum-size buffer. The hardware-profile layout leaves this entire legacy backing immutable and adds the separate bank below.

The two logical slots detect partial writes; they do not yet prove atomic power-loss survival across QMK's physical wear-level consolidation. All persistence calls check `wear_leveling_write` status directly, because QMK's EEPROM wrapper discards it. Commit then reloads using `wear_leveling_init` and validates the actual backing data/selected slot; this replay can itself consolidate and is not described as a pure physical read. Firmware must not advertise tested power-loss recovery until actual controlled tests pass. External stock profile storage is not touched by this driver; the Numpad external flash CS B6 is held inactive. MCU ROM bootloaders stay available. Targets whose application replaces stock in-flash updater code retain it in captured original images; board16 instead preserves its resident-loader prefix. No flashing is performed by the configuration protocol.

## Fixed physical recovery gesture

Hold **Space+B on PRO ISO**, or **Plus+Enter on the Numpad**, while connecting USB. Keep both held for approximately three seconds. Exactly these two physical switches must be down; any third switch or release cancels entry. The check runs only once during startup, after matrix initialization: 50 ms debounce (covering all supported0..40ms settings), followed by500 ms of sustained exclusive holding. The upstream Numpad additionally waits two seconds while enabling its lighting hardware. Physical coordinates are PRO `(9,4)+(4,5)`, Numpad `(1,3)+(3,3)`, so remapping and corrupt configuration do not remove the gesture.

The gesture invokes the upstream QMK ROM-bootloader jump without clearing configuration. It is unavailable through RAW HID or configurable keycodes. Native mocked tests can verify gesture timing/selection; successful actual ROM entry on flashed hardware remains a separate acceptance check. The MCU hardware boot control remains the fallback if application startup itself fails.

## Physical OS switch (firmware 0.11.9)

`GET_EXTENSIONS` bit7 (`0x80`) advertises an independent Windows/macOS switch on the seven explicitly mapped wired GMMK 3 targets (board IDs10..16). It does not advertise three hardware profiles or wireless connectivity. Hosts require firmware0.11.9 or later and the capability before querying; older or unlisted devices are not probed.

`RAW17 READ_HARDWARE_CONTROLS` accepts an empty payload only. Its eight-byte response is `[format=1, boardId, capabilities=1, requestedMac, effectiveMac, pending, 0, 0]`. Boolean fields are0/1. Without pending, requested and effective modes match. Unsupported boards return status9. This reports fixed, declared controls only; it does not accept addresses, change a pin, persist settings or select a profile.

The Windows app reads this state through a low-frequency monitor shared and serialized with knob-status polling, as well as during user-started device tests. Foreground configuration reads, transfers, tests and firmware operations pause the monitor and drain its outstanding request before opening another operation. Bank-capable board16 returns the OS and profile positions together in RAW18 instead of requiring separate parallel pollers. Temporary busy replies retain the last confirmed display; a confirmed disconnect or device change clears it. The firmware works without the app. Input debouncing and deferring effective changes until held keys and other active input have finished avoid mismatched modifier releases. Mapping is derived from the pinned manufacturer Windows/macOS base layouts, limited to unchanged plain bindings at those physical locations. Custom key bindings, macros, modifier chords, profile bytes and all four user layers remain authoritative.

<a id="private-board-16-hardware-profiles-profile-pairs-v1"></a>

## Board 16 hardware profiles (`profile-pairs-v1`)

This extension is enabled only for the wired, non-HE **GMMK 3 PRO 100% ISO**, board16, with `GET_EXTENSIONS` flag `0x0100`. The other GMMK3 sizes, layouts and boards do not inherit its selector mapping or flash geometry. The selector has been observed at row11/column7 for profile1, row12/column7 for profile2 and row13/column7 for profile3. Exactly one contact must be active for30ms. Zero, multiple or changing contacts retain the last active profile; they never select profile1 by default. Before the first stable valid selection, active profile0 means unknown.

Each hardware profile is a complete existing IGCF payload, including its own four user layers, bindings, lighting, macros, knob settings and timing. Profiles are not aliases for layers. The host profile library remains a separate collection of user files. Reads and physical switches do not save or initialize profiles.

### Persistent records and recovery

All addresses here are flash-relative offsets from `0x08000000`:

| Region | Offset and size |
| --- | --- |
| Preserved resident loader | `[0x00000, 0x07000)` |
| Application | starts at`0x07000`, must end at or before`0x2A000` |
| Profile bank | `[0x2A000, 0x36000)`, exactly49,152 bytes |
| Existing installation identity page | `0x37F00`, unchanged |
| Existing QMK legacy backing | `[0x38000, 0x40000)`, unchanged |

The bank has six8,192-byte records with fixed ownership: records0/1 belong to profile1, records2/3 to profile2, records4/5 to profile3. Each record contains a256-byte header, a7,680-byte payload area, and a separate256-byte commit page at record offset7,936. Unused header/payload/commit padding is `FF`; explicitly reserved fields below are zero. CRC32 uses the reflected IEEE algorithm defined above.

| Header offset | Field |
| --- | --- |
| 0..7 | ASCII `IGPBANK1` |
| 8 / 10 / 12 | format u16=`1`, page size u16=`256`, board u16=`16` |
| 14 / 15 | fixed owning profile byte1..3, reserved zero |
| 16 / 18 | IGCF schema u16, reserved u16 zero |
| 20 / 24 | payload length u32, generation u64 |
| 32 / 36 | payload CRC32 u32, header CRC32 u32 over bytes0..35 |
| 40..255 | `FF` |

| Commit-page offset | Field |
| --- | --- |
| 0..7 | ASCII `IGPCMT1!` |
| 8 / 10 / 12 | format u16=`1`, board u16=`16`, owning profile byte1..3 |
| 13..15 | zero |
| 16 | generation u64, equal to header |
| 24 | body CRC32 u32 over record bytes0..7935 |
| 28 | commit CRC32 u32 over commit bytes0..27 |
| 32..255 | `FF` |

A valid record needs both envelopes, matching owner/generation, exact padding, all CRCs and structural validation of the complete IGCF payload. Within a pair, the higher strictly compared u64 generation wins. Equal generations are ambiguous, even for equal payload bytes. Generation zero is reserved for a validated read-only fallback; `UINT64_MAX` remains readable but cannot be incremented. A recognized header or commit with a valid envelope CRC and an unknown format or foreign board/profile blocks bank access and saving; it is never reclaimed as scratch.

An empty pair uses the validated legacy configuration or immutable compiled defaults without writing it into the bank. If neither half is valid and at least one is torn, fallback is explicitly marked degraded. A valid half beside a torn half remains readable and is marked recovered. A new explicit save may reclaim only the non-current half of its own pair; the three currently selected records and all other pairs remain untouched. No boot, switch or scan performs repair. Ambiguous equal generations block all saves while unaffected pairs remain readable.

Saving invalidates/prepares the target's commit page first, programs and verifies its body, then writes and verifies the separate commit page last. The pinned WB32 driver physically erases to zero and rewrites whole256-byte pages; preparing a logical empty page therefore programs `FF`. A power cut may leave zero or partially programmed bytes in the inactive record. The codec fault tests model those phases, but they are not physical power-loss acceptance. An unsuccessful actual save latches storage unavailable until a normal reconnect; a later explicit save can reclaim a torn inactive half after the bank is reread.

The legacy QMK initialization path is read-only in this build, including full or corrupt logs. Public legacy writes/erase and staged legacy commits are rejected. Upload staging borrows the opposite legacy cache half; abort or completion restores the cache by read-only replay and verifies the original cache hash. Original fallback data is never rewritten by a bank save.

### Profile requests and status

The RAW names below use decimal command numbers. Every packet still obeys the common envelope, sequence and zero-padding rules. Bank-capable firmware rejects legacy commands3–6, including the old unowned ABORT. `INFO` still describes the active configuration, but its generation is only the low32 bits. Hosts must use RAW18's u64 generation and bank storage-health flag for bank operations.

**RAW18 / `0x12 PROFILE_STATUS`:** request one query-slot byte, `0` for active or`1..3` for a fixed profile. Query0 while active0 returns busy. Successful response is exactly24 bytes:

| Offset | Field |
| --- | --- |
| 0 / 1 / 2 | format1, board16, profile count3 |
| 3 / 4 | physical position0..3, active profile0..3 |
| 5 | flags: storage healthy`0x01`, queried fallback`0x02`, switch/active-record refresh pending`0x04`, upload open`0x08`, physical Mac`0x10`, effective Mac`0x20`, OS pending`0x40`, queried recovered/degraded`0x80` |
| 6 / 7 | resolved queried profile1..3, reserved zero |
| 8 / 16 | queried generation u64, payload CRC32 u32 |
| 20 / 22 | queried payload length u16, IGCF schema u16 |

Physical position0 is unknown, not profile1. Storage health is permission to save to the bank, independent of whether the queried snapshot can still be read. The fallback/recovery flags describe the queried pair. Status reads never activate a profile, sample arbitrary addresses or report held keys. They may expire an abandoned upload lease, which discards RAM only.

**RAW19 / `0x13 PROFILE_READ`:** request16 bytes: profile at0, count at1 (`1..12`), offset u16 at2, expected generation u64 at4, expected payload CRC32 u32 at12. Response is`12 + count` bytes: exact echo of request bytes0..11, followed by data. The CRC precondition is checked but not echoed. The host freezes the profile identity, reads every byte, then rechecks status, generation, CRC and installation GUID and validates the whole IGCF payload.

**RAW20 / `0x14 PROFILE_BEGIN`:** request24 bytes: profile at0, reserved0 at1, total length u16 at2, expected generation u64 at4, current payload CRC32 u32 at12, new payload CRC32 u32 at16, nonzero random owner token u32 at20. Response echoes all24 bytes. Only one upload may exist. A new BEGIN cannot abort or replace another owner's staging. Both destination generation and current CRC must match. The token binds a transfer; it is not authentication against other local applications.

The upload lease lasts30seconds from BEGIN or the last newly accepted chunk bytes. Duplicate or invalid packets do not extend it. Housekeeping and returning profile commands enforce expiry even while keys are held. Expiry discards only staged RAM by read-only replay, invalidates the token and never commits. Subsequent WRITE/COMMIT/ABORT from that expired owner returns no-upload. Starting again requires a new explicit BEGIN.

**RAW21 / `0x15 PROFILE_WRITE`:** request`8 + count` bytes: token u32 at0, profile at4, count at5 (`1..16`), offset u16 at6, data at8. Response8 bytes: token u32 at0, profile at4, reserved0 at5, total received u16 at6. Match token and profile before accepting bytes. Gaps and conflicting duplicates fail; an earlier byte-identical chunk may be acknowledged without changing state or extending the lease.

**RAW22 / `0x16 PROFILE_COMMIT`:** request20 bytes: token u32 at0, profile at4, reserved zeros5..7, expected generation u64 at8, payload CRC32 u32 at16. Require the complete semantically valid payload, original destination/generation/current CRC, matching owner, and safe idle state. Held keys, a macro, pending knob action, upload conflict, lighting/device test, USB restart or suspend prevent persistence. Response16 bytes: profile at0, reserved0 at1, saved length u16 at2, new generation u64 at4, payload CRC32 u32 at12. Actual flash verification precedes acknowledgment. Physical selector movement cannot retarget the commit. The host then reads and compares the entire saved payload byte-for-byte; a lost reply must be resolved with explicit readback, never an automatic COMMIT retry.

**RAW23 / `0x17 PROFILE_ABORT`:** request8 bytes: token u32 at0, profile at4, reserved zeros5..7. Response echoes all8 bytes. Only the matching owner may discard that upload. Cleanup uses read-only legacy replay and never writes a profile.

**RAW24 / `0x18 PROFILE_INSTALLATION_IDENTITY`:** empty request. Response20 bytes: format1 at0, board16 at1, reserved zeros2..3, the16 installation-GUID bytes at4. The GUID bytes use the existing installation-record layout (`Guid.ToByteArray`/`new Guid(bytes)` on the host). Firmware validates the fixed record's magic, version, board, CRC and nonempty/non-`FF` GUID before replying. No original-firmware digest or arbitrary memory is exposed. This is a recovery-installation identifier, not a hardware UID or proof of firmware authenticity.

### Bound USB interval application

On bank-capable board16, **RAW8 requires16 bytes**: profile at0 (`1..3`), reserved zeros1..3, expected generation u64 at4, payload CRC32 u32 at12. The old empty request is rejected. Other firmware keeps its existing empty request. The host verifies the installation GUID, full profile snapshot, active state and timing before sending; the firmware atomically checks the bound slot/generation/CRC in the handler. It also reads only the already-sampled selector rows and rejects unknown/debouncing contacts, a different physical position, upload, pending active-record refresh or unsafe runtime state. Neither an address nor a GPIO operation can be requested.

The successful four-byte response remains current interval, saved interval, reconnect-needed byte0/1 and reserved0. Acknowledgment precedes scheduling. Before executing the deferred restart, firmware rechecks the same profile/generation/CRC and physical selection. A changed selection cancels that request rather than restarting another profile. The already verified persistent save stays successful if the host loses this acknowledgment; the app reports that the active polling rate could not be confirmed and does not retry blindly.

Cold boot initially enumerates with the immutable legacy polling interval. The first stable profile activation, and each later actual physical profile change, may create one automatic reconnect plan if that profile requests a different interval. The plan is bound to its slot, full generation, CRC and requested interval. It waits at least five seconds after activation and five seconds after the last successful RAW19 read, plus the normal deferred-restart delay of at least100ms. RAW18 status and RAW24 identity queries do not extend this read-quiescence period. Scheduling and execution require active USB, healthy storage and idle keys, macros, knob actions, transfers and lighting/device tests.

A successful COMMIT refresh of the same profile does **not** create an automatic plan: the host completes its full saved-payload readback before requesting bound RAW8. A valid BEGIN or bound RAW8 takes over from an automatic plan. Any selector, slot, generation, CRC, requested-interval or storage-health mismatch cancels the old plan without automatic retry; only a new actual profile activation or a new explicit bound RAW8 can create another. Reads may postpone an otherwise valid automatic plan, while ordinary status polling cannot keep it waiting indefinitely. Equal active/saved intervals require no reconnect. Neither path writes configuration, resets the MCU or enters the bootloader. Physical USB timing acceptance remains pending.

### Activation, host binding and preservation

Profile activation runs in housekeeping only after physical keys, macros, pending knob actions, transfers, lighting/device tests, suspend and an existing reconnect are idle. It clears transient layers/input/effect state, resets runtime brightness and adopts the selected profile's settings without changing any stored bytes. Unknown/transitional selector contacts retain the active profile. Only the documented selector holes are excluded from physical key checks; ordinary keys remain protected.

The app reads the actual device configuration at startup and binds editing to installation GUID, explicit slot, full generation and CRC. A switch does not silently retarget a dirty draft. The Windows editor retains drafts separately by installation and slot; the portable editor keeps its draft pinned until an explicit reload when the device state differs. Before a normal transfer, the bound slot must still be active. Movement after BEGIN cannot change the explicit destination. Hardware-profile status shares the serialized monitor described above; foreground operations pause and drain it.

Firmware provenance explicitly declares `profile-pairs-v1` only for this board and bounded application size. An update preserves the complete49,152-byte bank and32,768-byte legacy tail byte-for-byte after matching full reads; unused gaps and the original installation identity remain checked. No normalization or repair is performed by the updater. Downgrade to a firmware layout without the bank is blocked. Restoring the captured original firmware remains a separate operation, retaining full current captures and exporting valid profiles before replacing the image. No configuration command can install or restore application firmware.

Native codec, actual patched QMK, mocked driver/runtime and host-preservation checks are automated evidence. Physical three-profile saves, switching without the app, reconnecting and app loading have been confirmed on one GMMK 3 PRO 100% ISO under Windows. Deferred timing, controlled interruption and preservation of distinct profiles through a firmware update or original-firmware restoration remain separate physical acceptance checks. These observations do not establish support for another keyboard variant.
