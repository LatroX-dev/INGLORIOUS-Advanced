// SPDX-License-Identifier: GPL-2.0-or-later
// Integer-only per-LED effects. No allocation, persistence or device operations.
#pragma once
#include <stdbool.h>
#include <stdint.h>

typedef struct { uint8_t r, g, b; } ig_rgb_t;
typedef struct { uint32_t time; uint8_t x, y, led; } ig_effect_hit_t;
#define IG_EFFECT_HITS 8
#define IG_EFFECT_MAX_HIT_AGE_MS 4000UL

static uint8_t ig_effect_scale(uint8_t value, uint8_t scale) { return (uint16_t)value * scale / 255; }
static uint8_t ig_effect_triangle(uint8_t phase) { return phase < 128 ? phase * 2 : (255 - phase) * 2; }
static uint8_t ig_effect_hash(uint32_t value) {
    value ^= value >> 16; value *= 0x7feb352dUL; value ^= value >> 15; value *= 0x846ca68bUL; value ^= value >> 16;
    return value;
}
static ig_rgb_t ig_effect_rainbow(uint8_t hue) {
    uint8_t region = hue / 43, fraction = (hue % 43) * 6;
    switch (region) {
        case 0: return (ig_rgb_t){255, fraction, 0};
        case 1: return (ig_rgb_t){255 - fraction, 255, 0};
        case 2: return (ig_rgb_t){0, 255, fraction};
        case 3: return (ig_rgb_t){0, 255 - fraction, 255};
        case 4: return (ig_rgb_t){fraction, 0, 255};
        default: return (ig_rgb_t){255, 0, 255 - fraction};
    }
}
// 0..8 RGB colors; a full period includes the final-to-first transition.
// Empty sequences use the original rainbow. No allocation and no global state.
static inline ig_rgb_t ig_effect_sequence(uint8_t phase, const uint8_t *colors, uint8_t count) {
    if (!count || count > 8 || !colors) return ig_effect_rainbow(phase);
    uint16_t scaled = (uint16_t)phase * count;
    uint8_t index = scaled >> 8, next = (index + 1) % count, fraction = scaled;
    ig_rgb_t result;
    result.r = ((uint16_t)colors[index * 3] * (256 - fraction) + (uint16_t)colors[next * 3] * fraction) >> 8;
    result.g = ((uint16_t)colors[index * 3 + 1] * (256 - fraction) + (uint16_t)colors[next * 3 + 1] * fraction) >> 8;
    result.b = ((uint16_t)colors[index * 3 + 2] * (256 - fraction) + (uint16_t)colors[next * 3 + 2] * fraction) >> 8;
    return result;
}
static uint16_t ig_effect_abs(int16_t value) { return value < 0 ? (uint16_t)-value : (uint16_t)value; }

// config: mode, speed, brightness, target(1 keys/2 sides/3 all), flags, reserved[3].
// Input and hit coordinates are QMK's existing physical LED coordinates.
static ig_rgb_t ig_effect_pixel(const uint8_t config[8], ig_rgb_t base, uint8_t led, uint8_t x, uint8_t y, bool side,
                                uint32_t now, const ig_effect_hit_t *hits, uint8_t hit_count) {
    if (!(config[3] & (side ? 2 : 1))) return base;
    if (side && (config[4] & 2)) x = 0; // Mirrored right-side coordinates match the corresponding left side.
    uint32_t period = 10000UL - (uint32_t)config[1] * 9600UL / 255;
    uint8_t phase = (now % period) * 256UL / period;
    uint8_t direction_phase = (config[4] & 1) ? (uint8_t)-phase : phase;
    uint8_t level = 255;
    switch (config[0]) {
        case 0: break;
        case 1: level = ig_effect_triangle(phase); break;
        case 2: base = ig_effect_rainbow(phase); break;
        case 3: base = ig_effect_rainbow(x - direction_phase); break;
        case 4: base = ig_effect_rainbow(y * 4 - direction_phase); break;
        case 5: level = ig_effect_triangle(x - direction_phase); break;
        case 6: level = ig_effect_triangle(y * 4 - direction_phase); break;
        case 7: {
            uint8_t coordinate = side ? y * 4 : x;
            uint8_t position = ig_effect_triangle(phase);
            if (config[4] & 1) position = 255 - position;
            uint16_t distance = ig_effect_abs((int16_t)coordinate - position);
            level = distance < 32 ? 255 - distance * 8 : 0;
            break;
        }
        case 8: {
            uint8_t seed = ig_effect_hash((uint32_t)x * 257 + y);
            uint8_t sparkle = ig_effect_triangle(phase + seed);
            level = sparkle > 192 ? (sparkle - 192) * 4 : 0;
            break;
        }
        case 9:
        case 10:
        case 11:
        case 15:
        case 20: {
            level = 0;
            uint32_t lifetime = IG_EFFECT_MAX_HIT_AGE_MS - (uint32_t)config[1] * 3600UL / 255;
            if (hit_count > IG_EFFECT_HITS) hit_count = IG_EFFECT_HITS;
            for (uint8_t i = 0; i < hit_count; ++i) {
                uint32_t age = now - hits[i].time;
                if (age >= lifetime) continue;
                uint16_t contribution;
                if (config[0] == 9) {
                    if (!side && hits[i].led != led) continue;
                    contribution = 255 - age * 255 / lifetime;
                } else {
                    // Octagonal distance avoids floating point/square roots in the scan loop.
                    uint16_t dx = ig_effect_abs((int16_t)x - hits[i].x), dy = ig_effect_abs((int16_t)y - hits[i].y);
                    uint16_t distance = dx > dy ? dx + dy / 2 : dy + dx / 2;
                    if (config[0] == 15) {
                        uint8_t fade = 255 - age * 255 / lifetime;
                        contribution = distance < 64 ? (uint32_t)fade * fade / 255 * (64 - distance) / 64 : 0;
                    } else {
                        if (config[0] == 20) distance = dx < dy ? dx : dy;
                        uint16_t ring = age * 320 / lifetime;
                        uint16_t delta = ig_effect_abs((int16_t)distance - ring);
                        contribution = delta < 24 ? 255 - delta * 11 : 0;
                    }
                }
                if (contribution > level) level = contribution;
            }
            if (config[0] == 11) base = ig_effect_rainbow(x + y * 2 + phase);
            break;
        }
        case 12: {
            uint8_t seed = ig_effect_hash(x / 12);
            uint8_t distance = y * 4 - direction_phase - seed;
            level = distance < 48 ? 255 - distance * 5 : 0;
            base = ig_effect_rainbow(seed + phase / 4);
            break;
        }
        case 13: {
            uint8_t position = x + y * 2;
            base = ig_effect_rainbow((config[4] & 1) ? (uint8_t)-position : position);
            break;
        }
        case 14: {
            uint8_t sparkle = ig_effect_hash((uint32_t)x * 257 + y + (now / (period / 16)) * 65537UL);
            level = sparkle > 216 ? 255 : 80;
            break;
        }
        case 16: {
            uint8_t distance = x + y * 3 - direction_phase;
            level = distance < 64 ? 255 - distance * 3 : 0;
            break;
        }
        case 17: level = (((uint16_t)x + direction_phase) / 24 + y / 12) & 1 ? 255 : 32; break;
        case 18: {
            // Two unequal pulses followed by a pause, using a fixed phase envelope.
            if (phase < 48) level = phase < 24 ? phase * 10 : (48 - phase) * 10;
            else if (phase >= 72 && phase < 112) level = phase < 92 ? (phase - 72) * 8 : (112 - phase) * 8;
            else level = 0;
            break;
        }
        case 19: {
            uint8_t heat = ig_effect_hash((uint32_t)(x / 12) * 257 + (now / (period / 32)) * 65537UL);
            base = (ig_rgb_t){255, (uint8_t)(32 + heat / 2), 0};
            level = 64 + (uint16_t)y * 127 / 64 + heat / 4;
            break;
        }
        default: return base; // Validation rejects unknown modes before persistence.
    }
    level = ig_effect_scale(level, config[2]);
    return (ig_rgb_t){ig_effect_scale(base.r, level), ig_effect_scale(base.g, level), ig_effect_scale(base.b, level)};
}
