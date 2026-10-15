# INGLORIOUS firmware protocol 1 / configuration schemas 1, 2, 3 and 4

This is the implementation contract for **firmware 0.11.4**, assigned build ID `0x000B0401`. The installation and update catalog supplies this release for all 16 model/layout variants; hardware validation is tracked separately in [the model overview](../docs/model-support.md). All multi-byte integers are little-endian. No packet enters a bootloader, resets the MCU, writes firmware, changes protection, or exposes arbitrary memory. Command 8 explicitly schedules a USB re-enumeration to apply a saved keyboard polling interval.

## Identity and transport

Development USB VID `0xFEED`, PRO PID `0x1C01`, Numpad PID `0x1C02`; these are private development identifiers, not a registered vendor allocation. Manufacturer `INGLORIOUS`, product `INGLORIOUS PRO ISO` / `INGLORIOUS Numpad`. Raw HID usage page `0xFF60`, usage `0x0061`, fixed 32-byte reports, report ID zero (Windows host buffer is 33 bytes including the leading zero). Require the identity and successful protocol INFO handshake; stock Glorious identities must never receive these packets.

Candidate0.10 adds board IDs3..9 and corresponding development PIDs1C03..1C09. The exact dimensions, products and capabilities are in [`targets.json`](targets.json). IDs3/4/5 are PRO rev1 ANSI, rev2 ANSI, rev2 ISO; IDs6/7/8/9 are GMMK2 65 ANSI,65 ISO,96 ANSI,96 ISO. All keyboards return lighting pattern1; only Numpad returns pattern2. The reserved encoder area must be zero on GMMK2; slider mode must be zero on all boards except Numpad. Runtime support is independent of stock recognition and firmware installation permission.

| Board | Schema1 bytes | Schema2/3/4 bytes | Schema3/4 macro events |
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

Candidate 0.11 adds GMMK 3 wired, non-HE board IDs 10..15 / PIDs 1C0A..1C0F in 65 ANSI, 65 ISO, 75 ANSI, 75 ISO, 100 ANSI, 100 ISO order. Each has an encoder and no slider. Their slot capacity is 7,648 payload bytes rather than the 7,136 bytes on IDs 1..9. Slots start at 1,024 and 8,704, with a 32-byte header. The second ends at 16,384. The 75/100 logo LED is an independent RGB LED, excluded from side pairing. It uses the existing per-LED color/effect fields without a wire-format extension.

The existing boards retain their wire/persistent layouts and slot addresses. Upload staging borrows the inactive slot payload in the existing16KiB RAM cache. BEGIN/WRITE_STAGE perform no backing writes. Ordinary EEPROM writes/erase/init are blocked while borrowed. ABORT replays the backing strictly read-only and verifies the original full cache hash. Only a semantically validated COMMIT can perform the ordered invalid-marker, forced-payload, header, valid-marker writes. The forced flush is necessary because staged bytes already equal the cache. Driver errors latch a fault. Consolidation retains the existing power-loss limitation; this is not a new atomicity guarantee.

Board 16 is the separate GMMK 3 PRO 100% ISO wired, non-HE target, PID `1C10`. It has the same configuration dimensions and slot sizes as board 15 but requires its own board identity. Its 256 KiB flash image preserves a 28 KiB resident-loader prefix; the application starts at `0x08007000`, the installation identity page at `0x08037F00`, and the 32 KiB settings region at `0x08038000`. These image-layout differences do not expose arbitrary memory through Raw HID.

These uppercase USB display names are present from release0.7 onward. Frozen release0.6 retains its original display strings and hashes. Device identifiers and protocol bytes are unchanged.

Common packet: bytes 0..1 ASCII `IG`, byte 2 protocol `1`, byte 3 command, byte 4 request sequence (echoed), byte 5 status (request must zero), byte 6 payload length 0..24, byte 7 zero, bytes 8..31 payload/padding (unused padding must zero). Responses have the same envelope. Status: 0 OK, 1 malformed, 2 range, 3 busy, 4 no upload, 5 CRC mismatch, 6 invalid config, 7 storage failure, 8 stale generation, 9 unsupported command. Validation/range/CRC/generation failures precede persistence. A storage error or timeout can have an uncertain commit result; query INFO and read back instead of retrying blindly. On a storage failure the firmware reloads its backing store, reselects a valid slot/defaults and disables further uploads for this boot (INFO persistent flag cleared).

| Command | Request payload | Successful response payload |
| --- | --- | --- |
| `0x01 INFO` | empty | 24 bytes described below |
| `0x02 READ_ACTIVE` | offset u16, count u8 (1..20), reserved zero | offset u16, count u8, reserved zero, data[count] |
| `0x03 WRITE_STAGE` | offset u16, count u8 (1..20), reserved zero, data[count] | next expected offset u16 |
| `0x04 BEGIN` | total blob size u16, expected active generation u32, expected CRC32 u32 (10 bytes) | next expected offset u16 = 0 |
| `0x05 COMMIT` | expected CRC32 u32, expected active generation u32 (8 bytes) | new generation u32, active CRC32 u32 |
| `0x06 ABORT` | empty | empty |
| `0x08 APPLY_USB_INTERVAL` | empty, idle device required | active interval byte, saved interval byte, queued byte(0/1), reserved zero |
| `0x09 READ_RUNTIME_BRIGHTNESS` | empty | brightness byte, active slider mode byte, two reserved zero bytes |
| `0x0A SET_RUNTIME_BRIGHTNESS` | one brightness byte0..255 | the exact accepted brightness byte |
| `0x0B GET_EXTENSIONS` | empty | version u16=1, flags u16(bit0=bounded manual light test, bit1=standby screensaver), durationMs u16, reserved u16=0; release0.9 reports flags3, duration21000 |
| `0x0C LIGHT_TEST` | action byte: 0=status, 1=start, 2=stop | active byte0/1, pattern byte0=idle/1=keyboard/2=Numpad, remainingMs u16 |
| `0x0D GET_FIRMWARE_VERSION` | empty | 16-byte release identity described below; no state changes |
| `0x0E PHYSICAL_DIAGNOSTICS` | action byte: 0=status, 1=start/refresh, 2=stop, 3=refresh-only | 24 bytes described in board 16 diagnostics below |
| `0x0F DIAGNOSTIC_LEDS` | mode, LED index, R, G, B | echoed request, reserved zero, remainingMs u16; requires active session |

INFO payload: board byte0 (1 PRO, 2 Numpad), rows byte1, cols byte2, layers byte3=4, LEDs byte4, macro slots byte5=32, max events per macro byte6=128, flags byte7 (bit0 persistent storage available, bit1 slider present, bit2 staging active, bit3 schema2 effects supported, bit4 runtime timing and command8 supported, bit5 schema3 color sequences supported, bit6 volatile brightness commands9/10 supported, bit7 schema4 autonomous standby supported), **active** blob size u16 at8, **active** event capacity u16 at10 (768 in schema1/2; PRO668 or Numpad702 in schema3/4), active generation u32 at12, active CRC32 u32 at16, received staging bytes u16 at20, **active** config schema u16 at22 (1,2,3,4). Firmware0.6 sets bits3..7 even while preserving an active schema1 blob. It accepts either supported size at BEGIN; schema2/3/4 have the same length. The active size is not an upload-capacity restriction. An older host that rejects unknown capability bits must stop before writing; a newer host must check the relevant capabilities before uploading settings. In particular schema3 requires bit5 even if its stored color sequences are currently unused by the chosen effects.

`GET_FIRMWARE_VERSION`, added in release0.8.0, reports the firmware's own explicit release identity in normal USB mode. Its request is empty. The successful16-byte payload for release0.9.1 is: format u16 at0=`1`; board u8 at2=`1` PRO or`2` Numpad; flags u8 at3=`0` (reserved); major u16 at4=`0`; minor u16 at6=`9`; patch u16 at8=`1`; reserved u16 at10=`0`; build identifier u32 at12=`0x00090101`. Release0.8.0 retains minor8 and build`0x00080001`. The build identifier is an assigned release identifier, **not** a binary hash, signature or authenticity proof. Hosts validate format, board, flags, reserved fields and a nonzero build, then compare the three numeric version components. A valid unsupported-command status9 means an older firmware cannot report its version; a timeout or malformed answer means the query failed and must not be treated as a version. USB `bcdDevice`, INFO capabilities, active config schema and extension-protocol version are not release numbers.

RAW13 is read-only and available during a staged upload, running macro, held key, lighting test, screensaver, pending USB re-enumeration or storage fault. It does not reload or write persistent storage, change staging/generation/CRC, wake standby, affect a lighting test, send input, or schedule/reset anything. It shares INFO's initialization gate and ordinary packet/padding validation. INFO, GET_EXTENSIONS and configuration schemas1..4 retain their layouts; GET_EXTENSIONS reports the new screensaver capability and test duration. App 0.6.25 uses this explicit runtime report for version comparison; an unavailable report leaves the version unknown. Setup from identified original firmware remains separate. A confirmed firmware write still requires fresh matching full before-captures, exact approved code hashes and two complete post-write readbacks; an update preserves the 32 KiB configuration backing byte-for-byte.

Uploads are sequential: first offset0, then next expected offset. An exact duplicate of already-received bytes is acknowledged idempotently; gaps, conflicting duplicates and out-of-range writes fail. BEGIN clears old staging; generation must match. COMMIT requires all bytes, matching IEEE CRC32 and semantic validation, no pressed matrix keys and no running macro. Generation mismatch aborts commit. On success select the verified persisted slot; ABORT drops staging. Empty/default configuration has generation0 and a computed CRC. Host reads the complete blob back after COMMIT and compares it byte-for-byte. Do not automatically retry an uncertain COMMIT: query INFO/readback first.

CRC32 is reflected IEEE, polynomial `0xEDB88320`, initial value `0xFFFFFFFF`, final XOR `0xFFFFFFFF`, computed over the entire fixed-size config blob including zero padding.

## Config blob layout

Schema1: PRO rows11, cols8, LEDs99, bytes **5140**; Numpad rows5, cols4, LEDs31, bytes **3780**. Formula `32 + 4*rows*cols*2 + 4*2*2 + 4*leds*3 + 32*4 + 768*4`. Schema2 preserves every keymap/RGB/macro offset and appends `32 + 4*leds*3` bytes: PRO **6360**, Numpad **4184**. Schema3 and schema4 have exactly the schema2 length and effect offsets; it dedicates the end of the original macro-event area to color sequences, reducing event capacity as documented below. No firmware boot automatically rewrites a legacy configuration.

Header, 32 bytes:

| Offset | Value |
| --- | --- |
| 0..3 | ASCII `IGCF` |
| 4..5 | schema u16 =1,2,3,4, must match exact blob length |
| 6 | board1..9 from targets.json |
| 7 | layers =4 |
| 8,9,10 | rows, cols, LEDs |
| 11 | macro slots =32 |
| 12..13 | event capacity u16: schema1/2=768; schema3/4 PRO668/Numpad702 |
| 14..15 | used event count u16, 0..event capacity |
| 16 | slider mode: 0 disabled, 1 MIDI CC, 2 relative media volume, 3 lighting brightness |
| 17 | zero |
| 18..19 | slider calibration minimum u16 (default0) |
| 20..21 | slider calibration maximum u16 (default1023), strictly greater than minimum |
| 22 | MIDI channel 0..15 (default0) |
| 23 | MIDI controller0..127 (default62) |
| 24 | schema1 zero; schema2/3/4 USB keyboard polling interval in ms:1,2,4,8 |
| 25 | schema1 zero; schema2/3/4 debounce in ms:0..40 |
| 26 | schema4 standby enabled0/1; otherwise zero |
| 27..28 | schema4 standby timeout seconds u16:10..3600; otherwise zero |
| 29 | schema4 standby mode0=off,1=dim,2=screensaver; mode2 requires GET_EXTENSIONS bit1, even if byte26 is0; otherwise zero |
| 30 | schema4 dim brightness percent1..100; otherwise zero |
| 31 | zero |

After header, in order:

1. Keymap `u16[4][rows][cols]`, row-major. Missing physical positions must be KC_NO0. Allowed basic QMK keycodes0..255, QMK modded basic codes0x0100..0x1FFF, TO(layer)0x5200..0x5203, MO(layer)0x5220..0x5223, TG(layer)0x5260..0x5263, macro0..31 as0x7700..0x771F. KC_TRNS1 falls through. Other QMK functions, boot/reset codes and arbitrary custom actions are rejected.
2. Encoder `u16[4][2]`, index0 counterclockwise, index1 clockwise. Same keycode vocabulary; momentary layers are rejected for encoder impulses. Encoder press is an ordinary matrix key.
3. Per-layer LED RGB bytes `[4][leds][3]` in QMK LED index order, R/G/B. Includes side lights; no missing-LED placeholders. Configured values are raw0..255, multiplied by a runtime brightness scale. No host animation stream is required.
4. Macro descriptors `[32]`, each start-event u16 and event-count u16. Each count <=128; ranges must lie within used event count. Empty descriptors have start0,count0. Sharing immutable event ranges is allowed.
5. Event pool `[768]`, each type byte, reserved byte0, value u16. Types1 key-down,2 key-up,3 delay-ms. Key values must be basic HID keyboard usages4..0xA4 or modifiers0xE0..0xE7; delay0..60000ms. Each macro must have balanced down/up events, no duplicate down/unmatched up, total delay<=300000ms. Unused events are all zero. Macro triggers do not recurse. One macro runs at a time, without blocking matrix scanning; a second macro trigger while running is ignored. Any new ordinary key press (including Escape) cancels and releases macro-held keys before the physical input is processed. A macro starts only without other host-reporting physical keys held; encoder-triggered macros require no host-reporting matrix keys held. Firmware revision 0.2 permits already-held MO(0..3) layer switches for both triggers; actual modifiers and normal keys still block start. The temporary layer-switch mask is cleared on release, non-layer remapping at that position, restart and suspend. Revision 0.1 blocked Fn-triggered macros too; the stored format is unchanged.

Slider mode2 sends bounded relative consumer volume steps after physical movement; it cannot determine the OS absolute volume, and startup position sends no volume changes. MIDI requires a MIDI-aware receiver. Brightness mode changes autonomous lighting live without continual EEPROM writes. Slider configuration must use mode0 on PRO.

## Board 16 physical diagnostics (0.11.3)

GET_EXTENSIONS format 1 adds flag `0x04` only for board 16; its combined flags are 7. The original fifteen targets remain at flags 3. No stored configuration layout changes. Hosts must check this flag before using commands 14 or 15.

RAW14 takes one action byte: 0 reads status, 1 starts or refreshes an active session, 2 stops, and 3 refreshes only an already-active session. Action 3 returns status 8 (conflict) if the session expired, stopped or was suspended; it never restarts or clears observations to satisfy a late poll. Hosts use action 1 only for explicit user start and action 3 for periodic refresh. The successful 24-byte payload is format 1 at byte 0, active boolean at 1, matrix rows 14 at 2, columns 8 at 3, fourteen cumulative row bitmasks at 4..17, clockwise counter u16 at 18, counterclockwise counter u16 at 20, and watchdog milliseconds remaining u16 at 22. Row bits latch physical pressed positions since session start, exclude matrix holes, and survive release, remapping and refresh. Encoder counters count pressed encoder events before keycode processing, including unmapped bindings, and wrap modulo 65536. Stop, expiry and suspend clear the session observations.

RAW15 takes five bytes: mode at 0 (0 clears the overlay, 1 colors all LEDs, 2 colors one LED), LED index at 1 (zero unless mode 2), and sRGB R/G/B at 2..4 (all zero for mode 0). The successful eight-byte response echoes those five bytes, reserves byte 5 as zero and reports remaining milliseconds at 6. One-LED mode makes all other LEDs black. Colors pass through the normal sRGB-to-PWM conversion once and temporarily override the saved brightness/effect. Clearing the overlay restores normal rendering without stopping the session. RAW15 requires an active session and never refreshes its deadline.

The session lasts at most 10000 ms without RAW14 action 1 or 3, using wrap-safe elapsed time. Housekeeping, rendering and status enforce expiry; USB suspend and explicit stop also restore normal profile rendering. A new session rejects held keys, staged uploads, running macros, an existing manual lighting test, pending USB re-enumeration or suspend. BEGIN, COMMIT, reconnect, volatile brightness and starting the old manual test return busy while active. Configuration reads and identity queries remain available. No diagnostic action writes configuration storage. Keys and the encoder retain their normal operating-system behavior; the app must explain that limitation. A lost host connection while the keyboard stays powered is bounded by the watchdog.

## Schema2 lighting extension

At the schema1 end offset, four 8-byte global effect records appear in layer order. Each contains mode0..20, speed0..255, brightness0..255, target1=keys/2=sides/3=all, flags (bit0 reverse, bit1 synchronized side coordinates), and three reserved zero bytes. In schema3/4, record byte5 is a color-sequence ID0..8; bytes6/7 remain zero. Default record: `00 80 FF 03 00 00 00 00`.

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

Let `L` be the board's LED count and `V1Size` the schema1 size. Reserve `R = (200 + 2*L + 3) & ~3` bytes at `P = V1Size - R`. PRO has R400, P4740 and668 macro events; Numpad has R264, P3516 and702 events. Header12 must report that exact capacity. Event storage ends at P; unused event bytes before P are zero. The maximum staging allocation remains6360/4184 bytes, unchanged from schema2. No additional RAM-resident palette or active-config blob is allocated.

At P is an eight-entry pool. Each25-byte entry contains a color count0..8 followed by eight RGB triples. Bytes beyond count are zero. Empty entries are all zero and follow all nonempty entries; duplicate entries, holes and unreferenced nonempty entries are invalid. ID0 means no custom sequence; IDs1..8 select pool entries0..7.

At P+200, packed4-bit IDs cover all LEDs on all four layers in layer-major order. Even flattened indices use the low nibble, odd indices the high nibble. An inherited `FF 00 00` LED effect must have ID0 and obtains the global sequence. An explicit override with ID0 retains the legacy generated colors, independently of any global sequence. Remaining bytes up to V1Size are zero. Global IDs live at effect-record byte5, as above. Pool IDs follow canonical first occurrence: for layer0..3 visit its global ID and then each LED ID0..L-1; the next unseen nonzero ID must be1, then2, and so on. This makes serializer/decode/re-encode byte-exact.

Mode2 uses the existing period `10000 - speed*9600/255` ms. `phase = (time % period)*256/period`; multiply phase by color count, select index from the high byte, and interpolate to the next entry (wrapping last to first) using the low byte as weight. Each component is `(a*(256-weight)+b*weight)>>8`. One color remains constant. Effect brightness and the runtime dimmer are then applied using the same integer scaling as the other modes. The desktop preview uses the same arithmetic and is checked against the actual C helper.

Firmware0.5 reads valid schema1/2 slots without changing their bytes, generation or CRC. Schema3 is written only by an explicit upload whose host checks INFO bit5. Older firmware cannot accept schema3. Downgrading firmware while a schema3 slot remains active is not a compatible configuration migration; first explicitly save a supported legacy configuration if such a downgrade is intended.

## Volatile brightness (firmware0.5)

INFO bit6 gates commands9/10. Command9 only reads the existing brightness scale and current slider mode; it does not sample ADC, write EEPROM or change generation. PRO always reports slider mode0. Command10 changes only the runtime0..255 scale and echoes it. It rejects a pending upload, USB re-enumeration or active manual lighting test with status3; key presses and the automatic screensaver do not block it. During screensaver output the new value is retained for normal lighting, without scaling the overlay. Invalid lengths, reserved bytes and nonzero padding are rejected before the assignment. These commands never persist configuration, allocate storage, change timing, reset the MCU or enter a bootloader. Brightness starts at255 after a cold power cycle; autonomous Numpad brightness mode then uses the physical slider position.

An optional desktop relay can read Numpad brightness and set PRO brightness only when it changes. That relay requires the desktop app to remain running. Local Numpad dimming and all stored lighting continue without the app; there is no direct USB device-to-device link. A timeout must not be treated as a confirmed set, and these calls do not use the persistent COMMIT API.

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

Boot loads and validates the active slot in the early keyboard hook before USB descriptor initialization. A verified COMMIT applies debounce immediately and records the desired interval, but does **not** disconnect USB. Host must finish its complete byte-for-byte readback before explicitly requesting command8. Command8 rejects a storage fault, active upload, running macro or held key. It acknowledges current/saved intervals before scheduling any action. If different, the device waits at least100ms and until no key/macro/upload is active and USB is not suspended, then clears keyboard reports and restarts its USB interface. If already equal, it is a no-op. A later verified config reload cancels an earlier queued reconnect. RAW-HID/MIDI endpoint intervals are unchanged. The saved interval also applies on the next physical reconnect; an explicit USB restart never jumps to a bootloader or modifies EEPROM.

## Persistence and safety scope

Both boards use QMK embedded-flash wear levelling, **not external SPI EEPROM**. Reserve the final32KiB physical flash via a linker limit (PRO224KiB code, Numpad96KiB code), 16KiB logical EEPROM shadow. The first1024 logical bytes remain QMK-owned. Two7168-byte slots start at1024 and8192; each has a32-byte slot header followed by the blob. Slot header records magic/schema/board/length/generation/CRC and a final valid marker. Validate and read back before selecting a new slot. At startup choose the newest valid CRC slot; otherwise compile-time defaults. Runtime active data is read from EEPROM shadow on demand; allocate one maximum-size6360/4184-byte staging buffer. No second active blob is allocated.

The two logical slots detect partial writes; they do not yet prove atomic power-loss survival across QMK's physical wear-level consolidation. All persistence calls check `wear_leveling_write` status directly, because QMK's EEPROM wrapper discards it. Commit then reloads using `wear_leveling_init` and validates the actual backing data/selected slot; this replay can itself consolidate and is not described as a pure physical read. Firmware must not advertise tested power-loss recovery until actual controlled tests pass. External stock profile storage is not touched by this driver; the Numpad external flash CS B6 is held inactive. MCU ROM bootloaders stay available, while stock in-flash updater code would be replaced and is preserved in the captured original images. No flashing is performed by the configuration protocol.

## Fixed physical recovery gesture

Hold **Space+B on PRO ISO**, or **Plus+Enter on the Numpad**, while connecting USB. Keep both held for approximately three seconds. Exactly these two physical switches must be down; any third switch or release cancels entry. The check runs only once during startup, after matrix initialization: 50 ms debounce (covering all supported0..40ms settings), followed by500 ms of sustained exclusive holding. The upstream Numpad additionally waits two seconds while enabling its lighting hardware. Physical coordinates are PRO `(9,4)+(4,5)`, Numpad `(1,3)+(3,3)`, so remapping and corrupt configuration do not remove the gesture.

The gesture invokes the upstream QMK ROM-bootloader jump without clearing configuration. It is unavailable through RAW HID or configurable keycodes. Native mocked tests can verify gesture timing/selection; successful actual ROM entry on flashed hardware remains a separate acceptance check. The MCU hardware boot control remains the fallback if application startup itself fails.
