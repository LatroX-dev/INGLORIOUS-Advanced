// SPDX-License-Identifier: GPL-2.0-or-later
#pragma once
#include <stdbool.h>
#include <stdint.h>

// Schema 1 or absent settings: 1 ms USB interval, 5 ms global deferred debounce.
bool ig_timing_valid(uint8_t interval_ms, uint8_t debounce_ms);
// Boot only, after persistent config validation and before protocol_pre_init starts USB.
void ig_timing_init(uint8_t interval_ms, uint8_t debounce_ms);
// After a verified config reload. Never disconnects USB; invalid values leave state intact.
bool ig_timing_update(uint8_t interval_ms, uint8_t debounce_ms);
uint8_t ig_timing_active_interval(void);
uint8_t ig_timing_saved_interval(void);
uint8_t ig_timing_debounce_ms(void);
bool ig_timing_reconnect_pending(void);
bool ig_timing_usb_active(void);
void ig_timing_cancel_reconnect(void);
// After RAW8 acknowledgment, or the board16 runtime's bound idle activation plan.
// Repeated calls are idempotent and never restart the pending delay.
bool ig_timing_schedule_reconnect(void);
// Housekeeping only. safe requires no keys, macros, or staging transaction in progress.
// A busy or suspended device defers the pending reconnect without discarding it.
void ig_timing_task(bool safe);
