// SPDX-License-Identifier: GPL-2.0-or-later
#ifndef IG_PROFILE_BANK_H
#define IG_PROFILE_BANK_H
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

// Pure codec: no device access, absolute addresses, boot repair or allocator.
// The caller supplies an approved, exclusively owned, stable physical mapping.
#define IG_PB_PROFILE_COUNT 3u
#define IG_PB_RECORD_COUNT 6u
#define IG_PB_PAGE_SIZE 256u
#define IG_PB_RECORD_SIZE 8192u
#define IG_PB_BANK_SIZE (IG_PB_RECORD_COUNT * IG_PB_RECORD_SIZE)
#define IG_PB_PAYLOAD_CAPACITY (IG_PB_RECORD_SIZE - 2u * IG_PB_PAGE_SIZE)
#define IG_PB_COMMIT_OFFSET (IG_PB_RECORD_SIZE - IG_PB_PAGE_SIZE)
#define IG_PB_FORMAT 1u

typedef enum {
    IG_PB_OK, IG_PB_ERR_ARGUMENT, IG_PB_ERR_NO_VALIDATOR, IG_PB_ERR_PAYLOAD,
    IG_PB_ERR_FUTURE, IG_PB_ERR_FOREIGN, IG_PB_ERR_AMBIGUOUS,
    IG_PB_ERR_CONFLICT, IG_PB_ERR_ROLLOVER, IG_PB_ERR_IO,
    IG_PB_ERR_VERIFY, IG_PB_ERR_NO_FALLBACK
} ig_pb_result;
typedef enum { IG_PB_EMPTY, IG_PB_VALID, IG_PB_TORN, IG_PB_FUTURE, IG_PB_FOREIGN } ig_pb_record_state;
typedef enum {
    IG_PB_PAIR_EMPTY, IG_PB_PAIR_READY, IG_PB_PAIR_RECOVERED,
    IG_PB_PAIR_DEGRADED, IG_PB_PAIR_AMBIGUOUS, IG_PB_PAIR_BLOCKED
} ig_pb_pair_state;
typedef enum { IG_PB_SOURCE_RECORD, IG_PB_SOURCE_LEGACY, IG_PB_SOURCE_DEGRADED_LEGACY } ig_pb_source;
typedef struct {
    uint16_t board, schema;
    uint8_t profile; // 1..3; each profile still has its own normal layers.
    uint32_t length;
    uint64_t generation; // 0 is reserved for the read-only fallback.
} ig_pb_metadata;
typedef struct {
    const uint8_t *payload;
    uint32_t length;
    uint16_t schema;
} ig_pb_fallback;
typedef bool (*ig_pb_validator)(void *context, const uint8_t *payload,
                                uint32_t length, uint16_t schema);
typedef bool (*ig_pb_fallback_reader)(void *context, ig_pb_fallback *fallback);
typedef struct {
    const uint8_t *bytes; // Exactly 48 KiB. Updated physical reads after callbacks.
    size_t length;
    uint16_t board;
    ig_pb_validator validate;
    void *validation_context;
    // Optional, immutable legacy source, not a second copy of the bank.
    // Needed to read or explicitly initialize any profile with no valid half.
    ig_pb_fallback_reader fallback;
    void *fallback_context;
} ig_pb_bank;
typedef struct {
    ig_pb_record_state state[IG_PB_RECORD_COUNT];
    ig_pb_metadata metadata[IG_PB_RECORD_COUNT];
    int latest[IG_PB_PROFILE_COUNT]; // -1 absent; -2 ambiguous/blocked.
    ig_pb_pair_state pair[IG_PB_PROFILE_COUNT];
    ig_pb_result write_status; // Future/foreign/ambiguous block all writes.
} ig_pb_scan;
typedef struct {
    const uint8_t *payload;
    uint32_t length;
    uint16_t schema;
    uint64_t generation;
    ig_pb_source source;
    ig_pb_pair_state pair_state;
} ig_pb_view;
typedef struct {
    void *context;
    // Bank-relative, 256-byte aligned. Synchronous; success means completed.
    // Program may erase/rewrite its complete page; commit has a separate page.
    bool (*erase_page)(void *context, size_t offset);
    bool (*program_page)(void *context, size_t offset, const uint8_t page[IG_PB_PAGE_SIZE]);
} ig_pb_io;

uint32_t ig_pb_crc32(const uint8_t *data, size_t length);
ig_pb_result ig_pb_scan_bank(const ig_pb_bank *bank, ig_pb_scan *scan);
// Read-only. IG_PB_OK means the returned source is usable, not necessarily a
// committed profile: callers must preserve source/pair_state in their status.
ig_pb_result ig_pb_read_profile(const ig_pb_bank *bank, uint8_t profile, ig_pb_view *view);
// Explicit user save only. The target is a profile ID, never switch position.
// Requires exclusive lock, immutable staging and fallback for the whole call.
// Torn inactive halves are reclaimable; all current records stay untouched.
// After any uncertain result, re-read. Never automatically repeat the save.
ig_pb_result ig_pb_save_profile(const ig_pb_bank *bank, const ig_pb_io *io, uint8_t profile,
                                uint64_t expected_generation, uint16_t schema,
                                const uint8_t *payload, uint32_t length);
#endif
