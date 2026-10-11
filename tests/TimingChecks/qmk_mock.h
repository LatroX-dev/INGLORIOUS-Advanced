#pragma once
#include <stdbool.h>
#include <stdint.h>
#define PROTOCOL_CHIBIOS 1
#define IG_DYNAMIC_USB_INTERVAL 1
#ifdef IG_TEST_PRO
#define MATRIX_ROWS 11
#else
#define MATRIX_ROWS 5
#endif
typedef uint8_t matrix_row_t;
