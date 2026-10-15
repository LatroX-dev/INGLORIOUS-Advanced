// Copyright 2026 INGLORIOUS contributors
// SPDX-License-Identifier: GPL-2.0-or-later
// Optimization of QMK's weak consolidated-snapshot writer on the listed WB32 boards.
// The WB32 driver still erases, programs and verifies each 256-byte page.
#include <hal.h>
#include <stdint.h>
#include <string.h>
#include "wear_leveling.h"
#include "wear_leveling_efl_config.h"
#include "wear_leveling_internal.h"

#if !defined(QMK_MCU_FAMILY_WB32) || (IG_BOARD != 2 && (IG_BOARD < 4 || IG_BOARD > 16))
#    error "WB32 bulk writer is restricted to the explicitly listed WB32 configurations"
#endif
#if IG_BOARD == 16 && (!defined(IG_RETAINED_LOADER_BUILD) || IG_RETAINED_LOADER_BUILD != 1)
#    error "Board 16 requires its retained-loader build"
#endif
#if defined(WB32_FLASH_PROGRAM_NO_ERASE)
#    error "INGLORIOUS requires the WB32 erase-before-program implementation"
#endif
#if defined(WEAR_LEVELING_EFL_FIRST_SECTOR)
#    error "The bulk writer requires the default trailing-flash placement"
#endif
#if defined(WEAR_LEVELING_EFL_OMIT_LAST_SECTOR_COUNT) && WEAR_LEVELING_EFL_OMIT_LAST_SECTOR_COUNT != 0
#    error "The bulk writer requires the default trailing-flash placement"
#endif

_Static_assert(BACKING_STORE_WRITE_SIZE == 8, "Reviewed WB32 write width");
_Static_assert(WB32_FLASH_SECTOR_SIZE == 256, "Reviewed WB32 page size");
#if IG_BOARD == 16
_Static_assert(WEAR_LEVELING_EFL_FLASH_SIZE == 262144, "Board 16 requires 256 KiB flash");
#else
_Static_assert(WEAR_LEVELING_EFL_FLASH_SIZE == 131072, "Existing WB32 boards require 128 KiB flash");
#endif
_Static_assert(WEAR_LEVELING_BACKING_SIZE == 32768, "Reviewed trailing storage range");

bool backing_store_write_bulk(uint32_t address, backing_store_int_t *values, size_t item_count) {
    // Match the weak implementation's empty-write behavior, without touching EFL.
    if (item_count == 0) return true;
    if (values == NULL || address % BACKING_STORE_WRITE_SIZE != 0 ||
        (uintptr_t)values % _Alignof(backing_store_int_t) != 0 ||
        address >= WEAR_LEVELING_BACKING_SIZE ||
        item_count > (WEAR_LEVELING_BACKING_SIZE - address) / BACKING_STORE_WRITE_SIZE) return false;

    BaseFlash *flash = (BaseFlash *)&EFLD1;
    const flash_descriptor_t *desc = flashGetDescriptor(flash);
    const uint32_t base = WEAR_LEVELING_EFL_FLASH_SIZE - WEAR_LEVELING_BACKING_SIZE;
    if (desc == NULL || desc->size < WEAR_LEVELING_EFL_FLASH_SIZE ||
        desc->sectors != NULL || desc->page_size != BACKING_STORE_WRITE_SIZE ||
        desc->sectors_size != WB32_FLASH_SECTOR_SIZE ||
        desc->sectors_count < WEAR_LEVELING_EFL_FLASH_SIZE / WB32_FLASH_SECTOR_SIZE) return false;

    const bool invert = (desc->attributes & FLASH_ATTR_ERASED_IS_ONE) != 0;
    uint8_t page[WB32_FLASH_SECTOR_SIZE];
    while (item_count != 0) {
        // Partial pages retain the existing scalar implementation and its checks.
        if (address % WB32_FLASH_SECTOR_SIZE != 0 ||
            item_count < WB32_FLASH_SECTOR_SIZE / BACKING_STORE_WRITE_SIZE) {
            if (!backing_store_write(address, *values)) return false;
            address += BACKING_STORE_WRITE_SIZE;
            ++values;
            --item_count;
            continue;
        }
        memcpy(page, values, sizeof(page));
        if (invert) {
            for (size_t i = 0; i < sizeof(page); ++i) page[i] = (uint8_t)~page[i];
        }
        // Existing EFL driver performs erase + program + full-page verification.
        // Propagate any failure immediately; never retry or advance after failure.
        if (flashProgram(flash, base + address, sizeof(page), page) != FLASH_NO_ERROR) return false;
        address += sizeof(page);
        values += sizeof(page) / BACKING_STORE_WRITE_SIZE;
        item_count -= sizeof(page) / BACKING_STORE_WRITE_SIZE;
    }
    return true;
}
