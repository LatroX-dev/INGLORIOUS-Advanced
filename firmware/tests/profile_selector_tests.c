// SPDX-License-Identifier: GPL-2.0-or-later
// Standalone pure-state tests. No QMK runtime, USB, GPIO or storage dependencies.
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "../inglorious/ig_profile_selector.h"

static unsigned checks;
static void check(bool condition, const char *message) {
    ++checks;
    if (!condition) { fprintf(stderr, "FAIL profile selector: %s\n", message); exit(1); }
}

static uint8_t sample(ig_profile_selector_state_t *state, uint8_t contacts, uint32_t now) {
    uint8_t result = ig_profile_selector_sample(state, (contacts & 1U) ? 0x80U : 0,
                                              (contacts & 2U) ? 0x80U : 0,
                                              (contacts & 4U) ? 0x80U : 0, now);
    check(result == ig_profile_selector_requested(state), "sample and read-only getter agree");
    return result;
}

static uint8_t expected_position(uint8_t contacts) {
#if IG_PROFILE_SELECTOR_SUPPORTED
    static const uint8_t positions[8] = {0,1,2,0,3,0,0,0};
    return positions[contacts];
#else
    (void)contacts;
    return 0;
#endif
}

static void check_decoder(void) {
    // Exhaust every input value of each 16-bit row, with the other two contacts
    // independently on/off. This includes every physical and unused noisy bit.
    for (unsigned row = 0; row < 3; ++row) {
        for (unsigned others = 0; others < 4; ++others) {
            for (uint32_t value = 0; value <= UINT16_MAX; ++value) {
                uint16_t rows[3] = {0};
                unsigned other = 0;
                for (unsigned at = 0; at < 3; ++at)
                    rows[at] = at == row ? (uint16_t)value : ((others & (1U << other++)) ? 0x80U : 0);
                uint8_t contacts = (rows[0] & 0x80U ? 1U : 0) |
                                   (rows[1] & 0x80U ? 2U : 0) |
                                   (rows[2] & 0x80U ? 4U : 0);
                check(ig_profile_selector_decode(rows[0], rows[1], rows[2]) == expected_position(contacts),
                      "only exact one-hot at row11/12/13 col7 chooses a slot");
            }
        }
    }
}

static void check_startup_and_transitions(void) {
    for (uint8_t contacts = 0; contacts < 8; ++contacts) {
        ig_profile_selector_state_t state;
        memset(&state, 0xA5, sizeof(state));
        ig_profile_selector_init(&state, 800U);
        check(!state.candidate && !ig_profile_selector_requested(&state), "initialization always starts unknown");
        check(!sample(&state, contacts, 800U), "boot starts debounce without selecting profile1");
        check(!sample(&state, contacts, 829U), "no request before full debounce interval");
        check(sample(&state, contacts, 830U) == expected_position(contacts), "boot chooses its actual position after debounce, including profile3");
        check(sample(&state, contacts, 980U) == expected_position(contacts), "stable position stays requested until caller can apply");
    }
    for (uint8_t from = 0; from < 8; ++from) for (uint8_t to = 0; to < 8; ++to) {
        ig_profile_selector_state_t state;
        ig_profile_selector_init(&state, 0);
        sample(&state, from, 0); sample(&state, from, 30);
        uint8_t old_position = expected_position(from), next_position = expected_position(to);
        uint8_t immediate = sample(&state, to, 100);
        check(immediate == (old_position == next_position ? old_position : 0), "raw transition cancels a stale pending request immediately");
        check(sample(&state, to, 129) == (old_position == next_position ? old_position : 0), "transition waits through29ms");
        check(sample(&state, to, 130) == next_position, "all contact-pattern transitions settle to exact valid or unknown state");
    }
}

#if IG_PROFILE_SELECTOR_SUPPORTED
static void check_bounce_and_wrap(void) {
    ig_profile_selector_state_t state;
    ig_profile_selector_init(&state, 0);
    sample(&state, 1, 0); check(sample(&state, 1, 30) == 1, "baseline profile1 requested");
    check(!sample(&state, 2, 31), "new position clears pending profile1");
    check(!sample(&state, 0, 40), "break-before-make is unknown");
    check(!sample(&state, 3, 50), "make-before-break is unknown");
    check(!sample(&state, 1, 60), "bounce to old position still debounces");
    check(!sample(&state, 1, 89), "old position is not restored prematurely");
    check(!sample(&state, 4, 90), "third position restarts debounce");
    check(!sample(&state, 4, 119), "profile3 not stable at29ms");
    check(sample(&state, 4, 120) == 3, "profile3 stable at30ms");
    // Non-contact bits do not restart the timer or alter the selected slot.
    check(ig_profile_selector_sample(&state, 0xFF7F, 0xFF7F, 0xFFFF, 121) == 3,
          "unrelated raw row bits do not cause bounce");
    for (unsigned offset = 0; offset < 64; ++offset) {
        uint32_t start = UINT32_MAX - offset;
        ig_profile_selector_init(&state, start);
        check(!sample(&state, 4, start), "wrap test starts unconfirmed");
        check(!sample(&state, 4, start + 29U), "29ms remains unconfirmed across overflow");
        check(sample(&state, 4, start + 30U) == 3, "30ms is accepted across overflow");
        check(!sample(&state, 2, start + 31U), "new position starts another debounce across overflow");
        check(!sample(&state, 4, start + 45U), "bounce restarts time across overflow");
        check(!sample(&state, 4, start + 74U), "bounced position waits29ms");
        check(sample(&state, 4, start + 75U) == 3, "bounced position settles across overflow");
    }
    ig_profile_selector_init(&state, 10);
    sample(&state, 2, 10); sample(&state, 2, 40);
    check(sample(&state, 2, 11) == 2, "confirmed request is retained after an entire clock cycle");
    // An unstable selector cannot eventually become valid just because time passes.
    for (uint32_t at = 100; at < 20000; at += 10)
        check(!sample(&state, at & 4U ? 1 : 4, at), "sustained bouncing cannot select a profile");
}

static void check_deferred_application(void) {
    ig_profile_selector_state_t state;
    ig_profile_selector_init(&state, 0);
    uint8_t active = 1;
    sample(&state, 4, 0);
    // Simulate every combination of caller busy reasons; the module is unaware
    // of runtime state and cannot apply a profile, emit keys, or write storage.
    for (unsigned busy = 1; busy < 32; ++busy) {
        uint8_t requested = sample(&state, 4, 30 + busy);
        check(requested == 3 && active == 1, "stable requested3 is separate from active1 while caller defers");
    }
    active = ig_profile_selector_requested(&state);
    check(active == 3, "caller applies only when ready");
    sample(&state, 2, 100); sample(&state, 2, 130);
    check(active == 3 && ig_profile_selector_requested(&state) == 2, "new request does not change caller active profile");
    sample(&state, 0, 131);
    check(active == 3 && !ig_profile_selector_requested(&state), "invalid contact cancels pending2 while active3 remains");
    // Independent state instances cannot influence one another.
    ig_profile_selector_state_t other;
    ig_profile_selector_init(&other, 200);
    sample(&other, 1, 200); sample(&other, 1, 230);
    check(ig_profile_selector_requested(&other) == 1 && !ig_profile_selector_requested(&state), "state is per-instance with no hidden global storage");
}
#endif

int main(void) {
    check_decoder(); check_startup_and_transitions();
#if IG_PROFILE_SELECTOR_SUPPORTED
    check_bounce_and_wrap(); check_deferred_application();
#endif
    printf("PASS selector support%d: %u pure native checks; no hardware access.\n", IG_PROFILE_SELECTOR_SUPPORTED, checks);
    return 0;
}
