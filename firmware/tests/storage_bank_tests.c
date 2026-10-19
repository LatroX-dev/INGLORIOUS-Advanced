// SPDX-License-Identifier: GPL-2.0-or-later
// Actual patched QMK replay and cache borrowing, with bank mode enabled.
// The legacy medium is only an in-process byte array. No device access.
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include "../qmk_firmware/quantum/wear_leveling/wear_leveling.c"

_Static_assert(IG_PROFILE_BANK_ENABLE == 1 && IG_BOARD == 16, "Bank storage fixture");
static uint8_t medium[32768], before[32768], expected[16384];
static unsigned checks, reads, writes, erases, unlocks, locks, fail_read;
static bool fail_init;
static void check(bool valid, const char *message) {
    ++checks;
    if (!valid) { fprintf(stderr, "FAIL bank width%d check%u: %s\n", BACKING_STORE_WRITE_SIZE, checks, message); exit(1); }
}
static void equal(const void *first, const void *second, size_t length, const char *message) {
    const uint8_t *a = first, *b = second;
    for (size_t i = 0; i < length; ++i) check(a[i] == b[i], message);
}
bool backing_store_init(void) { return !fail_init; }
bool backing_store_unlock(void) { ++unlocks; return true; }
bool backing_store_lock(void) { ++locks; return true; }
bool backing_store_erase(void) { ++erases; memset(medium, 0, sizeof(medium)); return true; }
bool backing_store_write(uint32_t address, backing_store_int_t value) {
    check(address % sizeof(value) == 0 && address <= sizeof(medium) - sizeof(value), "bounded backing write");
    ++writes; memcpy(medium + address, &value, sizeof(value)); return true;
}
bool backing_store_read(uint32_t address, backing_store_int_t *value) {
    check(address % sizeof(*value) == 0 && address <= sizeof(medium) - sizeof(*value), "bounded backing read");
    if (++reads == fail_read) return false;
    memcpy(value, medium + address, sizeof(*value)); return true;
}
static void reset_ram(void) {
    memset(&wear_leveling, 0, sizeof(wear_leveling));
    ig_storage_borrowed = ig_storage_fault = ig_storage_read_only_reload = false;
    ig_storage_phase = 0;
    reads = writes = erases = unlocks = locks = fail_read = 0; fail_init = false;
}
static void snapshot(void) { memcpy(before, medium, sizeof(before)); }
static void unchanged(void) {
    check(!writes && !erases && !unlocks && !locks, "zero legacy write/erase/unlock/lock operations");
    equal(medium, before, sizeof(medium), "every legacy byte preserved");
}
static void consolidated(void) {
    reset_ram(); memset(medium, 0, sizeof(medium));
    for (unsigned i = 0; i < sizeof(expected); ++i) expected[i] = (uint8_t)(i * 31 + 19);
    memcpy(medium, expected, sizeof(expected));
    const uint64_t hash = fnv_64a_buf(expected, sizeof(expected), FNV1A_64_INIT);
    memcpy(medium + 16384, &hash, sizeof(hash)); snapshot();
}
// Independently encode 8-byte log entries. Five bytes at logical address 20.
static void fill_log(void) {
    for (unsigned offset = 16392; offset < sizeof(medium); offset += 8) {
        const uint8_t entry[8] = {40, 0, 20, 81, 82, 83, 84, 85};
        memcpy(medium + offset, entry, sizeof(entry));
    }
    memcpy(expected + 20, (const uint8_t[]){81, 82, 83, 84, 85}, 5); snapshot();
}
static void rejected_writes(void) {
    uint8_t one = 1;
    check(wear_leveling_write(0, &one, 1) == WEAR_LEVELING_FAILED, "ordinary write denied even without borrowing");
    check(wear_leveling_write(UINT32_MAX, NULL, SIZE_MAX) == WEAR_LEVELING_FAILED, "invalid write denied without dereference");
    check(wear_leveling_erase() == WEAR_LEVELING_FAILED, "ordinary erase denied even without borrowing");
    check(ig_storage_stage_write(0, &one, 1) == WEAR_LEVELING_FAILED, "legacy staged write denied"); unchanged();
}
static void boot_cases(void) {
    reset_ram(); memset(medium, 0, sizeof(medium)); memset(expected, 0, sizeof(expected)); snapshot();
    check(wear_leveling_init() != WEAR_LEVELING_FAILED, "empty boot succeeds read-only");
    equal(wear_leveling.cache, expected, sizeof(expected), "empty boot has zero cache"); rejected_writes();
    consolidated(); check(wear_leveling_init() != WEAR_LEVELING_FAILED, "consolidated boot succeeds");
    equal(wear_leveling.cache, expected, sizeof(expected), "consolidated boot exact"); rejected_writes();
    consolidated(); fill_log(); check(wear_leveling_init() != WEAR_LEVELING_FAILED, "full log boot succeeds without consolidation");
    check(wear_leveling.write_address == sizeof(medium), "full log actually consumed to end");
    equal(wear_leveling.cache, expected, sizeof(expected), "full log replay exact"); rejected_writes();
    consolidated(); medium[16392] = 0xFF; snapshot();
    check(wear_leveling_init() == WEAR_LEVELING_FAILED, "corrupt log fails without repair"); rejected_writes();
    consolidated(); medium[16384] ^= 1; memset(expected, 0, sizeof(expected)); snapshot();
    check(wear_leveling_init() != WEAR_LEVELING_FAILED, "invalid consolidated checksum becomes empty fallback");
    equal(wear_leveling.cache, expected, sizeof(expected), "invalid checksum is not trusted"); rejected_writes();
    const unsigned fail_points[] = {1, 7, 16384 / BACKING_STORE_WRITE_SIZE, 16384 / BACKING_STORE_WRITE_SIZE + 1,
        16392 / BACKING_STORE_WRITE_SIZE, 16392 / BACKING_STORE_WRITE_SIZE + 1, 32768 / BACKING_STORE_WRITE_SIZE};
    for (unsigned i = 0; i < sizeof(fail_points) / sizeof(*fail_points); ++i) {
        consolidated(); fill_log(); fail_read = fail_points[i];
        check(wear_leveling_init() == WEAR_LEVELING_FAILED, "physical read failure propagated at boot"); unchanged();
    }
    consolidated(); fail_init = true;
    check(wear_leveling_init() == WEAR_LEVELING_FAILED, "backing initialization failure propagated"); unchanged();
}
static void borrow_cases(void) {
    const unsigned addresses[] = {1056, 8736};
    const unsigned lengths[] = {1, 5656, 7200, 7208, 7272, 7648};
    for (unsigned full = 0; full < 2; ++full) for (unsigned a = 0; a < 2; ++a)
        for (unsigned n = 0; n < sizeof(lengths) / sizeof(*lengths); ++n) {
            consolidated(); if (full) fill_log();
            check(wear_leveling_init() != WEAR_LEVELING_FAILED, "borrow source initialized");
            uint8_t *stage = ig_storage_stage_begin(addresses[a], lengths[n]);
            check(stage == wear_leveling.cache + addresses[a], "exact shared cache view borrowed");
            memset(stage, 0xD3, lengths[n]);
            check(wear_leveling_init() == WEAR_LEVELING_FAILED, "init cannot discard borrowed upload");
            rejected_writes();
            check(ig_storage_stage_finish(true) == WEAR_LEVELING_FAILED && ig_storage_borrowed,
                "legacy commit cannot release or persist dirty cache"); unchanged();
            check(ig_storage_stage_finish(false) != WEAR_LEVELING_FAILED && !ig_storage_borrowed,
                "bank save cleanup replays legacy without writing");
            equal(wear_leveling.cache, expected, sizeof(expected), "abort restores complete immutable cache"); unchanged();
            reset_ram(); check(wear_leveling_init() != WEAR_LEVELING_FAILED, "power loss boots immutable source");
            equal(wear_leveling.cache, expected, sizeof(expected), "post reboot cache unchanged"); unchanged();
        }
    consolidated(); check(wear_leveling_init() != WEAR_LEVELING_FAILED, "corrupt replay setup");
    check(ig_storage_stage_begin(8736, 7272) != NULL, "corrupt replay borrow");
    medium[16392] = 0xFF; snapshot();
    check(ig_storage_stage_finish(false) == WEAR_LEVELING_FAILED && ig_storage_fault, "corrupt replay latches fault without repair"); unchanged();
    consolidated(); check(wear_leveling_init() != WEAR_LEVELING_FAILED, "changed replay setup");
    check(ig_storage_stage_begin(8736, 7272) != NULL, "changed replay borrow");
    medium[0] ^= 1;
    const uint64_t changed_hash = fnv_64a_buf(medium, 16384, FNV1A_64_INIT);
    memcpy(medium + 16384, &changed_hash, sizeof(changed_hash)); snapshot();
    check(ig_storage_stage_finish(false) == WEAR_LEVELING_FAILED && ig_storage_fault, "valid but changed legacy rejected by original cache hash"); unchanged();
    check(ig_storage_stage_begin(1056, 1) == NULL, "fault blocks additional borrowing"); rejected_writes();
}
int main(void) {
    boot_cases(); borrow_cases();
    printf("PASS: bank board16 width%d, %u immutable legacy storage checks; no hardware.\n", BACKING_STORE_WRITE_SIZE, checks);
    return 0;
}
