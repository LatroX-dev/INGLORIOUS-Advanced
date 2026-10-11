// SPDX-License-Identifier: GPL-2.0-or-later
// Independent expected pixels from the unchanged firmware implementation.
#include <stdio.h>
#include <stdlib.h>
#include "../../firmware/inglorious/ig_effects.h"
static uint32_t state = 0x93A47521;
static uint32_t next_value(void) { state = state * 1664525u + 1013904223u; return state; }
static void byte(FILE *file, uint8_t v) { if (fputc(v, file) == EOF) exit(3); }
static void word(FILE *file, uint32_t v) { for (int i = 0; i < 4; ++i) byte(file, v >> (8 * i)); }
int main(int argc, char **argv) {
    if (argc != 2) return 1;
    FILE *file = fopen(argv[1], "wb"); if (!file) return 2;
    uint8_t speeds[] = {0, 1, 128, 254, 255};
    uint32_t ages[] = {0, 1, 399, 400, 999, 3999, 4000, 0xffffffffu};
    uint32_t count = 0;
    for (int mode = 0; mode <= 20; ++mode)
    for (int flags = 0; flags < 4; ++flags)
    for (int target = 1; target <= 3; ++target)
    for (int side = 0; side < 2; ++side)
    for (int speed = 0; speed < 5; ++speed)
    for (int sample = 0; sample < 16; ++sample) {
        uint8_t config[8] = {mode, speeds[speed], sample == 0 ? 0 : sample == 1 ? 255 : (uint8_t)next_value(), target, flags, 0, 0, 0};
        ig_rgb_t base = {(uint8_t)next_value(), (uint8_t)(next_value() >> 8), (uint8_t)(next_value() >> 16)};
        uint8_t led = next_value() % 98, x = next_value(), y = next_value();
        uint32_t now = sample == 0 ? 0 : sample == 1 ? 0xffffffffu : next_value();
        uint8_t hits_count = sample % 10; // Includes zero and >8 to exercise the firmware clamp.
        ig_effect_hit_t hits[IG_EFFECT_HITS];
        for (int i = 0; i < IG_EFFECT_HITS; ++i) hits[i] = (ig_effect_hit_t){now - ages[(i + sample) % 8], (uint8_t)next_value(), (uint8_t)next_value(), i == 0 ? led : (uint8_t)(next_value() % 98)};
        // Some exact-position hits, alongside arbitrary geometry and unsigned time wrap.
        hits[0].x = x; hits[0].y = y;
        ig_rgb_t expected = ig_effect_pixel(config, base, led, x, y, side, now, hits, hits_count);
        for (int i = 0; i < 5; ++i) byte(file, config[i]);
        byte(file, base.r); byte(file, base.g); byte(file, base.b);
        byte(file, led); byte(file, x); byte(file, y); byte(file, side); word(file, now); byte(file, hits_count);
        for (int i = 0; i < IG_EFFECT_HITS; ++i) { word(file, hits[i].time); byte(file, hits[i].x); byte(file, hits[i].y); byte(file, hits[i].led); }
        byte(file, expected.r); byte(file, expected.g); byte(file, expected.b); ++count;
    }
    if (fclose(file)) return 4;
    printf("Generated %u deterministic firmware RGB oracle vectors. No device operations.\n", count); return 0;
}
