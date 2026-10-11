# GMMK 3 development port

App 0.6.37 and firmware candidate 0.11.0 add six experimental runtime targets. No GMMK 3 was physically available for testing. The existing local updater remains on 0.9.2 for its two previously identified devices. Original firmware detection, backup, installation and restore are not enabled for these new targets.

## Scope and provenance

The [Glorious FAQ](https://glorious.ladesk.com/266984-GMMK-3-All-Variants---FAQ) limits QMK to wired, non-HE variants and links its [manufacturer fork](https://gitlab.com/glorious-public/qmk_firmware). The actual source has generic GMMK 3 targets. It does not establish separate PRO PCB identities: do not alias an unknown GMMK 3 PRO to one of these boards merely from its retail name. HE and wireless operation are not implemented.

| Target pair | Manufacturer branch | Pinned commit |
|---|---|---|
| 65 ANSI/ISO | `gmmk3_p65_ansi_iso_wired` | `6ce76ee92f7d83b798147baaed2d3d8ec89564b2` |
| 75 ANSI/ISO | `gmmk3_p75_ansi_iso_wired` | `121e1a6341c5c045b396dab8c5e755a4b9a4aefd` |
| 100 ANSI/ISO | `gmmk3_p100_ansi_iso_wired` | `f53deb1cc507412fe4cbb849c89080a1f580886c` |

Forty-two original source files and their pinned URLs/hashes are retained in `reference/glorious-gmmk3`. `firmware/Prepare-Gmmk3References.py` checks them and produces normalized references plus a separate `gmmk/inglorious3` port. Its `--check` mode is read-only. `firmware/ports/gmmk3/provenance.json` records the transformations and output hashes. The build base remains QMK commit `7a1bbf37c5c07da4ea0a162bb139083a46ef40a1`.

## Reviewed differences from the old fork

- Manufacturer LED count was 126 in every variant. Driver entries, positions and flags actually contain 85/86, 101/102 and 125/126 LEDs. The port uses those actual counts.
- Both 65 layouts assigned the physical `0` key at matrix `[8,7]` to LED 0 (Esc). The driver table and key coordinates identify LED 10. ISO additionally assigned `C` at `[3,4]` to LED 56 (PgDn); the matching LED is 46. The originals are preserved unchanged.
- The 75 ISO source placed Enter `[10,4]` over Backspace and `#` `[10,2]` over Slash. Enter is normalized to `(13.75, 2.25)` and `#` to `(12.75, 3.25)`, matching their physical rows, default key order and the same matrix positions in the manufacturer's 100 ISO source. This changes drawing geometry only, not matrix addresses or electrical LED mappings.
- The source USB identity is not treated as a verified stock identity. Each port has its own development runtime PID and an explicit protocol board ID. The incorrect ANSI display name in the 65 ISO source is normalized.
- Modern QMK provides the electrical SPI initialization needed by the AW20216S driver. Old vendor VIA handlers, OS layer hooks and DIP-switch behavior are not copied. The physical OS switch is disabled until verified on hardware.
- Manufacturer rollover choices are preserved: 75% disables NKRO, while 65% and 100% enable it. The port does not silently override those variant-specific USB settings.
- Stock external flash chip-select C4 is held high. The port neither reads nor writes that chip. Preserving it is not a verified backup of stock profiles.

## Layout and storage

All six use WB32FQ95, a 14 × 8 matrix, an unlit encoder press at `[11,6]`, and 20 side LEDs (10 per side). The 75% and 100% variants also have a separate logo LED; it is excluded from side synchronization. No logo LED is invented for the 65% models.

| Device ID | Board ID / PID | Keys including knob | LEDs | Maximum config bytes |
|---|---|---:|---:|---:|
| `gmmk3-65-ansi` | 10 / 1C0A | 66 | 85 | 6216 |
| `gmmk3-65-iso` | 11 / 1C0B | 67 | 86 | 6240 |
| `gmmk3-75-ansi` | 12 / 1C0C | 81 | 101 | 6600 |
| `gmmk3-75-iso` | 13 / 1C0D | 82 | 102 | 6624 |
| `gmmk3-100-ansi` | 14 / 1C0E | 105 | 125 | 7176 |
| `gmmk3-100-iso` | 15 / 1C0F | 106 | 126 | 7200 |

New slots are 7,680 bytes, starting at 1,024 and 8,704, with a 32-byte header. Their final boundary is exactly 16 KiB. IDs 1..9 retain the old 7,168-byte slots and existing persistent bytes. Both use the same 32 KiB physical backing and inactive-slot upload staging. Two logical slots do not guarantee survival of a power cut during shared QMK wear-level consolidation.

## Physical badge preview (app 0.6.38)

Badge drawing positions are separate from QMK's normalized LED effect coordinates. The preview uses the manufacturer's top-down photos: [65%](https://www.gloriousgaming.com/cdn/shop/files/GLO-KB-GMMK3-65-PB-W-BLK-US_Web_Gallery_Top.webp?v=1725664924), [75%](https://cdn.shopify.com/s/files/1/0549/2681/files/GLO-KB-GMMK3-75-PB-W-BLK-US_Web_Gallery_Top.webp?v=1725665130), [100%](https://cdn.shopify.com/s/files/1/0549/2681/files/GLO-KB-GMMK3-100-PB-W-BLK-US_Web_Gallery_Top.webp?v=1725665314). The [badge installation guide](https://www.gloriousgaming.com/en-ca/pages/guide-install-gmmk3-badges-knobs) confirms the horizontal 65% and vertical 75/100% orientation.

| Model | Preview X/Y/W/H in key units | Position |
|---|---|---|
| 65 ANSI/ISO | 15.2 / 1.0 / 1.4 / 0.85 | Horizontal below the knob; head on the right |
| 75 ANSI/ISO | 14.0 / -0.6 / 0.85 / 1.45 | Vertical between F12 and knob; head at the top |
| 100 ANSI/ISO | 20.35 / -0.6 / 0.85 / 1.45 | Vertical to the left of the knob, above the numpad |

These are photo-based drawing approximations, not CAD measurements. Preview bounds include the upper housing and right edge so the badge is neither clipped nor moved into an unrelated control row. The physical 65% badge is shown without lighting or an editor action because the reviewed port has no separately mapped LED for it. No wire payload, LED matrix, firmware source or frozen candidate changes for this display correction.

## Before hardware installation

Verify the PCB revision, MCU, bootloader and flash geometry on a physical unit; capture two matching original images and establish recovery. Check every key, encoder direction/press, side segment and logo, plus standby, persistence and recovery keys. USB names and a compiled binary alone are insufficient. No automatic installation rights are inferred from this source port.

GMBK and Eternal require a different approach; see [the separate discovery findings](discovery-gmbk-eternal.md).
