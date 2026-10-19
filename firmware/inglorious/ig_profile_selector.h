// SPDX-License-Identifier: GPL-2.0-or-later
#pragma once

#include <stdint.h>

// Only board 16 has a confirmed three-position mapping. The caller supplies
// already-sampled rows; this module never scans the matrix or accesses GPIO.
#if defined(IG_BOARD) && IG_BOARD == 16
#    define IG_PROFILE_SELECTOR_SUPPORTED 1
#else
#    define IG_PROFILE_SELECTOR_SUPPORTED 0
#endif

#define IG_PROFILE_SELECTOR_DEBOUNCE_MS 30U
#define IG_PROFILE_SELECTOR_UNKNOWN 0U

typedef struct {
    uint32_t changed_at;
    uint8_t candidate;
    uint8_t requested;
} ig_profile_selector_state_t;

static inline uint8_t ig_profile_selector_decode(uint16_t row11, uint16_t row12, uint16_t row13) {
#if IG_PROFILE_SELECTOR_SUPPORTED
    const uint8_t contacts = (uint8_t)(((row11 >> 7) & 1U) |
                                     (((row12 >> 7) & 1U) << 1) |
                                     (((row13 >> 7) & 1U) << 2));
    switch (contacts) {
        case 1: return 1;
        case 2: return 2;
        case 4: return 3;
        default: return IG_PROFILE_SELECTOR_UNKNOWN;
    }
#else
    (void)row11; (void)row12; (void)row13;
    return IG_PROFILE_SELECTOR_UNKNOWN;
#endif
}

static inline void ig_profile_selector_init(ig_profile_selector_state_t *state, uint32_t now) {
    state->changed_at = now;
    state->candidate = IG_PROFILE_SELECTOR_UNKNOWN;
    state->requested = IG_PROFILE_SELECTOR_UNKNOWN;
}

// A transition invalidates the previous request immediately. A complete stable
// interval is required even when contacts bounce back to the previous position.
// Zero or multiple contacts never select a fallback profile.
static inline uint8_t ig_profile_selector_sample(ig_profile_selector_state_t *state,
                                                uint16_t row11, uint16_t row12, uint16_t row13,
                                                uint32_t now) {
    const uint8_t candidate = ig_profile_selector_decode(row11, row12, row13);
    if (candidate != state->candidate) {
        state->candidate = candidate;
        state->changed_at = now;
        state->requested = IG_PROFILE_SELECTOR_UNKNOWN;
    }
    if (!candidate)
        state->requested = IG_PROFILE_SELECTOR_UNKNOWN;
    else if ((uint32_t)(now - state->changed_at) >= IG_PROFILE_SELECTOR_DEBOUNCE_MS)
        state->requested = candidate;
    return state->requested;
}

// This is a requested position, not an active profile. The caller must keep
// its active profile separate and apply a valid request only when switching is
// safe for held keys, macros, transfers and diagnostics. Nothing is applied here.
static inline uint8_t ig_profile_selector_requested(const ig_profile_selector_state_t *state) {
    return state->requested;
}
