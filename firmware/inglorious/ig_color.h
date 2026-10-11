// SPDX-License-Identifier: GPL-2.0-or-later
#pragma once
#include <stdint.h>

// Encoded sRGB -> linear 8-bit LED duty. Round the standard piecewise sRGB
// transfer function to nearest: 255 * (s/12.92 for s <= 0.04045, otherwise
// ((s + 0.055)/1.055)^2.4), where s = encoded/255.
// Source: https://www.w3.org/TR/css-color-4/#color-conversion-code
// This is an output transfer approximation, not measured LED white-point or
// spectral calibration. Profile bytes stay encoded sRGB and are never rewritten.
// Low values quantize at the driver's 8-bit PWM resolution. This const table
// resides in flash on both supported ARM boards; no floating point or heap use.
static const uint8_t ig_srgb_pwm[256] = {
      0,   0,   0,   0,   0,   0,   0,   1,   1,   1,   1,   1,   1,   1,   1,   1,
      1,   1,   2,   2,   2,   2,   2,   2,   2,   2,   3,   3,   3,   3,   3,   3,
      4,   4,   4,   4,   4,   5,   5,   5,   5,   6,   6,   6,   6,   7,   7,   7,
      8,   8,   8,   8,   9,   9,   9,  10,  10,  10,  11,  11,  12,  12,  12,  13,
     13,  13,  14,  14,  15,  15,  16,  16,  17,  17,  17,  18,  18,  19,  19,  20,
     20,  21,  22,  22,  23,  23,  24,  24,  25,  25,  26,  27,  27,  28,  29,  29,
     30,  30,  31,  32,  32,  33,  34,  35,  35,  36,  37,  37,  38,  39,  40,  41,
     41,  42,  43,  44,  45,  45,  46,  47,  48,  49,  50,  51,  51,  52,  53,  54,
     55,  56,  57,  58,  59,  60,  61,  62,  63,  64,  65,  66,  67,  68,  69,  70,
     71,  72,  73,  74,  76,  77,  78,  79,  80,  81,  82,  84,  85,  86,  87,  88,
     90,  91,  92,  93,  95,  96,  97,  99, 100, 101, 103, 104, 105, 107, 108, 109,
    111, 112, 114, 115, 116, 118, 119, 121, 122, 124, 125, 127, 128, 130, 131, 133,
    134, 136, 138, 139, 141, 142, 144, 146, 147, 149, 151, 152, 154, 156, 157, 159,
    161, 163, 164, 166, 168, 170, 171, 173, 175, 177, 179, 181, 183, 184, 186, 188,
    190, 192, 194, 196, 198, 200, 202, 204, 206, 208, 210, 212, 214, 216, 218, 220,
    222, 224, 226, 229, 231, 233, 235, 237, 239, 242, 244, 246, 248, 250, 253, 255
};
static uint8_t ig_srgb_to_pwm(uint8_t encoded) { return ig_srgb_pwm[encoded]; }
