// SPDX-License-Identifier: GPL-2.0-or-later
// Included only by an explicitly enabled supported build. The host still owns
// backup/target verification and all writes through the existing bootloader.
// A nonce correlates one app request; it is not host authentication.
#define IG_APP_BOOTLOADER_LEASE_MS 5000U
#define IG_APP_BOOTLOADER_DELAY_MS 150U
#define IG_APP_BOOTLOADER_COMMIT_MS 1000U
static uint8_t app_bootloader_state; // 0=idle, 1=prepared, 2=acknowledged commit.
static uint8_t app_bootloader_nonce[8];
static uint32_t app_bootloader_prepared_at, app_bootloader_committed_at;
static uint32_t app_bootloader_generation, app_bootloader_crc;
static uint16_t app_bootloader_size;
#if IG_HAS_OS_SWITCH
static bool app_bootloader_mac;
#endif
#if IG_PROFILE_BANK_ENABLE
static uint8_t app_bootloader_profile;
static uint64_t app_bootloader_profile_generation;
#endif

static void app_bootloader_cancel(void) {
    app_bootloader_state = 0;
    // Keep the last nonce so a delayed packet cannot reopen the retired request.
}
static bool app_bootloader_safe(void) {
    if (!runtime_ready || !storage_ok || stage_open || macro_running || any_keys_pressed() ||
        lighting_suspended || light_test_remaining(timer_read32()) ||
        diagnostic_remaining(timer_read32()) || ig_timing_reconnect_pending() || !ig_timing_usb_active()) return false;
#if IG_HAS_ENCODER
    if (knob_pending) return false;
#endif
#if IG_HAS_OS_SWITCH
    // Read only the fixed, reviewed OS switch. A change since housekeeping must
    // cancel entry before a RAW commit can race the next hardware-state sample.
    if (ig_os_read_mac() != ig_os_raw_mac || ig_os_raw_mac != ig_os_effective_mac ||
        (uint32_t)(timer_read32() - ig_os_changed_at) < IG_OS_SWITCH_DEBOUNCE_MS) return false;
#endif
#if IG_PROFILE_BANK_ENABLE
    if (!ig_profile_healthy || ig_profile_refresh_pending || ig_profile_token || ig_profile_target ||
        ig_profile_interval_state || !ig_profile_active || ig_profile_active > 3 ||
        !ig_profile_valid[ig_profile_active - 1]) return false;
    // Existing cooked rows only: no extra matrix scan and no configurable GPIO.
    uint8_t physical = ig_profile_selector_decode(matrix_get_row(11), matrix_get_row(12), matrix_get_row(13));
    if (physical != ig_profile_active || ig_profile_selector.candidate != physical ||
        ig_profile_selector.requested != physical ||
        (uint32_t)(timer_read32() - ig_profile_selector.changed_at) < IG_PROFILE_SELECTOR_DEBOUNCE_MS) return false;
#endif
    for (uint8_t i = 0; i < sizeof(macro_held); ++i) if (macro_held[i]) return false;
    for (uint8_t row = 0; row < MATRIX_ROWS; ++row) if (held_layer_keys[row]) return false;
    return true;
}
static bool app_bootloader_snapshot_matches(void) {
    if (active_generation != app_bootloader_generation || active_crc != app_bootloader_crc || active_size != app_bootloader_size) return false;
#if IG_HAS_OS_SWITCH
    if (ig_os_effective_mac != app_bootloader_mac) return false;
#endif
#if IG_PROFILE_BANK_ENABLE
    if (ig_profile_active != app_bootloader_profile ||
        ig_profile_views[ig_profile_active - 1].generation != app_bootloader_profile_generation ||
        ig_profile_crcs[ig_profile_active - 1] != app_bootloader_crc ||
        ig_profile_views[ig_profile_active - 1].length != app_bootloader_size) return false;
#endif
    return true;
}
static void app_bootloader_expire(uint32_t now) {
    bool timed_out = app_bootloader_state == 1 ? (uint32_t)(now - app_bootloader_prepared_at) >= IG_APP_BOOTLOADER_LEASE_MS :
                     (uint32_t)(now - app_bootloader_committed_at) >= IG_APP_BOOTLOADER_COMMIT_MS;
    if (app_bootloader_state && (timed_out ||
        !app_bootloader_safe() || !app_bootloader_snapshot_matches())) app_bootloader_cancel();
}
static bool app_bootloader_blocks(uint8_t command) {
    if (!app_bootloader_state) return false;
    // Reads may continue, but no transfer/test/brightness/USB action can start
    // between preparation and the jump. Unknown commands keep their normal error.
    return (command >= 3 && command <= 6) || command == 8 || command == 10 ||
           command == 12 || command == 14 || command == 15 || (command >= 20 && command <= 23);
}
static uint8_t app_bootloader_command(uint8_t command, const uint8_t *p, uint8_t count, uint8_t *out, uint8_t *size) {
    if (count != 24) return 1;
    uint8_t nonce_nonzero = 0;
    for (uint8_t i = 16; i < 24; ++i) nonce_nonzero |= p[i];
    if (!nonce_nonzero) return 1;
    if (memcmp(p, ig_fw_identity, 16)) return 8;
    bool same_nonce = !memcmp(p + 16, app_bootloader_nonce, 8);
    if (command == 27) {
        if (!app_bootloader_state || !same_nonce) return 8;
        if (app_bootloader_state != 1) return 3;
        app_bootloader_cancel();
    } else {
        if (!storage_ok) return 7;
        if (!app_bootloader_safe()) return 3;
        if (command == 25) {
            if (app_bootloader_state) {
                if (app_bootloader_state != 1 || !same_nonce) return 3;
                // An identical prepare can be acknowledged again, but never
                // renews its deadline or replaces its configuration snapshot.
            } else {
                if (same_nonce) return 8;
                memcpy(app_bootloader_nonce, p + 16, 8);
                app_bootloader_prepared_at = timer_read32();
                app_bootloader_generation = active_generation;
                app_bootloader_crc = active_crc;
                app_bootloader_size = active_size;
#if IG_HAS_OS_SWITCH
                app_bootloader_mac = ig_os_effective_mac;
#endif
#if IG_PROFILE_BANK_ENABLE
                app_bootloader_profile = ig_profile_active;
                app_bootloader_profile_generation = ig_profile_views[ig_profile_active - 1].generation;
#endif
                app_bootloader_state = 1;
            }
        } else if (app_bootloader_state != 1 || !same_nonce || !app_bootloader_snapshot_matches()) return 8;
    }
    memcpy(out, p, 24); *size = 24;
    return 0;
}
static void app_bootloader_schedule(void) {
    app_bootloader_state = 2;
    app_bootloader_committed_at = timer_read32();
}
static void app_bootloader_task(uint32_t now) {
    app_bootloader_expire(now);
    if (app_bootloader_state != 2 || (uint32_t)(now - app_bootloader_committed_at) < IG_APP_BOOTLOADER_DELAY_MS) return;
    app_bootloader_cancel(); // Exactly one attempt, including a returning test stub.
    bootloader_jump();
}
