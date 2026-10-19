// SPDX-License-Identifier: GPL-2.0-or-later
// Included by the actual runtime after validation/storage helpers. Only board16.
#if IG_PROFILE_BANK_ENABLE
#include "ig_profile_selector.h"
#include "ig_profile_defaults.h"
_Static_assert(sizeof(ig_profile_defaults) == IG_SIZE, "Exact immutable fallback payload");
_Static_assert(IG_PRESS_SIZE <= IG_PB_PAYLOAD_CAPACITY, "All schemas fit a bank record");
static ig_profile_selector_state_t ig_profile_selector;
static ig_pb_bank ig_profile_bank;
static ig_pb_view ig_profile_views[3];
static uint32_t ig_profile_crcs[3];
static bool ig_profile_valid[3], ig_profile_healthy, ig_profile_refresh_pending;
static uint8_t ig_profile_active, ig_profile_target;
static uint32_t ig_profile_token, ig_profile_expected_crc;
// A crashed/disconnected host must not pin the borrowed RAM indefinitely.
#define IG_PROFILE_UPLOAD_LEASE_MS 30000U
static uint32_t ig_profile_upload_activity;
static uint8_t ig_profile_interval_state; // 0=none, 1=one deferred attempt, 2=scheduled.
static uint8_t ig_profile_interval_slot, ig_profile_interval_value;
static uint64_t ig_profile_interval_generation;
static uint32_t ig_profile_interval_crc;
static bool ig_profile_interval_automatic, ig_profile_read_lease;
static uint32_t ig_profile_interval_activated_at, ig_profile_read_activity;
#define IG_PROFILE_USB_GRACE_MS 5000U
static uint64_t ig_profile_expected_generation;
static const uint8_t *ig_profile_legacy;
static uint16_t ig_profile_legacy_size;
static int8_t ig_profile_legacy_slot;
static bool ig_profile_release_upload(void);
static void ig_profile_expire_upload(uint32_t now) {
    if (stage_open && ig_profile_token &&
        (uint32_t)(now - ig_profile_upload_activity) >= IG_PROFILE_UPLOAD_LEASE_MS)
        (void)ig_profile_release_upload(); // Strictly read-only replay; never commits.
}
static uint64_t ig_profile_u64(const uint8_t *p) { return (uint64_t)u32(p) | ((uint64_t)u32(p + 4) << 32); }
static void ig_profile_put64(uint8_t *p, uint64_t value) { put32(p, (uint32_t)value); put32(p + 4, (uint32_t)(value >> 32)); }
static bool ig_profile_validate(void *context, const uint8_t *payload, uint32_t length, uint16_t schema) {
    (void)context;
    return length <= UINT16_MAX && config_length_allowed((uint16_t)length) &&
           u16(payload + 4) == schema && validate_config(payload, (uint16_t)length);
}
static bool ig_profile_fallback(void *context, ig_pb_fallback *view) {
    (void)context;
    view->payload = ig_profile_legacy; view->length = ig_profile_legacy_size;
    view->schema = ig_profile_legacy ? u16(ig_profile_legacy + 4) : 0;
    return ig_profile_legacy != NULL;
}
static void ig_profile_refresh(void) {
    ig_pb_scan scan;
    ig_profile_healthy = ig_profile_flash_ready() && ig_pb_scan_bank(&ig_profile_bank, &scan) == IG_PB_OK && scan.write_status == IG_PB_OK;
    for (uint8_t slot = 0; slot < 3; ++slot) {
        ig_profile_valid[slot] = ig_pb_read_profile(&ig_profile_bank, slot + 1, &ig_profile_views[slot]) == IG_PB_OK;
        ig_profile_crcs[slot] = ig_profile_valid[slot] ? ig_pb_crc32(ig_profile_views[slot].payload, ig_profile_views[slot].length) : 0;
    }
}
static void ig_profile_initialize(void) {
    ig_profile_legacy_slot = active_slot;
    ig_profile_legacy_size = active_size;
    ig_profile_legacy = active_slot >= 0 ? ig_storage_view(slot_base(active_slot) + IG_SLOT_HEADER, active_size) : ig_profile_defaults;
    ig_profile_payload = ig_profile_legacy;
    ig_profile_bank = (ig_pb_bank){ig_profile_flash_bytes(), IG_PB_BANK_SIZE, IG_BOARD,
                                 ig_profile_validate, NULL, ig_profile_fallback, NULL};
    ig_profile_active = ig_profile_target = 0; ig_profile_token = 0; ig_profile_interval_state = 0; ig_profile_read_lease = false;
    ig_profile_refresh_pending = false;
    ig_profile_selector_init(&ig_profile_selector, timer_read32());
    ig_profile_refresh();
}
static bool ig_profile_execution_idle(void) {
    return !lighting_suspended && !macro_running && !any_keys_pressed() &&
           !light_test_remaining(timer_read32()) && !diagnostic_remaining(timer_read32()) && !knob_pending;
}
static bool ig_profile_idle(bool own_upload) {
    return ig_profile_execution_idle() && (own_upload || !stage_open) && !ig_timing_reconnect_pending();
}
static void ig_profile_interval_bind(uint8_t state, bool automatic) {
    ig_profile_interval_slot = ig_profile_active;
    ig_profile_interval_generation = ig_profile_views[ig_profile_active - 1].generation;
    ig_profile_interval_crc = ig_profile_crcs[ig_profile_active - 1];
    ig_profile_interval_value = ig_timing_saved_interval();
    ig_profile_interval_automatic = automatic;
    ig_profile_interval_activated_at = timer_read32();
    ig_profile_interval_state = ig_timing_active_interval() != ig_profile_interval_value ? state : 0;
}
static bool ig_profile_interval_matches(uint8_t requested) {
    uint8_t slot = ig_profile_interval_slot;
    return storage_ok && ig_profile_healthy && slot && slot == requested && slot == ig_profile_active && !ig_profile_refresh_pending &&
           ig_profile_valid[slot - 1] && ig_profile_views[slot - 1].generation == ig_profile_interval_generation &&
           ig_profile_crcs[slot - 1] == ig_profile_interval_crc && active_crc == ig_profile_interval_crc &&
           ig_timing_saved_interval() == ig_profile_interval_value;
}
static void ig_profile_interval_cancel(void) {
    if (ig_profile_interval_state == 2) ig_timing_cancel_reconnect();
    ig_profile_interval_state = 0; // No retry until a new actual profile activation or explicit RAW8.
}
static bool ig_profile_read_busy(void) {
    if (ig_profile_read_lease && (uint32_t)(timer_read32() - ig_profile_read_activity) >= IG_PROFILE_USB_GRACE_MS)
        ig_profile_read_lease = false;
    return ig_profile_read_lease;
}
static void ig_profile_interval_task(uint8_t requested, bool may_schedule) {
    if (!ig_profile_interval_state) return;
    if (!ig_profile_interval_matches(requested)) { ig_profile_interval_cancel(); return; }
    if (ig_profile_interval_state == 2) {
        if (!ig_timing_reconnect_pending()) ig_profile_interval_state = 0;
    } else if (may_schedule && (uint32_t)(timer_read32() - ig_profile_interval_activated_at) >= IG_PROFILE_USB_GRACE_MS &&
               !ig_profile_read_busy() && !stage_open && ig_profile_execution_idle() && ig_timing_usb_active()) {
        ig_profile_interval_state = ig_timing_schedule_reconnect() ? 2 : 0;
    }
}
static bool ig_profile_reconnect_safe(void) {
    if (!ig_timing_reconnect_pending()) return false;
    uint8_t requested = ig_profile_selector_sample(&ig_profile_selector,
        matrix_get_row(11), matrix_get_row(12), matrix_get_row(13), timer_read32());
    if (ig_profile_interval_state != 2 || !ig_profile_interval_matches(requested)) {
        ig_timing_cancel_reconnect(); ig_profile_interval_state = 0; return false;
    }
    return (!ig_profile_interval_automatic || !ig_profile_read_busy()) && !stage_open && ig_profile_execution_idle() && ig_timing_usb_active();
}
static void ig_profile_apply(uint8_t slot, bool automatic_interval) {
    const ig_pb_view *view = &ig_profile_views[slot - 1];
    ig_profile_payload = view->payload; ig_profile_active = slot;
    active_size = (uint16_t)view->length; active_crc = ig_profile_crcs[slot - 1];
    active_generation = (uint32_t)view->generation; // Legacy INFO is read-only; explicit API uses full64bits.
    layer_clear(); default_layer_state = 0;
    memset(held_layer_keys, 0, sizeof(held_layer_keys));
    memset(macro_held, 0, sizeof(macro_held));
    knob_reset(); brightness = 255; effect_hit_count = 0; lighting_activity();
    ig_timing_update(saved_interval(), saved_debounce());
    ig_profile_interval_bind(automatic_interval ? 1 : 0, automatic_interval);
}
static void ig_profile_task(uint32_t now) {
    ig_profile_expire_upload(now);
    uint8_t requested = ig_profile_selector_sample(&ig_profile_selector, matrix_get_row(11), matrix_get_row(12), matrix_get_row(13), now);
    ig_profile_interval_task(requested, false);
    if (!ig_profile_idle(false)) return;
    if (ig_profile_refresh_pending && ig_profile_active && ig_profile_valid[ig_profile_active - 1]) {
        ig_profile_apply(ig_profile_active, false); ig_profile_refresh_pending = false; // COMMIT readback precedes explicit RAW8.
    }
    if (requested && requested != ig_profile_active && ig_profile_valid[requested - 1]) ig_profile_apply(requested, true);
    ig_profile_interval_task(requested, true);
}
static bool ig_profile_release_upload(void) {
    bool ok = ig_storage_stage_finish(false) != WEAR_LEVELING_FAILED;
    stage_open = false; stage_received = stage_size = 0; staging = NULL;
    ig_profile_target = 0; ig_profile_token = 0;
    if (!ok) storage_ok = false;
    return ok;
}
static uint8_t ig_profile_reconnect(const uint8_t *p, uint8_t count) {
    if (count != 16 || p[1] || p[2] || p[3]) return 1;
    uint8_t slot = p[0];
    if (slot < 1 || slot > 3) return 2;
    ig_profile_expire_upload(timer_read32());
    if (!storage_ok || !ig_profile_healthy || !ig_profile_valid[slot - 1]) return 7;
    if (slot != ig_profile_active || ig_profile_views[slot - 1].generation != ig_profile_u64(p + 4) ||
        ig_profile_crcs[slot - 1] != u32(p + 12)) return 8;
    if (ig_profile_interval_automatic && ig_profile_execution_idle() && !stage_open) ig_profile_interval_cancel();
    if (ig_profile_refresh_pending || !ig_profile_idle(false)) return 3;
    // RAW is processed before housekeeping. Sample only the existing cooked
    // matrix here, so a move since the last status cannot restart another slot.
    uint8_t requested = ig_profile_selector_sample(&ig_profile_selector,
        matrix_get_row(11), matrix_get_row(12), matrix_get_row(13), timer_read32());
    if (requested != slot || ig_profile_selector.candidate != slot) return 3;
    ig_profile_interval_bind(2, false); // The caller acknowledges before scheduling the USB restart.
    return 0;
}
static uint8_t ig_profile_command(uint8_t command, const uint8_t *p, uint8_t count, uint8_t *out, uint8_t *size) {
    // RAW runs before housekeeping in the QMK loop. Reject an expired owner even
    // when its first returning packet arrives before the next housekeeping pass.
    ig_profile_expire_upload(timer_read32());
    switch (command) {
        case 18: {
            if (count != 1) return 1;
            if (p[0] > 3) return 2;
            uint8_t slot = p[0] ? p[0] : ig_profile_active;
            if (!slot) return 3;
            if (!ig_profile_valid[slot - 1]) return 7;
            const ig_pb_view *view = &ig_profile_views[slot - 1];
            out[0] = 1; out[1] = IG_BOARD; out[2] = 3; out[3] = ig_profile_selector.candidate; out[4] = ig_profile_active;
            out[5] = (storage_ok && ig_profile_healthy ? 1 : 0) |
                     (view->source != IG_PB_SOURCE_RECORD ? 2 : 0) |
                     (ig_profile_selector.candidate != ig_profile_active || ig_profile_refresh_pending ? 4 : 0) |
                     (stage_open ? 8 : 0) | (ig_os_raw_mac ? 16 : 0) | (ig_os_effective_mac ? 32 : 0) |
                     (ig_os_raw_mac != ig_os_effective_mac ? 64 : 0) |
                     (view->pair_state == IG_PB_PAIR_RECOVERED || view->source == IG_PB_SOURCE_DEGRADED_LEGACY ? 128 : 0);
            out[6] = slot; ig_profile_put64(out + 8, view->generation); put32(out + 16, ig_profile_crcs[slot - 1]);
            put16(out + 20, (uint16_t)view->length); put16(out + 22, view->schema); *size = 24; return 0;
        }
        case 19: {
            if (count != 16) return 1;
            uint8_t slot = p[0], n = p[1]; uint16_t offset = u16(p + 2);
            if (slot < 1 || slot > 3 || !n || n > 12) return 2;
            if (!ig_profile_valid[slot - 1]) return 7;
            const ig_pb_view *view = &ig_profile_views[slot - 1];
            if (view->generation != ig_profile_u64(p + 4) || ig_profile_crcs[slot - 1] != u32(p + 12)) return 8;
            if ((uint32_t)offset + n > view->length) return 2;
            ig_profile_read_lease = true; ig_profile_read_activity = timer_read32();
            memcpy(out, p, 12); memcpy(out + 12, view->payload + offset, n); *size = 12 + n; return 0;
        }
        case 20: {
            if (count != 24 || p[1]) return 1;
            uint8_t slot = p[0]; uint16_t length = u16(p + 2);
            if (slot < 1 || slot > 3 || !config_length_allowed(length) || !u32(p + 20)) return 2;
            if (stage_open || ig_profile_refresh_pending) return 3;
            if (!storage_ok || !ig_profile_healthy || !ig_profile_valid[slot - 1]) return 7;
            if (ig_profile_views[slot - 1].generation != ig_profile_u64(p + 4) || ig_profile_crcs[slot - 1] != u32(p + 12)) return 8;
            if (ig_profile_interval_automatic && ig_profile_execution_idle()) ig_profile_interval_cancel();
            if (!ig_profile_idle(false)) return 3;
            staging = ig_storage_stage_begin(slot_base(ig_profile_legacy_slot == 0 ? 1 : 0) + IG_SLOT_HEADER, length);
            if (!staging) { storage_ok = false; return 7; }
            stage_open = true; stage_size = length; stage_received = 0; stage_crc = u32(p + 16);
            ig_profile_target = slot; ig_profile_token = u32(p + 20); ig_profile_expected_generation = ig_profile_u64(p + 4); ig_profile_expected_crc = u32(p + 12);
            ig_profile_upload_activity = timer_read32();
            memcpy(out, p, 24); *size = 24; return 0;
        }
        case 21: {
            if (count < 9 || count != 8U + p[5]) return 1;
            if (!stage_open) return 4;
            if (u32(p) != ig_profile_token || p[4] != ig_profile_target) return 8;
            uint8_t n = p[5]; uint16_t offset = u16(p + 6);
            if (!n || n > 16 || (uint32_t)offset + n > stage_size || offset > stage_received) return 2;
            if (offset < stage_received) {
                if ((uint32_t)offset + n > stage_received || memcmp(staging + offset, p + 8, n)) return 2;
            } else { memcpy(staging + offset, p + 8, n); stage_received += n; ig_profile_upload_activity = timer_read32(); }
            put32(out, ig_profile_token); out[4] = ig_profile_target; put16(out + 6, stage_received); *size = 8; return 0;
        }
        case 22: {
            if (count != 20 || p[5] || p[6] || p[7]) return 1;
            if (!stage_open) return 4;
            if (u32(p) != ig_profile_token || p[4] != ig_profile_target || ig_profile_u64(p + 8) != ig_profile_expected_generation) return 8;
            if (!storage_ok || !ig_profile_healthy) return 7;
            if (stage_received != stage_size) return 2;
            if (u32(p + 16) != stage_crc || crc_buffer(staging, stage_size) != stage_crc) return 5;
            if (!validate_config(staging, stage_size)) return 6;
            if (!ig_profile_idle(true)) return 3;
            uint8_t slot = ig_profile_target; uint16_t length = stage_size; uint32_t crc = stage_crc;
            uint64_t generation = ig_profile_expected_generation;
            if (!ig_profile_valid[slot - 1] || ig_profile_views[slot - 1].generation != generation ||
                ig_profile_crcs[slot - 1] != ig_profile_expected_crc ||
                ig_pb_crc32(ig_profile_views[slot - 1].payload, ig_profile_views[slot - 1].length) != ig_profile_expected_crc) return 8;
            if (!ig_profile_flash_begin()) return 7;
            ig_pb_result result = ig_pb_save_profile(&ig_profile_bank, ig_profile_flash_io(), slot, generation, u16(staging + 4), staging, length);
            ig_profile_flash_end();
            bool replay = ig_profile_release_upload();
            ig_profile_refresh();
            if (!replay || result != IG_PB_OK || !ig_profile_valid[slot - 1] || ig_profile_views[slot - 1].generation != generation + 1 ||
                ig_profile_crcs[slot - 1] != crc || ig_profile_views[slot - 1].length != length) {
                storage_ok = false; return 7;
            }
            if (ig_profile_active == slot) ig_profile_refresh_pending = true;
            out[0] = slot; put16(out + 2, length); ig_profile_put64(out + 4, generation + 1); put32(out + 12, crc); *size = 16; return 0;
        }
        case 23:
            if (count != 8 || p[5] || p[6] || p[7]) return 1;
            if (!stage_open) return 4;
            if (u32(p) != ig_profile_token || p[4] != ig_profile_target) return 8;
            if (!ig_profile_release_upload()) return 7;
            memcpy(out, p, 8); *size = 8; return 0;
        case 24: {
            if (count) return 1;
            const uint8_t *identity = ig_profile_flash_identity();
            if (!identity || memcmp(identity, "IGIDENT1", 8) || identity[8] != 1 || identity[9] != IG_BOARD || identity[10] || identity[11] ||
                ig_pb_crc32(identity, 60) != u32(identity + 60)) return 7;
            uint8_t any = 0, all = 255;
            for (uint8_t i = 12; i < 28; ++i) { any |= identity[i]; all &= identity[i]; }
            if (!any || all == 255) return 7;
            out[0] = 1; out[1] = IG_BOARD; memcpy(out + 4, identity + 12, 16); *size = 20; return 0;
        }
        default: return 9;
    }
}
#endif
