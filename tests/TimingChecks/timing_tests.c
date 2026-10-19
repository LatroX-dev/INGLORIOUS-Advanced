#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#define QMK_KEYBOARD_H "qmk_mock.h"
#include "../../firmware/inglorious/ig_timing.c"

static unsigned checks, clears, disconnects, restarts;
static uint32_t now;
static uint8_t disconnected_interval, restarted_interval;
USBDriver mock_usb = {USB_ACTIVE};
#define CHECK(condition) do { ++checks; if (!(condition)) { fprintf(stderr, "FAIL line %d: %s\n", __LINE__, #condition); exit(1); } } while (0)
uint32_t timer_read32(void) { return now; }
void clear_keyboard(void) { ++clears; }
void usbDisconnectBus(USBDriver *driver) { CHECK(driver == &mock_usb); ++disconnects; disconnected_interval = ig_timing_active_interval(); }
void restart_usb_driver(USBDriver *driver) { CHECK(driver == &mock_usb); CHECK(disconnects == restarts + 1); ++restarts; restarted_interval = ig_timing_active_interval(); }
static void reset(void) { now = clears = disconnects = restarts = 0; mock_usb.state = USB_ACTIVE; ig_timing_init(1, 5); debounce_init(); }

int main(void) {
    CHECK(ig_timing_active_interval() == 1 && ig_timing_debounce_ms() == 5);
    for (unsigned rate = 0; rate < 256; ++rate) for (unsigned delay = 0; delay < 256; ++delay) {
        const bool valid = (rate == 1 || rate == 2 || rate == 4 || rate == 8) && delay <= 40;
        CHECK(ig_timing_valid(rate, delay) == valid);
    }
    const uint8_t rates[] = {1, 2, 4, 8};
    for (unsigned i = 0; i < sizeof(rates); ++i) {
        reset(); ig_timing_init(rates[i], 40);
        CHECK(ig_timing_active_interval() == rates[i] && ig_timing_saved_interval() == rates[i]);
        CHECK(ig_timing_debounce_ms() == 40 && !ig_timing_schedule_reconnect());
    }
    reset(); ig_timing_init(3, 99);
    CHECK(ig_timing_active_interval() == 1 && ig_timing_debounce_ms() == 5);
    CHECK(ig_timing_update(8, 0));
    CHECK(ig_timing_active_interval() == 1 && ig_timing_saved_interval() == 8 && ig_timing_debounce_ms() == 0);
    now = 1000; ig_timing_task(true); CHECK(restarts == 0);
    CHECK(!ig_timing_update(3, 5) && !ig_timing_update(4, 41));
    CHECK(ig_timing_saved_interval() == 8 && ig_timing_debounce_ms() == 0);
    CHECK(ig_timing_schedule_reconnect());
    now = 1099; ig_timing_task(true); CHECK(restarts == 0 && ig_timing_reconnect_pending());
    CHECK(ig_timing_schedule_reconnect()); // Must not postpone an existing request.
    now = 1100; ig_timing_task(false); CHECK(restarts == 0 && ig_timing_reconnect_pending());
    mock_usb.state = 0; ig_timing_task(true); CHECK(restarts == 0 && ig_timing_reconnect_pending());
    mock_usb.state = USB_ACTIVE; ig_timing_task(true);
    CHECK(restarts == 1 && clears == 1 && !ig_timing_reconnect_pending());
    CHECK(disconnected_interval == 1 && restarted_interval == 8);
    ig_timing_task(true); CHECK(restarts == 1);
    CHECK(!ig_timing_schedule_reconnect());
    CHECK(ig_timing_update(2, 5) && ig_timing_schedule_reconnect());
    CHECK(ig_timing_update(4, 10) && !ig_timing_reconnect_pending());
    now += 1000; ig_timing_task(true); CHECK(restarts == 1 && ig_timing_active_interval() == 8);
    reset(); now = UINT32_MAX - 50; CHECK(ig_timing_update(4, 5) && ig_timing_schedule_reconnect());
    now += 99; ig_timing_task(true); CHECK(restarts == 0);
    now += 1; ig_timing_task(true); CHECK(restarts == 1 && restarted_interval == 4);

    reset(); CHECK(ig_timing_usb_active());
    USB_DRIVER.state = 0; CHECK(!ig_timing_usb_active());
    USB_DRIVER.state = USB_ACTIVE;
    CHECK(ig_timing_update(8,5) && ig_timing_schedule_reconnect());
    ig_timing_cancel_reconnect(); now+=1000; ig_timing_task(true);
    CHECK(!ig_timing_reconnect_pending() && restarts==0 && clears==0 && ig_timing_active_interval()==1 && ig_timing_saved_interval()==8);

    for (unsigned delay = 0; delay <= 40; ++delay) {
        reset(); CHECK(ig_timing_update(1, delay));
        matrix_row_t raw[MATRIX_ROWS] = {0}, cooked[MATRIX_ROWS] = {0};
        CHECK(!debounce(raw, cooked, false));
        raw[0] = 1;
        CHECK(debounce(raw, cooked, true) == (delay == 0));
        if (delay) {
            now = delay - 1; CHECK(!debounce(raw, cooked, false));
            now = delay; CHECK(debounce(raw, cooked, false));
        }
        CHECK(cooked[0] == 1 && !debounce(raw, cooked, false));
        raw[MATRIX_ROWS - 1] = 128;
        CHECK(debounce(raw, cooked, true) == (delay == 0));
        now += delay;
        CHECK(debounce(raw, cooked, false) == (delay != 0));
        CHECK(cooked[MATRIX_ROWS - 1] == 128 && cooked[0] == 1);
        memset(raw, 0, sizeof(raw)); CHECK(debounce(raw, cooked, true) == (delay == 0));
        now += delay; CHECK(debounce(raw, cooked, false) == (delay != 0));
        CHECK(memcmp(raw, cooked, sizeof(raw)) == 0 && restarts == 0);
    }
    reset(); matrix_row_t raw[MATRIX_ROWS] = {1}, cooked[MATRIX_ROWS] = {0};
    CHECK(!debounce(raw, cooked, true));
    now = 4; raw[0] = 0; CHECK(!debounce(raw, cooked, true));
    now = 8; raw[0] = 1; CHECK(!debounce(raw, cooked, true));
    now = 12; CHECK(!debounce(raw, cooked, false));
    now = 13; CHECK(debounce(raw, cooked, false));
    // A different key restarts the global timer for all pending changes.
    now = 20; raw[0] = 0; CHECK(!debounce(raw, cooked, true));
    now = 24; raw[1] = 2; CHECK(!debounce(raw, cooked, true));
    now = 28; CHECK(!debounce(raw, cooked, false) && cooked[0] == 1);
    now = 29; CHECK(debounce(raw, cooked, false) && cooked[0] == 0 && cooked[1] == 2);
    // Runtime delay change restarts settling, rather than reusing an older timer.
    now = 30; raw[0] = 4; CHECK(!debounce(raw, cooked, true));
    now = 34; CHECK(ig_timing_update(1, 1)); CHECK(!debounce(raw, cooked, false));
    now = 35; CHECK(debounce(raw, cooked, false) && cooked[0] == 4);
    // Explicit zero delay flushes a pending stable matrix immediately.
    raw[0] = 8; CHECK(!debounce(raw, cooked, true)); CHECK(ig_timing_update(1, 0));
    CHECK(debounce(raw, cooked, false) && cooked[0] == 8);
    reset(); memset(raw, 0, sizeof(raw)); memset(cooked, 0, sizeof(cooked));
    now = UINT32_MAX - 2; raw[0] = 16; CHECK(!debounce(raw, cooked, true));
    now += 4; CHECK(!debounce(raw, cooked, false)); now += 1;
    CHECK(debounce(raw, cooked, false) && cooked[0] == 16);
    // Startup recovery scans once, waits 50 ms, then checks the debounced chord.
    for (unsigned delay = 0; delay <= 40; delay += 40) {
        reset(); ig_timing_init(1, delay); debounce_init();
        memset(raw, 0, sizeof(raw)); memset(cooked, 0, sizeof(cooked));
        raw[1] = 8; raw[3] = 8;
        CHECK(debounce(raw, cooked, true) == (delay == 0));
        now = 50; CHECK(debounce(raw, cooked, false) == (delay != 0));
        CHECK(cooked[1] == 8 && cooked[3] == 8);
    }
    printf("PASS timing (%u rows): %u checks; no real USB calls\n", MATRIX_ROWS, checks);
    return 0;
}
