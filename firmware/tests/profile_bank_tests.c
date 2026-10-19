// SPDX-License-Identifier: GPL-2.0-or-later
#include "../inglorious/ig_profile_bank.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <inttypes.h>

static unsigned long checks, fault_cases, explicit_retries;
#define CHECK(x) do { ++checks; if (!(x)) { fprintf(stderr, "FAIL line %d: %s\n", __LINE__, #x); exit(1); } } while (0)
typedef struct {
    uint8_t guarded[IG_PB_BANK_SIZE + 512];
    unsigned phase, fail_phase, prefix, corrupt_phase;
    bool dead;
} simulated_flash;
static simulated_flash f, baseline, copy;
static uint8_t payload[IG_PB_PAYLOAD_CAPACITY], legacy_data[64];
static unsigned validation_calls, legacy_reads;
static bool validate(void *context, const uint8_t *p, uint32_t n, uint16_t schema) {
    (void)context; ++validation_calls;
    return p && n >= 8 && schema == 7 && p[0] == 0xA5; // Injected fixture only.
}
static bool legacy(void *context, ig_pb_fallback *view) {
    (void)context; ++legacy_reads;
    view->payload = legacy_data; view->length = sizeof(legacy_data); view->schema = 7;
    return true;
}
static ig_pb_bank bank(simulated_flash *s) {
    ig_pb_bank b = {s->guarded + 256, IG_PB_BANK_SIZE, 16, validate, NULL, legacy, NULL}; return b;
}
static void initialize(simulated_flash *s) {
    memset(s, 0, sizeof(*s)); memset(s->guarded, 0x5A, sizeof(s->guarded));
    memset(s->guarded + 256, 255, IG_PB_BANK_SIZE);
}
static bool phase(simulated_flash *s, size_t off, const uint8_t *p, bool erase) {
    CHECK(!s->dead); CHECK(off % IG_PB_PAGE_SIZE == 0); CHECK(off <= IG_PB_BANK_SIZE - IG_PB_PAGE_SIZE);
    ++s->phase;
    size_t n = IG_PB_PAGE_SIZE;
    if (s->phase == s->fail_phase) { n = s->prefix; s->dead = true; }
    if (erase) memset(s->guarded + 256 + off, 0, n);
    else memcpy(s->guarded + 256 + off, p, n);
    if (s->phase == s->corrupt_phase) s->guarded[256 + off + 17] ^= 1u;
    return !s->dead;
}
static bool erase_page(void *ctx, size_t off) {
    // Physical WB32 erase is zero. The bounded adapter prepares logical empty
    // pages by programming FF, which itself has a zero-erase/program pair.
    uint8_t empty[IG_PB_PAGE_SIZE]; memset(empty, 255, sizeof(empty));
    return phase(ctx, off, NULL, true) && phase(ctx, off, empty, false);
}
static bool program_page(void *ctx, size_t off, const uint8_t *p) {
    // WB32's program normally erases and then programs the entire page.
    return phase(ctx, off, NULL, true) && phase(ctx, off, p, false);
}
static ig_pb_io io(simulated_flash *s) { ig_pb_io v = {s, erase_page, program_page}; return v; }
static void reboot(simulated_flash *s) { s->phase = s->fail_phase = s->corrupt_phase = 0; s->dead = false; }
static ig_pb_result save(simulated_flash *s, uint8_t profile, uint64_t generation, uint8_t value) {
    ig_pb_bank b = bank(s); ig_pb_io v = io(s);
    memset(payload, value, sizeof(payload)); payload[0] = 0xA5;
    return ig_pb_save_profile(&b, &v, profile, generation, 7, payload, sizeof(payload));
}
static ig_pb_view read_profile(simulated_flash *s, uint8_t profile) {
    ig_pb_bank b = bank(s); ig_pb_view v;
    CHECK(ig_pb_read_profile(&b, profile, &v) == IG_PB_OK); return v;
}
static void guards(simulated_flash *s) {
    for (unsigned i = 0; i < 256; ++i) { CHECK(s->guarded[i] == 0x5A); CHECK(s->guarded[256 + IG_PB_BANK_SIZE + i] == 0x5A); }
}
static void put16(uint8_t *p, uint16_t v) { p[0] = (uint8_t)v; p[1] = (uint8_t)(v >> 8); }
static void put32(uint8_t *p, uint32_t v) { for (unsigned i = 0; i < 4; ++i) p[i] = (uint8_t)(v >> (8 * i)); }
static void put64(uint8_t *p, uint64_t v) { for (unsigned i = 0; i < 8; ++i) p[i] = (uint8_t)(v >> (8 * i)); }
static void reseal(uint8_t *r) {
    put32(r + 36, ig_pb_crc32(r, 36));
    put32(r + IG_PB_COMMIT_OFFSET + 24, ig_pb_crc32(r, IG_PB_COMMIT_OFFSET));
    put32(r + IG_PB_COMMIT_OFFSET + 28, ig_pb_crc32(r + IG_PB_COMMIT_OFFSET, 28));
}
static unsigned latest(simulated_flash *s, unsigned profile) {
    ig_pb_bank b = bank(s); ig_pb_scan scan;
    CHECK(ig_pb_scan_bank(&b, &scan) == IG_PB_OK); CHECK(scan.latest[profile - 1] >= 0);
    return (unsigned)scan.latest[profile - 1];
}
static void assert_no_save(simulated_flash *s, ig_pb_result expected) {
    uint8_t before[IG_PB_BANK_SIZE]; memcpy(before, s->guarded + 256, sizeof(before));
    unsigned before_phase = s->phase;
    CHECK(save(s, 1, 2, 99) == expected); CHECK(s->phase == before_phase);
    CHECK(memcmp(before, s->guarded + 256, sizeof(before)) == 0);
}
static void fallback_tests(void) {
    initialize(&f); memset(legacy_data, 0x33, sizeof(legacy_data)); legacy_data[0] = 0xA5;
    for (unsigned p = 1; p <= IG_PB_PROFILE_COUNT; ++p) {
        ig_pb_view v = read_profile(&f, (uint8_t)p);
        CHECK(v.source == IG_PB_SOURCE_LEGACY && v.pair_state == IG_PB_PAIR_EMPTY && v.generation == 0 && v.payload == legacy_data);
    }
    CHECK(f.phase == 0 && legacy_reads == 3);
    ig_pb_bank b = bank(&f); ig_pb_view v; ig_pb_io writer = io(&f);
    b.fallback = NULL; CHECK(ig_pb_read_profile(&b, 1, &v) == IG_PB_ERR_NO_FALLBACK);
    memset(payload, 0xA5, sizeof(payload));
    CHECK(ig_pb_save_profile(&b, &writer, 1, 0, 7, payload, sizeof(payload)) == IG_PB_ERR_NO_FALLBACK);
    b = bank(&f); legacy_data[0] = 0; CHECK(ig_pb_read_profile(&b, 1, &v) == IG_PB_ERR_NO_FALLBACK);
    CHECK(f.phase == 0); legacy_data[0] = 0xA5;
    f.guarded[256 + IG_PB_RECORD_SIZE + 41] = 0; // An interrupted first save in B.
    CHECK(ig_pb_read_profile(&b, 1, &v) == IG_PB_OK && v.source == IG_PB_SOURCE_DEGRADED_LEGACY && v.pair_state == IG_PB_PAIR_DEGRADED);
    CHECK(f.phase == 0); CHECK(save(&f, 1, 0, 11) == IG_PB_OK);
    CHECK(latest(&f, 1) == 1 && read_profile(&f, 1).source == IG_PB_SOURCE_RECORD);
    CHECK(read_profile(&f, 2).source == IG_PB_SOURCE_LEGACY && read_profile(&f, 3).source == IG_PB_SOURCE_LEGACY);
    // Two damaged halves have no committed profile; validated fallback remains
    // explicitly degraded. The codec never invents a committed generation.
    initialize(&f); f.guarded[256 + 33] = f.guarded[256 + IG_PB_RECORD_SIZE + 33] = 0;
    CHECK(read_profile(&f, 1).source == IG_PB_SOURCE_DEGRADED_LEGACY);
    CHECK(save(&f, 1, 0, 12) == IG_PB_OK); CHECK(read_profile(&f, 1).generation == 1);
    guards(&f);
}
static void build_baseline(void) {
    initialize(&f);
    for (uint8_t p = 1; p <= 3; ++p) {
        CHECK(save(&f, p, 0, (uint8_t)(p * 11)) == IG_PB_OK);
        CHECK(save(&f, p, 1, (uint8_t)(p * 17)) == IG_PB_OK);
    }
    ig_pb_bank b = bank(&f); ig_pb_scan scan;
    CHECK(ig_pb_scan_bank(&b, &scan) == IG_PB_OK && scan.write_status == IG_PB_OK);
    for (unsigned i = 0; i < IG_PB_RECORD_COUNT; ++i) CHECK(scan.state[i] == IG_PB_VALID && scan.metadata[i].profile == i / 2 + 1);
    reboot(&f); baseline = f;
}
static void fault_sweep(const simulated_flash *initial, uint8_t profile, uint64_t generation) {
    ig_pb_bank base_bank = bank((simulated_flash *)initial); ig_pb_scan selected;
    CHECK(ig_pb_scan_bank(&base_bank, &selected) == IG_PB_OK);
    copy = *initial; reboot(&copy); CHECK(save(&copy, profile, generation, 0x66) == IG_PB_OK);
    unsigned phase_count = copy.phase; CHECK(phase_count == 128);
    for (unsigned cut = 1; cut <= phase_count; ++cut) for (unsigned prefix = 0; prefix <= IG_PB_PAGE_SIZE; ++prefix) {
        f = *initial; reboot(&f); f.fail_phase = cut; f.prefix = prefix;
        CHECK(save(&f, profile, generation, 0x66) == IG_PB_ERR_IO);
        CHECK(f.phase == cut && f.dead); ++fault_cases;
        for (unsigned p = 0; p < IG_PB_PROFILE_COUNT; ++p) {
            if (selected.latest[p] >= 0) {
                size_t offset = (unsigned)selected.latest[p] * IG_PB_RECORD_SIZE;
                CHECK(memcmp(base_bank.bytes + offset, f.guarded + 256 + offset, IG_PB_RECORD_SIZE) == 0);
            }
            ig_pb_view recovered = read_profile(&f, (uint8_t)(p + 1));
            uint64_t old = selected.latest[p] < 0 ? 0 : selected.metadata[selected.latest[p]].generation;
            CHECK(recovered.generation == old || (p + 1 == profile && recovered.generation == old + 1));
            if (p + 1 != profile) {
                CHECK(recovered.generation == old);
                CHECK(memcmp(base_bank.bytes + p * 2 * IG_PB_RECORD_SIZE,
                             f.guarded + 256 + p * 2 * IG_PB_RECORD_SIZE, 2 * IG_PB_RECORD_SIZE) == 0);
            } else if (recovered.generation == old + 1) CHECK(recovered.payload[1] == 0x66);
        }
        CHECK(memcmp(f.guarded, initial->guarded, 256) == 0);
        CHECK(memcmp(f.guarded + 256 + IG_PB_BANK_SIZE, initial->guarded + 256 + IG_PB_BANK_SIZE, 256) == 0);
        // New user action after a fresh read, not an automatic transport retry.
        // Every torn result must permit another explicit save without a format
        // operation, and an already committed result advances from its new gen.
        ig_pb_view recovered = read_profile(&f, profile);
        reboot(&f); CHECK(save(&f, profile, recovered.generation, 0x77) == IG_PB_OK); ++explicit_retries;
        CHECK(read_profile(&f, profile).generation == recovered.generation + 1);
        CHECK(read_profile(&f, profile).payload[1] == 0x77);
        for (unsigned p = 0; p < IG_PB_PROFILE_COUNT; ++p) if (p + 1 != profile)
            CHECK(memcmp(base_bank.bytes + p * 2 * IG_PB_RECORD_SIZE,
                         f.guarded + 256 + p * 2 * IG_PB_RECORD_SIZE, 2 * IG_PB_RECORD_SIZE) == 0);
    }
}
static void rejection_tests(void) {
    ig_pb_scan scan; ig_pb_view view;
    f = baseline; unsigned record = latest(&f, 1); uint8_t *r = f.guarded + 256 + record * IG_PB_RECORD_SIZE;
    put16(r + 8, IG_PB_FORMAT + 1); reseal(r);
    ig_pb_bank b = bank(&f); CHECK(ig_pb_scan_bank(&b, &scan) == IG_PB_OK && scan.write_status == IG_PB_ERR_FUTURE);
    CHECK(ig_pb_read_profile(&b, 1, &view) == IG_PB_ERR_FUTURE); assert_no_save(&f, IG_PB_ERR_FUTURE);
    f = baseline; r = f.guarded + 256 + record * IG_PB_RECORD_SIZE;
    put16(r + 12, 17); put16(r + IG_PB_COMMIT_OFFSET + 10, 17); reseal(r);
    CHECK(ig_pb_read_profile(&b, 1, &view) == IG_PB_ERR_FOREIGN); assert_no_save(&f, IG_PB_ERR_FOREIGN);
    f = baseline; memcpy(f.guarded + 256 + (record ^ 1u) * IG_PB_RECORD_SIZE, f.guarded + 256 + record * IG_PB_RECORD_SIZE, IG_PB_RECORD_SIZE);
    CHECK(ig_pb_read_profile(&b, 1, &view) == IG_PB_ERR_AMBIGUOUS); assert_no_save(&f, IG_PB_ERR_AMBIGUOUS);
    // A valid profile moved to another pair cannot be reclaimed as damaged.
    f = baseline; memcpy(f.guarded + 256 + 2 * IG_PB_RECORD_SIZE, f.guarded + 256 + record * IG_PB_RECORD_SIZE, IG_PB_RECORD_SIZE);
    CHECK(ig_pb_scan_bank(&b, &scan) == IG_PB_OK && scan.state[2] == IG_PB_FOREIGN); assert_no_save(&f, IG_PB_ERR_FOREIGN);
    for (unsigned variant = 0; variant < 9; ++variant) {
        f = baseline; r = f.guarded + 256 + record * IG_PB_RECORD_SIZE;
        switch (variant) {
            case 0: r[IG_PB_PAGE_SIZE + 17] ^= 1; break;
            case 1: put32(r + 20, IG_PB_PAYLOAD_CAPACITY + 1); reseal(r); break;
            case 2: r[14] = 4; reseal(r); break;
            case 3: put64(r + 24, 0); put64(r + IG_PB_COMMIT_OFFSET + 16, 0); reseal(r); break;
            case 4: r[70] = 0; reseal(r); break;
            case 5: put16(r + 16, 8); reseal(r); break;
            case 6: r[IG_PB_COMMIT_OFFSET + 12] = 2; reseal(r); break;
            case 7: r[IG_PB_COMMIT_OFFSET + 70] = 0; break;
            case 8: put32(r + 20, 0); reseal(r); break;
        }
        CHECK(ig_pb_scan_bank(&b, &scan) == IG_PB_OK);
        if (variant == 2 || variant == 6) { CHECK(scan.state[record] == IG_PB_FOREIGN); assert_no_save(&f, IG_PB_ERR_FOREIGN); }
        else {
            CHECK(scan.state[record] == IG_PB_TORN && scan.pair[0] == IG_PB_PAIR_RECOVERED);
            CHECK(read_profile(&f, 1).generation == 1); CHECK(f.phase == 0);
            CHECK(save(&f, 1, 1, 88) == IG_PB_OK); CHECK(read_profile(&f, 1).generation == 2);
        }
    }
    f = baseline; r = f.guarded + 256 + record * IG_PB_RECORD_SIZE;
    put64(r + 24, UINT64_MAX); put64(r + IG_PB_COMMIT_OFFSET + 16, UINT64_MAX); reseal(r);
    ig_pb_io v = io(&f); unsigned before = f.phase;
    CHECK(ig_pb_save_profile(&b, &v, 1, UINT64_MAX, 7, payload, sizeof(payload)) == IG_PB_ERR_ROLLOVER && f.phase == before);
    f = baseline; CHECK(save(&f, 1, 1, 55) == IG_PB_ERR_CONFLICT && f.phase == 0);
    b.validate = NULL; CHECK(ig_pb_scan_bank(&b, &scan) == IG_PB_ERR_NO_VALIDATOR);
    b = bank(&f); b.length--; CHECK(ig_pb_scan_bank(&b, &scan) == IG_PB_ERR_ARGUMENT);
    b = bank(&f); b.board = 0; CHECK(ig_pb_scan_bank(&b, &scan) == IG_PB_ERR_ARGUMENT);
    b = bank(&f); CHECK(ig_pb_read_profile(&b, 0, &view) == IG_PB_ERR_ARGUMENT);
    CHECK(ig_pb_save_profile(&b, &v, 4, 0, 7, payload, sizeof(payload)) == IG_PB_ERR_ARGUMENT);
    CHECK(ig_pb_save_profile(&b, &v, 1, 2, 7, b.bytes, 64) == IG_PB_ERR_ARGUMENT);
    payload[0] = 0; CHECK(ig_pb_save_profile(&b, &v, 1, 2, 7, payload, 64) == IG_PB_ERR_PAYLOAD);
    CHECK(f.phase == 0); guards(&f);
}
static void repeated_saves(void) {
    f = baseline; uint64_t generations[3] = {2, 2, 2};
    for (unsigned n = 0; n < 300; ++n) {
        unsigned p = n % 3;
        CHECK(save(&f, (uint8_t)(p + 1), generations[p], (uint8_t)n) == IG_PB_OK); ++generations[p];
        for (unsigned q = 0; q < 3; ++q) CHECK(read_profile(&f, (uint8_t)(q + 1)).generation == generations[q]);
    }
    guards(&f);
    // Interrupted retries themselves can be interrupted. Fixed ownership and
    // the unchanged valid half still permit the following explicit save.
    for (unsigned cut = 1; cut <= 128; ++cut) {
        f = baseline; f.fail_phase = cut; f.prefix = 127;
        CHECK(save(&f, 3, 2, 50) == IG_PB_ERR_IO);
        uint64_t gen = read_profile(&f, 3).generation;
        reboot(&f); f.fail_phase = 129 - cut; f.prefix = 129;
        CHECK(save(&f, 3, gen, 51) == IG_PB_ERR_IO);
        gen = read_profile(&f, 3).generation;
        reboot(&f); CHECK(save(&f, 3, gen, 52) == IG_PB_OK);
        CHECK(memcmp(f.guarded + 256, baseline.guarded + 256, 4 * IG_PB_RECORD_SIZE) == 0);
    }
}
static void integrity_tests(void) {
    unsigned record = latest(&baseline, 1);
    ig_pb_bank b = bank(&f); ig_pb_scan scan; ig_pb_view view;
    for (unsigned byte = 0; byte < IG_PB_RECORD_SIZE; ++byte) {
        f = baseline; f.guarded[256 + record * IG_PB_RECORD_SIZE + byte] ^= 1u;
        CHECK(ig_pb_scan_bank(&b, &scan) == IG_PB_OK && scan.state[record] == IG_PB_TORN);
        CHECK(ig_pb_read_profile(&b, 1, &view) == IG_PB_OK && view.generation == 1 && view.pair_state == IG_PB_PAIR_RECOVERED);
    }
    // Header-only and commit-only recognized future/foreign envelopes protect
    // themselves even when an interrupted record has no other valid envelope.
    for (unsigned mode = 0; mode < 4; ++mode) {
        f = baseline; uint8_t *r = f.guarded + 256 + record * IG_PB_RECORD_SIZE;
        if (mode < 2) {
            if (mode == 0) put16(r + 8, 2); else put16(r + 12, 17);
            reseal(r); memset(r + IG_PB_COMMIT_OFFSET, 255, IG_PB_PAGE_SIZE);
        } else {
            if (mode == 2) put16(r + IG_PB_COMMIT_OFFSET + 8, 2); else r[IG_PB_COMMIT_OFFSET + 12] = 3;
            reseal(r); memset(r, 255, IG_PB_PAGE_SIZE);
        }
        assert_no_save(&f, mode % 2 == 0 ? IG_PB_ERR_FUTURE : IG_PB_ERR_FOREIGN);
    }
    f = baseline; uint8_t *r = f.guarded + 256 + record * IG_PB_RECORD_SIZE;
    put64(r + 24, UINT64_MAX - 1); put64(r + IG_PB_COMMIT_OFFSET + 16, UINT64_MAX - 1); reseal(r);
    CHECK(save(&f, 1, UINT64_MAX - 1, 12) == IG_PB_OK && read_profile(&f, 1).generation == UINT64_MAX);
    unsigned before = f.phase; CHECK(save(&f, 1, UINT64_MAX, 13) == IG_PB_ERR_ROLLOVER && f.phase == before);
    f = baseline; f.corrupt_phase = 68;
    CHECK(save(&f, 1, 2, 87) == IG_PB_ERR_VERIFY && f.phase == 126); CHECK(read_profile(&f, 1).generation == 2);
    reboot(&f); CHECK(save(&f, 1, 2, 88) == IG_PB_OK);
    f = baseline; f.corrupt_phase = 128;
    CHECK(save(&f, 1, 2, 87) == IG_PB_ERR_VERIFY); CHECK(read_profile(&f, 1).generation == 2);
    reboot(&f); CHECK(save(&f, 1, 2, 88) == IG_PB_OK);
    const uint32_t lengths[] = {8, 63, 255, 256, 257, 7272, 7648, IG_PB_PAYLOAD_CAPACITY};
    for (unsigned n = 0; n < sizeof(lengths) / sizeof(lengths[0]); ++n) {
        initialize(&f); ig_pb_io v = io(&f); memset(payload, 0x27, sizeof(payload)); payload[0] = 0xA5;
        CHECK(ig_pb_save_profile(&b, &v, 3, 0, 7, payload, lengths[n]) == IG_PB_OK);
        CHECK(ig_pb_read_profile(&b, 3, &view) == IG_PB_OK && view.length == lengths[n] && memcmp(payload, view.payload, view.length) == 0);
        if (lengths[n] < IG_PB_PAYLOAD_CAPACITY) {
            unsigned target = latest(&f, 3); f.guarded[256 + target * IG_PB_RECORD_SIZE + IG_PB_PAGE_SIZE + lengths[n]] = 0;
            reseal(f.guarded + 256 + target * IG_PB_RECORD_SIZE);
            CHECK(ig_pb_scan_bank(&b, &scan) == IG_PB_OK && scan.state[target] == IG_PB_TORN);
        }
    }
}
int main(void) {
    CHECK(ig_pb_crc32((const uint8_t *)"123456789", 9) == UINT32_C(0xCBF43926));
    fallback_tests(); build_baseline(); fault_sweep(&baseline, 1, 2);
    simulated_flash empty; initialize(&empty); fault_sweep(&empty, 2, 0);
    rejection_tests(); repeated_saves(); integrity_tests();
    printf("{\"passed\":true,\"checks\":%lu,\"faultCases\":%lu,\"explicitRetries\":%lu,\"validationCalls\":%u,\"legacyReads\":%u,\"hardwareAccess\":false}\n", checks, fault_cases, explicit_retries, validation_calls, legacy_reads);
    return 0;
}
