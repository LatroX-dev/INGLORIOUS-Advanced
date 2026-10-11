// SPDX-License-Identifier: GPL-2.0-or-later
#include QMK_KEYBOARD_H
#include "ig_timing.h"
#include "debounce.h"
#include "timer.h"
#include "action.h"
#include "usb_main.h"
#include <string.h>

#if !defined(PROTOCOL_CHIBIOS) || !defined(IG_DYNAMIC_USB_INTERVAL)
#    error "INGLORIOUS timing requires ChibiOS and the guarded dynamic descriptor patch"
#endif
#ifdef SPLIT_KEYBOARD
#    error "INGLORIOUS timing is scoped to the non-split PRO and Numpad"
#endif

static uint8_t active_interval = 1;
static uint8_t saved_interval = 1;
static uint8_t debounce_delay = 5;
static bool reconnect_pending;
static uint32_t reconnect_requested_at;
static bool debounce_pending;
static bool debounce_restart;
static uint32_t debounce_changed_at;

bool ig_timing_valid(uint8_t interval_ms, uint8_t debounce_ms) {
    return (interval_ms == 1 || interval_ms == 2 || interval_ms == 4 || interval_ms == 8) && debounce_ms <= 40;
}

void ig_timing_init(uint8_t interval_ms, uint8_t debounce_ms) {
    if (!ig_timing_valid(interval_ms, debounce_ms)) { interval_ms = 1; debounce_ms = 5; }
    active_interval = saved_interval = interval_ms;
    debounce_delay = debounce_ms;
    reconnect_pending = false;
    debounce_pending = false;
    debounce_restart = true;
}

bool ig_timing_update(uint8_t interval_ms, uint8_t debounce_ms) {
    if (!ig_timing_valid(interval_ms, debounce_ms)) return false;
    saved_interval = interval_ms;
    if (debounce_delay != debounce_ms) { debounce_delay = debounce_ms; debounce_restart = true; }
    // A newly verified configuration requires its own readback and RAW8 acknowledgment.
    reconnect_pending = false;
    return true;
}

uint8_t ig_timing_active_interval(void) { return active_interval; }
uint8_t ig_timing_saved_interval(void) { return saved_interval; }
uint8_t ig_timing_debounce_ms(void) { return debounce_delay; }
bool ig_timing_reconnect_pending(void) { return reconnect_pending; }

bool ig_timing_schedule_reconnect(void) {
    if (active_interval == saved_interval) return false;
    if (!reconnect_pending) { reconnect_requested_at = timer_read32(); reconnect_pending = true; }
    return true;
}

void ig_timing_task(bool safe) {
    if (!reconnect_pending || !safe || USB_DRIVER.state != USB_ACTIVE || (uint32_t)(timer_read32() - reconnect_requested_at) < 100) return;
    reconnect_pending = false;
    if (active_interval == saved_interval) return;
    clear_keyboard();
    // Disconnect before changing the descriptor selection. The existing QMK restart
    // stops/reinitializes endpoints and leaves the bus disconnected for another 50 ms.
    usbDisconnectBus(&USB_DRIVER);
    active_interval = saved_interval;
    restart_usb_driver(&USB_DRIVER);
}

// Same global deferred policy as QMK sym_defer_g, with a bounded runtime delay.
// Changing the delay starts a fresh settling interval; zero explicitly bypasses it.
void debounce_init(void) { debounce_pending = false; debounce_restart = true; }

bool debounce(matrix_row_t raw[], matrix_row_t cooked[], bool changed) {
    const size_t size = MATRIX_ROWS * sizeof(matrix_row_t);
    const bool different = memcmp(raw, cooked, size) != 0;
    if (!debounce_delay) {
        debounce_pending = false; debounce_restart = false;
        if (different) memcpy(cooked, raw, size);
        return different;
    }
    if (debounce_restart) {
        debounce_restart = false;
        debounce_pending = different;
        debounce_changed_at = timer_read32();
    }
    if (changed || (!debounce_pending && different)) {
        debounce_pending = true;
        debounce_changed_at = timer_read32();
    }
    if (debounce_pending && (uint32_t)(timer_read32() - debounce_changed_at) >= debounce_delay) {
        debounce_pending = false;
        if (different) memcpy(cooked, raw, size);
        return different;
    }
    return false;
}
