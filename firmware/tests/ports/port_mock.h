// SPDX-License-Identifier: GPL-2.0-or-later
#pragma once
#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>
#include "port_model.h"
#include "ig_board.h"
#define RAW_EPSIZE 32
#define KC_NO 0
#define KC_TRNS 1
#define KC_NUM 83
#define KC_VOLU 0xA9
#define KC_VOLD 0xAA
#define RGB_MATRIX_SOLID_COLOR 1
#define LED_FLAG_UNDERGLOW 2
#define B6 6
#define B12 12
#define B14 14
#define C4 20
typedef uint16_t matrix_row_t;
typedef struct { uint8_t x, y; } point_t;
typedef struct { uint8_t matrix_co[MATRIX_ROWS][MATRIX_COLS]; point_t point[RGB_MATRIX_LED_COUNT]; uint8_t flags[RGB_MATRIX_LED_COUNT]; } led_config_t;
extern led_config_t g_led_config;
typedef struct { struct { bool pressed; int type; struct { uint8_t row, col; } key; } event; } keyrecord_t;
#define IS_KEYEVENT(event) ((event).type == 1)
#define IS_ENCODEREVENT(event) ((event).type == 2 || (event).type == 3)
typedef enum { WEAR_LEVELING_FAILED, WEAR_LEVELING_SUCCESS, WEAR_LEVELING_CONSOLIDATED } wear_leveling_status_t;
extern uint32_t layer_state, default_layer_state;
uint16_t keycode_at_keymap_location_raw(uint8_t layer, uint8_t row, uint8_t col);
uint8_t eeprom_read_byte(const uint8_t *address);
void eeprom_read_block(void *out, const void *address, size_t length);
wear_leveling_status_t wear_leveling_write(uint32_t address, const void *data, size_t length);
wear_leveling_status_t wear_leveling_init(void);
matrix_row_t matrix_get_row(uint8_t row);
void raw_hid_send(uint8_t *data, uint8_t length);
void register_code(uint8_t code);
void unregister_code(uint8_t code);
uint32_t timer_read32(void);
void layer_clear(void);
uint8_t get_highest_layer(uint32_t state);
void rgb_matrix_set_color(uint8_t led, uint8_t r, uint8_t g, uint8_t b);
void rgb_matrix_enable_noeeprom(void);
void rgb_matrix_mode_noeeprom(uint8_t mode);
uint32_t timer_elapsed32(uint32_t since);
void gpio_write_pin_high(int pin);
void gpio_set_pin_output(int pin);
void matrix_scan(void);
void wait_ms(uint16_t milliseconds);
void bootloader_jump(void);
