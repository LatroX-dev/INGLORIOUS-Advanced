// SPDX-License-Identifier: GPL-2.0-or-later
#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include "wear_leveling.h"

// Preserve the original slot locations for every existing board. Only the six
// GMMK3 boards use the remaining 1 KiB of the same 16 KiB logical EEPROM.
#define IG_STORAGE_BASE 1024U
#define IG_STORAGE_HEADER_BYTES 32U
#if IG_BOARD == 10 || IG_BOARD == 11 || IG_BOARD == 12 || IG_BOARD == 13 || IG_BOARD == 14 || IG_BOARD == 15
#    define IG_STORAGE_SLOT_BYTES 7680U
#elif IG_BOARD == 1 || IG_BOARD == 2 || IG_BOARD == 3 || IG_BOARD == 4 || IG_BOARD == 5 || IG_BOARD == 6 || IG_BOARD == 7 || IG_BOARD == 8 || IG_BOARD == 9
#    define IG_STORAGE_SLOT_BYTES 7168U
#else
#    error "Storage geometry requires an explicitly supported INGLORIOUS board"
#endif
#define IG_STORAGE_PAYLOAD_BYTES (IG_STORAGE_SLOT_BYTES - IG_STORAGE_HEADER_BYTES)
#define IG_STORAGE_PAYLOAD0 (IG_STORAGE_BASE + IG_STORAGE_HEADER_BYTES)
#define IG_STORAGE_PAYLOAD1 (IG_STORAGE_BASE + IG_STORAGE_SLOT_BYTES + IG_STORAGE_HEADER_BYTES)

// A borrowed payload is RAM only. Ordinary EEPROM writes/erase/init are blocked
// until finish. The active slot must never be passed to stage_begin.
const uint8_t *ig_storage_view(uint32_t address, size_t length);
uint8_t *ig_storage_stage_begin(uint32_t payload_address, size_t length);
// Strict sequence: invalidate marker(4), entire payload, header(28), marker(4).
// The payload is forced into the log even though it already equals RAM cache.
wear_leveling_status_t ig_storage_stage_write(uint32_t address, const void *value, size_t length);
// false: abort with strictly read-only backing replay; true: ordinary post-write
// recovery/replay, which can consolidate as in the pre-existing commit path.
wear_leveling_status_t ig_storage_stage_finish(bool commit_attempted);
