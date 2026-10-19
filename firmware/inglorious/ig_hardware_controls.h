// SPDX-License-Identifier: GPL-2.0-or-later
#pragma once

// Board definitions are generated from the pinned manufacturer keymap and GPIO
// configuration. There is no host command to choose a pin or alter this state.
#ifndef IG_HAS_OS_SWITCH
#    define IG_HAS_OS_SWITCH 0
#endif

#if IG_HAS_OS_SWITCH
#define IG_OS_SWITCH_DEBOUNCE_MS 30U
typedef struct { uint8_t row, col; uint16_t windows, mac; } ig_os_binding_t;
static const ig_os_binding_t ig_os_bindings[] = IG_OS_BINDINGS;
static bool ig_os_raw_mac, ig_os_effective_mac;
static uint32_t ig_os_changed_at;

static bool ig_os_read_mac(void) { return gpio_read_pin(IG_OS_SWITCH_PIN) == IG_OS_MAC_LEVEL; }
static void ig_hardware_init(void) {
    gpio_set_pin_input_high(IG_OS_SWITCH_PIN);
    ig_os_raw_mac = ig_os_effective_mac = ig_os_read_mac();
    ig_os_changed_at = timer_read32();
}
static void ig_hardware_task(uint32_t now, bool idle) {
    bool mac = ig_os_read_mac();
    if (mac != ig_os_raw_mac) { ig_os_raw_mac = mac; ig_os_changed_at = now; }
    // Housekeeping runs after QMK processes matrix releases. Never change the
    // resolved modifier between its press and release, or during a macro.
    if (idle && (uint32_t)(now - ig_os_changed_at) >= IG_OS_SWITCH_DEBOUNCE_MS)
        ig_os_effective_mac = ig_os_raw_mac;
}
static uint16_t ig_hardware_keycode(uint8_t row, uint8_t col, uint16_t code) {
    if (ig_os_effective_mac)
        for (uint8_t i = 0; i < sizeof(ig_os_bindings) / sizeof(ig_os_bindings[0]); ++i) {
            const ig_os_binding_t *binding = &ig_os_bindings[i];
            if (row == binding->row && col == binding->col && code == binding->windows) return binding->mac;
        }
    return code;
}
static void ig_hardware_status(uint8_t *out) {
    out[0] = 1; out[1] = IG_BOARD; out[2] = 1;
    out[3] = ig_os_raw_mac; out[4] = ig_os_effective_mac;
    out[5] = ig_os_raw_mac != ig_os_effective_mac;
    out[6] = out[7] = 0;
}
#else
static void ig_hardware_init(void) {}
static uint16_t ig_hardware_keycode(uint8_t row, uint8_t col, uint16_t code) { (void)row; (void)col; return code; }
#endif
