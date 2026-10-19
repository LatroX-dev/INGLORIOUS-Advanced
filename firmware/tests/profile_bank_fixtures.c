// SPDX-License-Identifier: GPL-2.0-or-later
// Explicit offline fixture exporter; never opens a device. The caller supplies
// an independently semantically validated, non-user IGCF fixture payload.
#include "../inglorious/ig_profile_bank.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static uint8_t bytes[IG_PB_BANK_SIZE], saved[IG_PB_BANK_SIZE], payload[IG_PB_PAYLOAD_CAPACITY];
static uint32_t length;
static uint16_t schema;
static const char *directory;
static void require(bool condition) { if (!condition) { fputs("Fixture creation failed.\n", stderr); exit(1); } }
static uint16_t u16(const uint8_t *p) { return (uint16_t)(p[0] | ((uint16_t)p[1] << 8)); }
static void w16(uint8_t *p, uint16_t n) { p[0] = (uint8_t)n; p[1] = (uint8_t)(n >> 8); }
static void w32(uint8_t *p, uint32_t n) { for (unsigned i = 0; i < 4; ++i) p[i] = (uint8_t)(n >> (i * 8)); }
static bool valid(void *ctx, const uint8_t *p, uint32_t n, uint16_t s) {
    (void)ctx; return n == length && s == schema && memcmp(p, payload, length) == 0;
}
static bool fallback(void *ctx, ig_pb_fallback *out) {
    (void)ctx; out->payload = payload; out->length = length; out->schema = schema; return true;
}
static bool erase(void *ctx, size_t offset) {
    (void)ctx; require(offset % 256u == 0 && offset <= IG_PB_BANK_SIZE - 256u);
    memset(bytes + offset, 255, 256); return true;
}
static bool program(void *ctx, size_t offset, const uint8_t *page) {
    (void)ctx; require(offset % 256u == 0 && offset <= IG_PB_BANK_SIZE - 256u);
    memcpy(bytes + offset, page, 256); return true;
}
static ig_pb_bank bank = {bytes, sizeof(bytes), 16, valid, NULL, fallback, NULL};
static ig_pb_io writer = {NULL, erase, program};
static void seal(uint8_t *record) {
    w32(record + 36, ig_pb_crc32(record, 36));
    w32(record + IG_PB_COMMIT_OFFSET + 24, ig_pb_crc32(record, IG_PB_COMMIT_OFFSET));
    w32(record + IG_PB_COMMIT_OFFSET + 28, ig_pb_crc32(record + IG_PB_COMMIT_OFFSET, 28));
}
static void output(const char *name, ig_pb_result expected, ig_pb_pair_state first_pair) {
    ig_pb_scan scan; require(ig_pb_scan_bank(&bank, &scan) == IG_PB_OK);
    require(scan.write_status == expected && scan.pair[0] == first_pair);
    char path[2048]; int n = snprintf(path, sizeof(path), "%s/%s.bin", directory, name);
    require(n > 0 && (size_t)n < sizeof(path)); FILE *file = fopen(path, "wb"); require(file != NULL);
    require(fwrite(bytes, 1, sizeof(bytes), file) == sizeof(bytes)); require(fclose(file) == 0);
    printf("%s %u %u\n", name, (unsigned)expected, (unsigned)first_pair);
}
int main(int argc, char **argv) {
    require(argc == 3); directory = argv[2];
    FILE *input = fopen(argv[1], "rb"); require(input != NULL);
    size_t n = fread(payload, 1, sizeof(payload), input); require(n >= 32 && fgetc(input) == EOF && !ferror(input));
    require(fclose(input) == 0); length = (uint32_t)n; schema = u16(payload + 4);
    require(memcmp(payload, "IGCF", 4) == 0 && payload[6] == 16 && schema >= 1 && schema <= 7);
    memset(bytes, 255, sizeof(bytes)); output("empty", IG_PB_OK, IG_PB_PAIR_EMPTY);
    bytes[17] = 0; output("first-save-torn", IG_PB_OK, IG_PB_PAIR_DEGRADED);
    memset(bytes, 255, sizeof(bytes));
    for (uint8_t profile = 1; profile <= 3; ++profile)
        for (uint64_t generation = 0; generation < 2; ++generation)
            require(ig_pb_save_profile(&bank, &writer, profile, generation, schema, payload, length) == IG_PB_OK);
    memcpy(saved, bytes, sizeof(bytes)); output("valid", IG_PB_OK, IG_PB_PAIR_READY);
    memset(bytes, 0, 117); output("torn-inactive", IG_PB_OK, IG_PB_PAIR_RECOVERED);
    memcpy(bytes, saved, sizeof(bytes)); w16(bytes + 8, 2); seal(bytes);
    memset(bytes + IG_PB_COMMIT_OFFSET, 255, 256); output("future-header", IG_PB_ERR_FUTURE, IG_PB_PAIR_BLOCKED);
    memcpy(bytes, saved, sizeof(bytes)); w16(bytes + 12, 17); seal(bytes);
    memset(bytes + IG_PB_COMMIT_OFFSET, 255, 256); output("foreign-header", IG_PB_ERR_FOREIGN, IG_PB_PAIR_BLOCKED);
    memcpy(bytes, saved, sizeof(bytes)); memcpy(bytes, bytes + IG_PB_RECORD_SIZE, IG_PB_RECORD_SIZE);
    output("equal-generation", IG_PB_ERR_AMBIGUOUS, IG_PB_PAIR_AMBIGUOUS);
    memcpy(bytes, saved, sizeof(bytes)); memcpy(bytes, bytes + 2 * IG_PB_RECORD_SIZE, IG_PB_RECORD_SIZE);
    output("wrong-pair", IG_PB_ERR_FOREIGN, IG_PB_PAIR_BLOCKED);
    return 0;
}
