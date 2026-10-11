// SPDX-License-Identifier: GPL-2.0-or-later
// Local INGLORIOUS configuration runtime. No firmware-update or arbitrary-memory API.
#include QMK_KEYBOARD_H
#include "raw_hid.h"
#include "eeprom.h"
#include "wear_leveling.h"
#include "keymap_introspection.h"
#include "bootloader.h"
#include "wait.h"
#include <string.h>
#include "ig_effects.h"
#include "ig_color.h"
#include "ig_timing.h"
#include "ig_version.h"
#include "ig_board.h"
#include "ig_storage.h"
#ifdef IG_HAS_SLIDER
#    include "analog.h"
#    include "qmk_midi.h"
static void slider_reset(void);
#endif

#define IG_LAYERS 4
#define IG_MACROS 32
#define IG_EVENTS 768
#define IG_KEYS_OFFSET 32
#define IG_ENCODER_OFFSET (IG_KEYS_OFFSET + IG_LAYERS * MATRIX_ROWS * MATRIX_COLS * 2)
#define IG_RGB_OFFSET (IG_ENCODER_OFFSET + 16)
#define IG_MACRO_OFFSET (IG_RGB_OFFSET + IG_LAYERS * RGB_MATRIX_LED_COUNT * 3)
#define IG_EVENT_OFFSET (IG_MACRO_OFFSET + IG_MACROS * 4)
#define IG_SIZE (IG_EVENT_OFFSET + IG_EVENTS * 4)
#define IG_OVERRIDE_OFFSET (IG_SIZE + IG_LAYERS * 8)
#define IG_MAX_SIZE (IG_OVERRIDE_OFFSET + IG_LAYERS * RGB_MATRIX_LED_COUNT * 3)
#define IG_SEQUENCE_BYTES ((200 + IG_LAYERS * RGB_MATRIX_LED_COUNT / 2 + 3) & ~3)
#define IG_SEQUENCE_OFFSET (IG_SIZE - IG_SEQUENCE_BYTES)
#define IG_SEQUENCE_EVENTS (IG_EVENTS - IG_SEQUENCE_BYTES / 4)
#define IG_SLOT_BYTES IG_STORAGE_SLOT_BYTES
#define IG_SLOT_HEADER IG_STORAGE_HEADER_BYTES
#define IG_VALID 0xA55A5AA5UL
_Static_assert(IG_MAX_SIZE <= IG_SLOT_BYTES - IG_SLOT_HEADER, "Config must fit one persistent slot");
_Static_assert(RAW_EPSIZE == 32, "Protocol 1 requires 32-byte RAW HID");

static uint8_t *staging;
static bool stage_open;
static uint16_t stage_received, stage_size;
static uint32_t stage_crc, stage_generation;
static int8_t active_slot = -1;
static uint32_t active_generation, active_crc;
static uint16_t active_size = IG_SIZE;
static bool runtime_ready;
static bool storage_ok = true;
static bool macro_running;
static uint16_t macro_position, macro_end;
static uint32_t macro_deadline;
static uint8_t macro_held[32];
static matrix_row_t held_layer_keys[MATRIX_ROWS];
static uint8_t brightness = 255;
static ig_effect_hit_t effect_hits[IG_EFFECT_HITS];
static uint8_t effect_hit_count;
static uint32_t effect_frame_time;
static uint32_t lighting_last_activity;
static uint8_t effect_frame_brightness;
static bool lighting_suspended;
static bool lighting_idle;
// One bounded volatile overlay; no RGB framebuffer or second configuration copy.
#define IG_LIGHT_TEST_MS 21000
static uint32_t light_test_started;
static bool light_test_active;
static const uint8_t light_test_keys[10][2] = IG_LIGHT_TEST_KEYS;
#include "ig_physical.h"

static uint16_t u16(const uint8_t *p) { return (uint16_t)p[0] | ((uint16_t)p[1] << 8); }
static uint32_t u32(const uint8_t *p) { return (uint32_t)u16(p) | ((uint32_t)u16(p + 2) << 16); }
static void put16(uint8_t *p, uint16_t v) { p[0] = v; p[1] = v >> 8; }
static void put32(uint8_t *p, uint32_t v) { put16(p, v); put16(p + 2, v >> 16); }
static uint16_t slot_base(uint8_t slot) { return IG_STORAGE_BASE + slot * IG_SLOT_BYTES; }
static uint8_t ee_byte(uint16_t offset) { return eeprom_read_byte((const uint8_t *)(uintptr_t)offset); }
static void ee_read(uint16_t offset, void *out, uint16_t len) { eeprom_read_block(out, (const void *)(uintptr_t)offset, len); }
static bool ee_write(uint16_t offset, const void *data, uint16_t len) { return ig_storage_stage_write(offset, data, len) != WEAR_LEVELING_FAILED; }
static uint32_t crc_byte(uint32_t crc, uint8_t byte) {
    crc ^= byte;
    for (uint8_t bit = 0; bit < 8; ++bit) crc = (crc >> 1) ^ ((crc & 1) ? 0xEDB88320UL : 0);
    return crc;
}
static uint32_t crc_buffer(const uint8_t *data, uint16_t len) {
    uint32_t crc = 0xFFFFFFFFUL;
    for (uint16_t i = 0; i < len; ++i) crc = crc_byte(crc, data[i]);
    return crc ^ 0xFFFFFFFFUL;
}
static uint32_t crc_eeprom(uint16_t base, uint16_t length) {
    uint32_t crc = 0xFFFFFFFFUL;
    for (uint16_t i = 0; i < length; ++i) crc = crc_byte(crc, ee_byte(base + i));
    return crc ^ 0xFFFFFFFFUL;
}
static bool key_allowed(uint16_t code, bool encoder) {
    if (code <= 0x00FF || (code >= 0x0100 && code <= 0x1FFF)) return true;
    if (code >= 0x5200 && code <= 0x5203) return true;
    if (!encoder && code >= 0x5220 && code <= 0x5223) return true;
    if (code >= 0x5260 && code <= 0x5263) return true;
    return code >= 0x7700 && code <= 0x771F;
}
static uint8_t default_byte(uint16_t offset) {
    static const uint8_t header[32] = {
        'I','G','C','F', 1,0, IG_BOARD,4, MATRIX_ROWS,MATRIX_COLS,RGB_MATRIX_LED_COUNT,32,
        0,3,0,0, 0,0,0,0, 255,3,0,62, 0,0,0,0,0,0,0,0
    };
    if (offset < 32) return header[offset];
    if (offset < IG_ENCODER_OFFSET) {
        uint16_t index = (offset - IG_KEYS_OFFSET) / 2;
        uint8_t layer = index / (MATRIX_ROWS * MATRIX_COLS);
        uint8_t key = index % (MATRIX_ROWS * MATRIX_COLS);
        uint16_t code = 0;
        if (ig_physical[key]) {
            code = layer ? KC_TRNS : keycode_at_keymap_location_raw(0, key / MATRIX_COLS, key % MATRIX_COLS);
#ifdef IG_HAS_SLIDER
            if (layer == 1 && key == 4 * MATRIX_COLS) code = KC_NUM; // Fn + numpad 0.
#endif
            if (!key_allowed(code, false)) code = KC_NO;
        }
        return (offset & 1) ? code >> 8 : code;
    }
    if (offset < IG_RGB_OFFSET) {
#if IG_HAS_ENCODER
        uint16_t code = ((offset - IG_ENCODER_OFFSET) / 2) % 2 ? KC_VOLU : KC_VOLD;
        return (offset & 1) ? code >> 8 : code;
#else
        return 0; // Reserved encoder field on boards without an encoder.
#endif
    }
    if (offset < IG_MACRO_OFFSET) {
        static const uint8_t color[3] = {32,100,220};
        return color[(offset - IG_RGB_OFFSET) % 3];
    }
    return 0;
}
static uint8_t active_byte(uint16_t offset) {
    if (offset >= active_size) return 0;
    return active_slot < 0 ? default_byte(offset) : ee_byte(slot_base(active_slot) + IG_SLOT_HEADER + offset);
}
static uint16_t active_u16(uint16_t offset) { return (uint16_t)active_byte(offset) | ((uint16_t)active_byte(offset + 1) << 8); }
static void lighting_wake(void) { lighting_last_activity = timer_read32(); lighting_idle = false; }
static void lighting_activity(void) { lighting_wake(); light_test_active = false; }
static uint16_t light_test_remaining(uint32_t now) {
    if (!light_test_active) return 0;
    uint32_t elapsed = now - light_test_started;
    if (elapsed >= IG_LIGHT_TEST_MS || lighting_suspended) { light_test_active = false; return 0; }
    return IG_LIGHT_TEST_MS - elapsed;
}
static uint8_t light_test_drop_red(uint16_t elapsed, uint8_t x, uint8_t y) {
    // QMK physical LED coordinates: x=0..224, y=0..64, including both side strips.
    // The first drops arrive before the pool starts rising; it reaches the top
    // during this phase, so the final hold does not suddenly fill a dark board.
    if (elapsed >= 1000) {
        uint16_t fill = (uint32_t)(elapsed - 1000) * 65 / 5300;
        if (fill >= 65 || (fill && y >= 65 - fill)) return 160;
    }
    uint16_t delay = (((uint16_t)x + 8) / 16 * 193) % 500;
    if (elapsed < delay) return 0;
    uint16_t head = (uint32_t)((elapsed - delay) % 1300) * 80 / 1000;
    // A bright falling head followed by a short, fading red tail. Each physical
    // column is staggered; no frame buffer, random state or persistent writes.
    if (head < y || head - y > 12) return 0;
    return 224 - (head - y) * 16;
}
static ig_rgb_t light_test_pixel(uint16_t elapsed, uint8_t led) {
    if (elapsed < 2000) return (ig_rgb_t){160,160,160};
    if (elapsed < 2500) return (ig_rgb_t){0,0,0};
    if (elapsed < 12500) {
        uint16_t step = elapsed - 2500;
        uint8_t key = step / 1000;
        bool lit = step % 1000 < 700 && led == g_led_config.matrix_co[light_test_keys[key][0]][light_test_keys[key][1]];
        return (ig_rgb_t){lit ? 160 : 0,0,0};
    }
    if (elapsed < 19000) return (ig_rgb_t){light_test_drop_red(elapsed - 12500, g_led_config.point[led].x, g_led_config.point[led].y),0,0};
    return (ig_rgb_t){160,0,0};
}
static uint8_t lighting_scale(uint32_t now) {
    if (lighting_suspended) return 0;
    if (active_u16(4) != 4 || !active_byte(26)) return brightness;
    if (!lighting_idle && (uint32_t)(now - lighting_last_activity) < (uint32_t)active_u16(27) * 1000UL) return brightness;
    lighting_idle = true; // Remain asleep across later timer wraps until actual input/resume.
    if (active_byte(29) == 2) return brightness; // Screensaver is drawn separately, like the explicit test.
    return active_byte(29) == 0 ? 0 : (uint16_t)brightness * active_byte(30) / 100;
}
static uint8_t saved_interval(void) { return active_size == IG_MAX_SIZE ? active_byte(24) : 1; }
static uint8_t saved_debounce(void) { return active_size == IG_MAX_SIZE ? active_byte(25) : 5; }
static bool macro_key_allowed(uint16_t code) { return (code >= 4 && code <= 0xA4) || (code >= 0xE0 && code <= 0xE7); }
static bool sequence_ref_valid(const uint8_t *data, uint8_t id, uint8_t *seen) {
    if (!id) return true;
    if (id > 8 || id > *seen + 1 || !data[IG_SEQUENCE_OFFSET + (id - 1) * 25]) return false;
    if (id > *seen) *seen = id;
    return true;
}
static bool validate_sequences(const uint8_t *data, bool allow_empty) {
    uint8_t count = 0, seen = 0;
    for (uint8_t id = 0; id < 8; ++id) {
        const uint8_t *entry = data + IG_SEQUENCE_OFFSET + id * 25;
        if (entry[0] > 8 || (entry[0] && count != id)) return false;
        if (entry[0]) {
            count++;
            for (uint8_t prior = 0; prior < id; ++prior) if (!memcmp(entry, data + IG_SEQUENCE_OFFSET + prior * 25, 25)) return false;
        }
        for (uint8_t i = 1 + entry[0] * 3; i < 25; ++i) if (entry[i]) return false;
    }
    for (uint8_t layer = 0; layer < IG_LAYERS; ++layer) {
        if (!sequence_ref_valid(data, data[IG_SIZE + layer * 8 + 5], &seen)) return false;
        for (uint8_t led = 0; led < RGB_MATRIX_LED_COUNT; ++led) {
            uint16_t at = layer * RGB_MATRIX_LED_COUNT + led;
            uint8_t id = (data[IG_SEQUENCE_OFFSET + 200 + at / 2] >> ((at & 1) * 4)) & 15;
            if ((data[IG_OVERRIDE_OFFSET + at * 3] == 255 && id) || !sequence_ref_valid(data, id, &seen)) return false;
        }
    }
    for (uint16_t i = IG_SEQUENCE_OFFSET + 200 + IG_LAYERS * RGB_MATRIX_LED_COUNT / 2; i < IG_SIZE; ++i) if (data[i]) return false;
    return (count || allow_empty) && seen == count;
}
static bool validate_config(const uint8_t *data, uint16_t length) {
    if (length != IG_SIZE && length != IG_MAX_SIZE) return false;
    uint16_t schema = u16(data + 4), capacity = schema >= 3 ? IG_SEQUENCE_EVENTS : IG_EVENTS;
    if (memcmp(data, "IGCF", 4) || (length == IG_SIZE ? schema != 1 : (schema < 2 || schema > 4)) || data[6] != IG_BOARD || data[7] != 4 || data[8] != MATRIX_ROWS || data[9] != MATRIX_COLS || data[10] != RGB_MATRIX_LED_COUNT || data[11] != IG_MACROS || u16(data + 12) != capacity || u16(data + 14) > capacity) return false;
    if (data[16] > 3 || data[17] || u16(data + 18) >= u16(data + 20) || u16(data + 20) > 1023 || data[22] > 15 || data[23] > 127) return false;
#ifndef IG_HAS_SLIDER
    if (data[16] != 0) return false;
#endif
    if (length == IG_MAX_SIZE) {
        if ((data[24] != 1 && data[24] != 2 && data[24] != 4 && data[24] != 8) || data[25] > 40) return false;
    } else if (data[24] || data[25]) return false;
    if (schema == 4) {
        if (data[26] > 1 || u16(data + 27) < 10 || u16(data + 27) > 3600 || data[29] > 2 || data[30] < 1 || data[30] > 100 || data[31]) return false;
    } else for (uint8_t i = 26; i < 32; ++i) if (data[i]) return false;
    if (length == IG_MAX_SIZE) {
        for (uint8_t layer = 0; layer < IG_LAYERS; ++layer) {
            const uint8_t *effect = data + IG_SIZE + layer * 8;
            if (effect[0] > 20 || !effect[3] || effect[3] > 3 || (effect[4] & ~3U) || (schema < 3 && effect[5]) || effect[6] || effect[7]) return false;
        }
        for (uint16_t offset = IG_OVERRIDE_OFFSET; offset < IG_MAX_SIZE; offset += 3) {
            const uint8_t *effect = data + offset;
            if (effect[0] == 255) { if (effect[1] || effect[2]) return false; }
            else if ((effect[0] & 0x60) || (effect[0] & 0x1F) > 20) return false;
        }
    }
    if (schema >= 3 && !validate_sequences(data, schema == 4)) return false;
    for (uint16_t i = IG_KEYS_OFFSET; i < IG_ENCODER_OFFSET; i += 2) {
        uint16_t code = u16(data + i);
        if (!key_allowed(code, false) || (!ig_physical[((i - IG_KEYS_OFFSET) / 2) % (MATRIX_ROWS * MATRIX_COLS)] && code)) return false;
    }
    for (uint16_t i = IG_ENCODER_OFFSET; i < IG_RGB_OFFSET; i += 2) {
        if (!key_allowed(u16(data + i), true) || (!IG_HAS_ENCODER && u16(data + i))) return false;
    }
    uint16_t used = u16(data + 14);
    for (uint16_t i = 0; i < capacity; ++i) {
        const uint8_t *event = data + IG_EVENT_OFFSET + i * 4;
        uint16_t value = u16(event + 2);
        if (i >= used) { if (u32(event)) return false; continue; }
        if (event[1] || event[0] < 1 || event[0] > 3) return false;
        if (event[0] == 3 ? value > 60000 : !macro_key_allowed(value)) return false;
    }
    for (uint8_t m = 0; m < IG_MACROS; ++m) {
        const uint8_t *desc = data + IG_MACRO_OFFSET + m * 4;
        uint16_t start = u16(desc), count = u16(desc + 2);
        if (count > 128 || (uint32_t)start + count > used || (!count && start)) return false;
        uint8_t held[32] = {0};
        uint32_t delay = 0;
        for (uint16_t i = start; i < start + count; ++i) {
            const uint8_t *event = data + IG_EVENT_OFFSET + i * 4;
            uint16_t value = u16(event + 2);
            if (event[0] == 3) { delay += value; if (delay > 300000) return false; continue; }
            uint8_t bit = 1U << (value & 7);
            if (event[0] == 1) { if (held[value >> 3] & bit) return false; held[value >> 3] |= bit; }
            else { if (!(held[value >> 3] & bit)) return false; held[value >> 3] &= ~bit; }
        }
        for (uint8_t i = 0; i < sizeof(held); ++i) if (held[i]) return false;
    }
    return true;
}
static bool read_slot(uint8_t slot, uint32_t *generation, uint32_t *crc, uint16_t *length) {
    uint8_t header[IG_SLOT_HEADER];
    ee_read(slot_base(slot), header, sizeof(header));
    *length = u16(header + 6);
    if (memcmp(header, "IGS1", 4) || header[4] != 1 || header[5] != IG_BOARD || (*length != IG_SIZE && *length != IG_MAX_SIZE) || u32(header + 28) != IG_VALID) return false;
    *generation = u32(header + 8); *crc = u32(header + 12);
    if (!*generation || crc_eeprom(slot_base(slot) + IG_SLOT_HEADER, *length) != *crc) return false;
    const uint8_t *data = ig_storage_view(slot_base(slot) + IG_SLOT_HEADER, *length);
    return data && validate_config(data, *length);
}
static void init_runtime(void) {
    active_slot = -1; active_generation = 0; active_size = IG_SIZE;
    uint32_t generations[2] = {0}, crcs[2] = {0};
    uint16_t sizes[2] = {0};
    bool valid[2];
    for (uint8_t i = 0; i < 2; ++i) valid[i] = read_slot(i, &generations[i], &crcs[i], &sizes[i]);
    if (valid[0]) active_slot = 0;
    if (valid[1] && (!valid[0] || (int32_t)(generations[1] - generations[0]) > 0)) active_slot = 1;
    if (active_slot >= 0) { active_generation = generations[active_slot]; active_crc = crcs[active_slot]; active_size = sizes[active_slot]; }
    else { uint32_t crc = 0xFFFFFFFFUL; for (uint16_t i = 0; i < IG_SIZE; ++i) crc = crc_byte(crc, default_byte(i)); active_crc = crc ^ 0xFFFFFFFFUL; }
    staging = NULL;
    lighting_activity();
#ifdef IG_HAS_SLIDER
    slider_reset();
#endif
    runtime_ready = true;
}
static bool any_keys_pressed(void) {
    for (uint8_t row = 0; row < MATRIX_ROWS; ++row) if (matrix_get_row(row)) return true;
    return false;
}
static bool store_staging(void) {
    uint8_t slot = active_slot == 0 ? 1 : 0;
    uint16_t base = slot_base(slot);
    uint8_t header[IG_SLOT_HEADER] = {0};
    uint32_t expected_crc = stage_crc;
    uint16_t expected_size = stage_size;
    uint32_t generation = active_generation + 1;
    if (!generation) generation = 1;
    bool success = false;
    if (!ee_write(base + 28, header, 4)) goto reload; // Invalidate inactive slot first.
    if (!ee_write(base + IG_SLOT_HEADER, staging, stage_size)) goto reload;
    if (crc_eeprom(base + IG_SLOT_HEADER, stage_size) != stage_crc) goto reload;
    memcpy(header, "IGS1", 4); header[4] = 1; header[5] = IG_BOARD; put16(header + 6, stage_size);
    put32(header + 8, generation); put32(header + 12, stage_crc);
    if (!ee_write(base, header, 28)) goto reload;
    put32(header + 28, IG_VALID);
    if (!ee_write(base + 28, header + 28, 4)) goto reload; // Valid marker is last.
    success = true;
reload:
    // EEPROM reads otherwise see only QMK's RAM cache, even after a failed physical write.
    // Replay from backing storage before reporting persistence; replay can consolidate.
    if (ig_storage_stage_finish(true) == WEAR_LEVELING_FAILED) success = false;
    init_runtime();
    ig_timing_update(saved_interval(), saved_debounce());
    stage_open = false; stage_received = 0; stage_size = 0;
    if (active_slot != slot || active_generation != generation || active_crc != expected_crc || active_size != expected_size) success = false;
    if (!success) storage_ok = false; // No retries against potentially dirty cache this boot.
    return success;
}
static bool abort_staging(void) {
    bool success = !stage_open || ig_storage_stage_finish(false) != WEAR_LEVELING_FAILED;
    stage_open = false; stage_received = 0; stage_size = 0; staging = NULL;
    if (!success) { storage_ok = false; init_runtime(); }
    return success;
}
static void reply(uint8_t command, uint8_t seq, uint8_t status, const uint8_t *payload, uint8_t count) {
    uint8_t out[32] = {'I','G',1,command,seq,status,count,0};
    if (payload && count) memcpy(out + 8, payload, count);
    raw_hid_send(out, sizeof(out));
}
void raw_hid_receive(uint8_t *data, uint8_t length) {
    if (length != 32 || data[0] != 'I' || data[1] != 'G' || data[2] != 1) return;
    uint8_t command = data[3], seq = data[4], count = data[6], status = 0, size = 0, out[24] = {0};
    const uint8_t *p = data + 8;
    if (!runtime_ready) { reply(command, seq, 3, NULL, 0); return; }
    if (data[5] || data[7] || count > 24) { reply(command, seq, 1, NULL, 0); return; }
    for (uint8_t i = count; i < 24; ++i) if (p[i]) { reply(command, seq, 1, NULL, 0); return; }
    switch (command) {
        case 1:
            if (count) { status = 1; break; }
            out[0] = IG_BOARD; out[1] = MATRIX_ROWS; out[2] = MATRIX_COLS; out[3] = 4; out[4] = RGB_MATRIX_LED_COUNT; out[5] = 32; out[6] = 128;
            out[7] = (storage_ok ? 1 : 0) | (stage_open ? 4 : 0) | 8 | 16 | 32 | 64 | 128; // Effects, timing, sequences, volatile brightness and standby.
#ifdef IG_HAS_SLIDER
            out[7] |= 2;
#endif
            put16(out + 8, active_size); put16(out + 10, active_u16(4) >= 3 ? IG_SEQUENCE_EVENTS : IG_EVENTS); put32(out + 12, active_generation); put32(out + 16, active_crc); put16(out + 20, stage_received); put16(out + 22, active_u16(4)); size = 24;
            break;
        case 2: {
            if (count != 4 || p[3]) { status = 1; break; }
            uint16_t offset = u16(p); uint8_t n = p[2];
            if (!n || n > 20 || (uint32_t)offset + n > active_size) { status = 2; break; }
            put16(out, offset); out[2] = n; for (uint8_t i = 0; i < n; ++i) out[4 + i] = active_byte(offset + i); size = 4 + n;
            break;
        }
        case 3: {
            if (count < 5 || p[3] || p[2] + 4 != count) { status = 1; break; }
            if (!stage_open) { status = 4; break; }
            uint16_t offset = u16(p); uint8_t n = p[2];
            if (!n || n > 20 || (uint32_t)offset + n > stage_size || offset > stage_received) { status = 2; break; }
            if (offset < stage_received) {
                if (offset + n > stage_received || memcmp(staging + offset, p + 4, n)) { status = 2; break; }
            } else { memcpy(staging + offset, p + 4, n); stage_received += n; }
            put16(out, stage_received); size = 2; break;
        }
        case 4:
            if (count != 10) { status = 1; break; }
            if (light_test_remaining(timer_read32())) { status = 3; break; }
            if (!storage_ok) { status = 7; break; }
            if (u16(p) != IG_SIZE && u16(p) != IG_MAX_SIZE) { status = 2; break; }
            if (u32(p + 2) != active_generation) { status = 8; break; }
            if (!abort_staging()) { status = 7; break; }
            staging = ig_storage_stage_begin(slot_base(active_slot == 0 ? 1 : 0) + IG_SLOT_HEADER, u16(p));
            if (!staging) { storage_ok = false; status = 7; break; }
            stage_open = true; stage_received = 0; stage_size = u16(p); stage_generation = u32(p + 2); stage_crc = u32(p + 6); size = 2; break;
        case 5:
            if (count != 8) { status = 1; break; }
            if (light_test_remaining(timer_read32())) { status = 3; break; }
            if (!storage_ok) { status = 7; break; }
            if (!stage_open) { status = 4; break; }
            if (u32(p + 4) != active_generation || stage_generation != active_generation) { status = 8; break; }
            if (stage_received != stage_size) { status = 2; break; }
            if (u32(p) != stage_crc || crc_buffer(staging, stage_size) != stage_crc) { status = 5; break; }
            if (!validate_config(staging, stage_size)) { status = 6; break; }
            if (any_keys_pressed() || macro_running) { status = 3; break; }
            if (!store_staging()) { status = 7; break; }
            stage_open = false; stage_received = 0; layer_clear(); brightness = 255; effect_hit_count = 0;
            put32(out, active_generation); put32(out + 4, active_crc); size = 8; break;
        case 6:
            if (count) { status = 1; break; }
            if (!abort_staging()) status = 7;
            break;
        case 8:
            if (count) { status = 1; break; }
            if (!storage_ok) { status = 7; break; }
            if (stage_open || macro_running || any_keys_pressed() || light_test_remaining(timer_read32())) { status = 3; break; }
            out[0] = ig_timing_active_interval(); out[1] = ig_timing_saved_interval(); out[2] = out[0] != out[1]; size = 4;
            break;
        case 9: // Read volatile state only; no ADC read, EEPROM or flash access.
            if (count) { status = 1; break; }
            out[0] = brightness; out[1] = active_byte(16); size = 4; break;
        case 10: // Volatile dimmer; reset by power-on, never stored with a profile.
            if (count != 1) { status = 1; break; }
            if (stage_open || ig_timing_reconnect_pending() || light_test_remaining(timer_read32())) { status = 3; break; }
            brightness = p[0]; out[0] = brightness; size = 1; break;
        case 11: // Extension discovery leaves the original INFO layout and all config schemas intact.
            if (count) { status = 1; break; }
            put16(out, 1); put16(out + 2, 3); put16(out + 4, IG_LIGHT_TEST_MS); size = 8; break;
        case 12: { // 0=status, 1=start once, 2=stop. Never persistent and never extends a running deadline.
            if (count != 1) { status = 1; break; }
            if (p[0] > 2) { status = 2; break; }
            uint32_t now = timer_read32();
            uint16_t remaining = light_test_remaining(now);
            if (p[0] == 1) {
                if (remaining || stage_open || macro_running || any_keys_pressed() || ig_timing_reconnect_pending() || lighting_suspended) { status = 3; break; }
                light_test_started = now; light_test_active = true; remaining = IG_LIGHT_TEST_MS;
            } else if (p[0] == 2) { light_test_active = false; remaining = 0; }
            out[0] = remaining ? 1 : 0; out[1] = remaining ? IG_LIGHT_TEST_PATTERN : 0; put16(out + 2, remaining); size = 4; break;
        }
        case 13: // Read-only release identity; available during staging/macros just like INFO.
            if (count) { status = 1; break; }
            put16(out, IG_FW_VERSION_FORMAT); out[2] = IG_BOARD;
            put16(out + 4, IG_FW_VERSION_MAJOR); put16(out + 6, IG_FW_VERSION_MINOR); put16(out + 8, IG_FW_VERSION_PATCH);
            put32(out + 12, IG_FW_BUILD_ID); size = 16; break;
        default: status = 9; break;
    }
    reply(command, seq, status, out, status ? 0 : size);
    if (command == 8 && !status) ig_timing_schedule_reconnect(); // ACK must precede any scheduled USB restart.
}

uint16_t keycode_at_keymap_location(uint8_t layer, uint8_t row, uint8_t col) {
    if (layer >= 4 || row >= MATRIX_ROWS || col >= MATRIX_COLS) return KC_NO;
    return active_u16(IG_KEYS_OFFSET + 2 * (layer * MATRIX_ROWS * MATRIX_COLS + row * MATRIX_COLS + col));
}
uint16_t keycode_at_encodermap_location(uint8_t layer, uint8_t index, bool clockwise) {
#if IG_HAS_ENCODER
    if (layer >= 4 || index) return KC_NO;
    return active_u16(IG_ENCODER_OFFSET + 2 * (layer * 2 + (clockwise ? 1 : 0)));
#else
    (void)layer; (void)index; (void)clockwise; return KC_NO;
#endif
}
static void cancel_macro(void) {
    for (uint16_t code = 0; code < 256; ++code) if (macro_held[code >> 3] & (1U << (code & 7))) unregister_code(code);
    memset(macro_held, 0, sizeof(macro_held)); macro_running = false;
}
static void start_macro(uint8_t index, bool physical_trigger) {
    if (macro_running) return;
    // Momentary layer switches do not produce host key reports, so Fn may remain held.
    // Other physical keys (including modifiers) must not overlap macro-held reports.
    uint8_t pressed = 0;
    for (uint8_t row = 0; row < MATRIX_ROWS; ++row) { matrix_row_t bits = matrix_get_row(row) & ~held_layer_keys[row]; while (bits) { pressed += bits & 1; bits >>= 1; } }
    if (pressed > (physical_trigger ? 1 : 0)) return;
    uint16_t start = active_u16(IG_MACRO_OFFSET + index * 4), count = active_u16(IG_MACRO_OFFSET + index * 4 + 2);
    if (!count) return;
    macro_position = start; macro_end = start + count; macro_deadline = timer_read32(); macro_running = true;
}
bool process_record_user(uint16_t keycode, keyrecord_t *record) {
    if (IS_KEYEVENT(record->event) || IS_ENCODEREVENT(record->event)) lighting_activity();
    if (IS_KEYEVENT(record->event) && record->event.key.row < MATRIX_ROWS && record->event.key.col < MATRIX_COLS) {
        matrix_row_t bit = (matrix_row_t)1U << record->event.key.col;
        uint8_t row = record->event.key.row;
        // Clear on every release and every non-MO press, even if the resolved mapping changed.
        if (record->event.pressed && keycode >= 0x5220 && keycode <= 0x5223) held_layer_keys[row] |= bit;
        else held_layer_keys[row] &= ~bit;
        if (record->event.pressed) {
            uint8_t led = g_led_config.matrix_co[row][record->event.key.col];
            if (led < RGB_MATRIX_LED_COUNT) {
                if (effect_hit_count < IG_EFFECT_HITS) effect_hit_count++;
                for (uint8_t i = effect_hit_count - 1; i > 0; --i) effect_hits[i] = effect_hits[i - 1];
                effect_hits[0] = (ig_effect_hit_t){timer_read32(), g_led_config.point[led].x, g_led_config.point[led].y, led};
            }
        }
    }
    if (keycode >= 0x7700 && keycode <= 0x771F) { if (record->event.pressed) start_macro(keycode - 0x7700, IS_KEYEVENT(record->event)); return false; }
    if (record->event.pressed && macro_running) cancel_macro();
    return true;
}
static void macro_task(void) {
    if (!macro_running || (int32_t)(timer_read32() - macro_deadline) < 0) return;
    if (macro_position >= macro_end) { cancel_macro(); return; }
    uint16_t offset = IG_EVENT_OFFSET + 4 * macro_position++;
    uint8_t type = active_byte(offset); uint16_t value = active_u16(offset + 2);
    if (type == 3) { macro_deadline = timer_read32() + value; return; }
    if (type == 1) { macro_held[value >> 3] |= 1U << (value & 7); register_code(value); }
    else { macro_held[value >> 3] &= ~(1U << (value & 7)); unregister_code(value); }
}
static void lighting_output(uint8_t led, uint8_t red, uint8_t green, uint8_t blue) {
    // Decode once, after all effects/dimmer scales. The AW20216S consumes
    // linear PWM values; stored colors and the effect engine remain sRGB.
    rgb_matrix_set_color(led, ig_srgb_to_pwm(red), ig_srgb_to_pwm(green), ig_srgb_to_pwm(blue));
}
bool rgb_matrix_indicators_advanced_user(uint8_t led_min, uint8_t led_max) {
    uint8_t layer = get_highest_layer(layer_state | default_layer_state);
    if (layer >= 4) layer = 0;
    if (!led_min) {
        effect_frame_time = timer_read32(); // One timestamp/standby scale across partial frame batches.
        effect_frame_brightness = lighting_scale(effect_frame_time);
        (void)light_test_remaining(effect_frame_time);
    }
    uint8_t effect[8] = {0,128,255,3,0,0,0,0};
    if (active_size == IG_MAX_SIZE) for (uint8_t i = 0; i < 8; ++i) effect[i] = active_byte(IG_SIZE + layer * 8 + i);
    // The automatic pattern has no manual-test state: uploads and dimmer writes
    // remain available, and ordinary activity/commit clears the existing idle latch.
    bool screensaver = lighting_idle && active_u16(4) == 4 && active_byte(26) && active_byte(29) == 2;
    uint32_t pattern_elapsed = 0;
    if (light_test_active) pattern_elapsed = effect_frame_time - light_test_started;
    else if (screensaver) pattern_elapsed = (effect_frame_time - lighting_last_activity - (uint32_t)active_u16(27) * 1000UL) % IG_LIGHT_TEST_MS;
    for (uint8_t led = led_min; led < led_max && led < RGB_MATRIX_LED_COUNT; ++led) {
        if ((light_test_active || screensaver) && !lighting_suspended) {
            // A start request arriving halfway through an old normal frame waits
            // for the next frame's timestamp, rather than rendering wrapped time.
            ig_rgb_t color = pattern_elapsed < IG_LIGHT_TEST_MS ? light_test_pixel(pattern_elapsed, led) : (ig_rgb_t){0,0,0};
            lighting_output(led, color.r, color.g, color.b);
            continue;
        }
        uint16_t offset = IG_RGB_OFFSET + 3 * (layer * RGB_MATRIX_LED_COUNT + led);
        ig_rgb_t base = {active_byte(offset), active_byte(offset + 1), active_byte(offset + 2)};
        uint8_t selected[8]; memcpy(selected, effect, sizeof(selected));
        if (active_size == IG_MAX_SIZE) {
            uint16_t override = IG_OVERRIDE_OFFSET + 3 * (layer * RGB_MATRIX_LED_COUNT + led);
            uint8_t mode = active_byte(override);
            if (mode != 255) {
                selected[0] = mode & 0x1F; selected[1] = active_byte(override + 1); selected[2] = active_byte(override + 2);
                selected[3] = 3; selected[4] = (effect[4] & 2) | (mode >> 7);
                uint16_t at = layer * RGB_MATRIX_LED_COUNT + led;
                selected[5] = active_u16(4) >= 3 ? (active_byte(IG_SEQUENCE_OFFSET + 200 + at / 2) >> ((at & 1) * 4)) & 15 : 0;
            }
        }
        ig_rgb_t color = ig_effect_pixel(selected, base, led, g_led_config.point[led].x, g_led_config.point[led].y, (g_led_config.flags[led] & LED_FLAG_UNDERGLOW) != 0, effect_frame_time, effect_hits, effect_hit_count);
        if (active_u16(4) >= 3 && selected[0] == 2 && selected[5] && (selected[3] & ((g_led_config.flags[led] & LED_FLAG_UNDERGLOW) ? 2 : 1))) {
            uint16_t palette = IG_SEQUENCE_OFFSET + (selected[5] - 1) * 25;
            uint8_t colors[24];
            for (uint8_t i = 0; i < sizeof(colors); ++i) colors[i] = active_byte(palette + 1 + i);
            uint32_t period = 10000UL - (uint32_t)selected[1] * 9600UL / 255;
            uint8_t phase = (effect_frame_time % period) * 256UL / period;
            color = ig_effect_sequence(phase, colors, active_byte(palette));
            color.r = ig_effect_scale(color.r, selected[2]); color.g = ig_effect_scale(color.g, selected[2]); color.b = ig_effect_scale(color.b, selected[2]);
        }
        uint8_t scale = lighting_suspended ? 0 : effect_frame_brightness;
        lighting_output(led, ig_effect_scale(color.r, scale), ig_effect_scale(color.g, scale), ig_effect_scale(color.b, scale));
    }
    return false;
}
#ifdef IG_HAS_SLIDER
static uint32_t slider_timer;
static int16_t slider_last = -1;
static uint8_t slider_last_mode = 255;
static uint32_t slider_last_generation;
static int16_t slider_samples[3], slider_activity_position;
static uint8_t slider_sample_index, slider_sample_count;
static int8_t slider_activity_direction;
static void slider_reset(void) {
    slider_last = -1; slider_last_mode = 255;
    slider_sample_index = slider_sample_count = 0;
    slider_activity_direction = 0;
    slider_timer = timer_read32();
}
static int16_t slider_median(void) {
    int16_t a = slider_samples[0], b = slider_samples[1], c = slider_samples[2], swap;
    if (a > b) { swap = a; a = b; b = swap; }
    if (b > c) { swap = b; b = c; c = swap; }
    if (a > b) b = a;
    return b;
}
static void slider_task(void) {
    if (timer_elapsed32(slider_timer) < 20) return;
    slider_timer = timer_read32();
    uint8_t mode = active_byte(16);
    if (mode != slider_last_mode || active_generation != slider_last_generation) { slider_reset(); slider_last_mode = mode; slider_last_generation = active_generation; }
    slider_samples[slider_sample_index] = analogReadPin(SLIDER_PIN);
    slider_sample_index = (slider_sample_index + 1) % 3;
    if (slider_sample_count < 3) slider_sample_count++;
    if (slider_sample_count < 3) return; // Prime with three real samples, never a possibly noisy first sample.
    int32_t sample = slider_median(), low = active_u16(18), high = active_u16(20);
    int16_t position = (sample - low) * 127 / (high - low);
    if (position < 0) position = 0;
    if (position > 127) position = 127;
    if (slider_last < 0) { slider_last = slider_activity_position = position; if (mode == 3) brightness = position * 255 / 127; return; }
    // Wake uses its own anchor: ordinary two-step actions must not consume a
    // slow movement before it can cross the three-step activity threshold.
    int16_t movement = position - slider_activity_position;
    int8_t direction = movement >= 3 ? 1 : movement <= -3 ? -1 : 0;
    if (direction && direction == slider_activity_direction) {
        slider_activity_position = position;
        slider_activity_direction = 0;
        lighting_wake();
    } else slider_activity_direction = direction;
    int16_t delta = position - slider_last;
    if (delta > -2 && delta < 2) return;
    // Slider actions never interrupt an explicit test. A measurement is not a stop request.
    if (mode == 1) midi_send_cc(&midi_device, active_byte(22), active_byte(23), position);
    else if (mode == 2) tap_code(delta > 0 ? KC_VOLU : KC_VOLD);
    else if (mode == 3) brightness = position * 255 / 127;
    slider_last = position;
}
#endif
void housekeeping_task_user(void) {
    (void)light_test_remaining(timer_read32()); // Stops even if rendering is idle or the host disappears.
    if (lighting_suspended) return;
    if (any_keys_pressed()) lighting_activity(); // A held physical key is not inactivity.
    macro_task();
#ifdef IG_HAS_SLIDER
    slider_task();
#endif
    bool was_pending = ig_timing_reconnect_pending();
    ig_timing_task(!stage_open && !macro_running && !any_keys_pressed());
    if (was_pending && !ig_timing_reconnect_pending()) effect_hit_count = 0;
}
void keyboard_pre_init_user(void) {
#ifdef IG_EXTERNAL_SPI_CS
    gpio_write_pin_high(IG_EXTERNAL_SPI_CS); gpio_set_pin_output(IG_EXTERNAL_SPI_CS); // Leave external stock SPI flash inactive.
#endif
    // QMK eeprom_driver_init ran before this hook; USB descriptors are requested later.
    storage_ok = wear_leveling_init() != WEAR_LEVELING_FAILED;
    init_runtime();
    ig_timing_init(saved_interval(), saved_debounce());
}
static bool recovery_chord_held(void) {
    // Fixed physical positions remain usable after arbitrary key remapping.
    for (uint8_t row = 0; row < MATRIX_ROWS; ++row) {
        matrix_row_t expected = 0;
        if (row == IG_RECOVERY_KEY1_ROW) expected |= (matrix_row_t)1U << IG_RECOVERY_KEY1_COL;
        if (row == IG_RECOVERY_KEY2_ROW) expected |= (matrix_row_t)1U << IG_RECOVERY_KEY2_COL;
        if (matrix_get_row(row) != expected) return false;
    }
    return true;
}
static void recovery_startup(void) {
    // Startup only: debounce, require exactly the chord, then hold for 500 ms.
    // No EEPROM reset and no remotely accessible boot command.
    matrix_scan(); wait_ms(50); matrix_scan(); // Covers every supported debounce setting (0..40 ms).
    if (!recovery_chord_held()) return;
    for (uint8_t sample = 0; sample < 50; ++sample) {
        wait_ms(10); matrix_scan();
        if (!recovery_chord_held()) return;
    }
    bootloader_jump();
}
void keyboard_post_init_user(void) {
    recovery_startup();
    lighting_suspended = false;
    memset(held_layer_keys, 0, sizeof(held_layer_keys));
    effect_hit_count = 0;
    storage_ok = wear_leveling_init() != WEAR_LEVELING_FAILED;
    init_runtime();
    ig_timing_update(saved_interval(), saved_debounce());
    rgb_matrix_enable_noeeprom();
    rgb_matrix_mode_noeeprom(RGB_MATRIX_SOLID_COLOR);
}
void suspend_power_down_user(void) { lighting_suspended = true; light_test_active = false; cancel_macro(); memset(held_layer_keys, 0, sizeof(held_layer_keys)); effect_hit_count = 0; }
void suspend_wakeup_init_user(void) { lighting_suspended = false; lighting_activity(); effect_hit_count = 0; }
