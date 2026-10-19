// SPDX-License-Identifier: GPL-2.0-or-later
#include QMK_KEYBOARD_H
#include "ig_profile_flash.h"
#if IG_PROFILE_BANK_ENABLE
#include "hal.h"
#include <string.h>
_Static_assert(IG_PROFILE_BANK_END - IG_PROFILE_BANK_OFFSET == IG_PB_BANK_SIZE, "Fixed48KiB bank");
_Static_assert(IG_PB_PAGE_SIZE == 256 && IG_PROFILE_BANK_END < IG_PROFILE_IDENTITY_OFFSET, "Bounded board16 pages");
static bool ig_profile_flash_open;
static BaseFlash *ig_profile_flash_driver(void) { return (BaseFlash *)&EFLD1; }
bool ig_profile_flash_ready(void) {
    const flash_descriptor_t *d = flashGetDescriptor(ig_profile_flash_driver());
    return d && d->address == (uint8_t *)0x08000000U && d->size == 262144U &&
           (d->attributes & FLASH_ATTR_MEMORY_MAPPED) && !d->sectors &&
           d->sectors_size == 256U && d->sectors_count == 1024U;
}
const uint8_t *ig_profile_flash_bytes(void) { return ig_profile_flash_ready() ? (const uint8_t *)(0x08000000U + IG_PROFILE_BANK_OFFSET) : NULL; }
const uint8_t *ig_profile_flash_identity(void) { return ig_profile_flash_ready() ? (const uint8_t *)(0x08000000U + IG_PROFILE_IDENTITY_OFFSET) : NULL; }
bool ig_profile_flash_begin(void) {
    if (ig_profile_flash_open || !ig_profile_flash_ready()) return false;
    if (eflStart(&EFLD1, NULL) != HAL_RET_SUCCESS) return false;
    ig_profile_flash_open = true;
    return true;
}
void ig_profile_flash_end(void) {
    if (ig_profile_flash_open) eflStop(&EFLD1);
    ig_profile_flash_open = false;
}
static bool ig_profile_flash_program(void *context, size_t offset, const uint8_t page[IG_PB_PAGE_SIZE]) {
    (void)context;
    if (!ig_profile_flash_open || !page || !ig_profile_flash_ready() ||
        offset % IG_PB_PAGE_SIZE || offset > IG_PB_BANK_SIZE - IG_PB_PAGE_SIZE) return false;
    const uint32_t address = IG_PROFILE_BANK_OFFSET + (uint32_t)offset;
    if (address < IG_PROFILE_BANK_OFFSET || address + IG_PB_PAGE_SIZE > IG_PROFILE_BANK_END) return false;
    if (flashProgram(ig_profile_flash_driver(), address, IG_PB_PAGE_SIZE, page) != FLASH_NO_ERROR) return false;
    return memcmp((const void *)(uintptr_t)(0x08000000U + address), page, IG_PB_PAGE_SIZE) == 0;
}
static bool ig_profile_flash_empty(void *context, size_t offset) {
    // WB32 erases physically to00. A complete FF page establishes the codec's
    // logical empty state. Its pinned driver erases/programs exactly one page;
    // power loss at either phase leaves only the inactive record interrupted.
    uint8_t empty[IG_PB_PAGE_SIZE]; memset(empty, 255, sizeof(empty));
    return ig_profile_flash_program(context, offset, empty);
}
const ig_pb_io *ig_profile_flash_io(void) {
    static const ig_pb_io io = {NULL, ig_profile_flash_empty, ig_profile_flash_program};
    return &io;
}
#endif
