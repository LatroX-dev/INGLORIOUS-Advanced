// SPDX-License-Identifier: GPL-2.0-or-later
#pragma once
#include <stddef.h>
#include <stdint.h>
typedef struct { unsigned state; } BaseFlash;
typedef BaseFlash EFlashDriver;
typedef struct {
    uint32_t attributes;
    size_t page_size, sectors_count, sectors_size;
    const void *sectors;
    uint8_t *address;
    size_t size;
} flash_descriptor_t;
enum { FLASH_ATTR_MEMORY_MAPPED = 1, HAL_RET_SUCCESS = 0, FLASH_NO_ERROR = 0 };
extern EFlashDriver EFLD1;
const flash_descriptor_t *flashGetDescriptor(BaseFlash *flash);
int eflStart(EFlashDriver *flash, const void *configuration);
void eflStop(EFlashDriver *flash);
int flashProgram(BaseFlash *flash, uint32_t offset, size_t length, const uint8_t *page);
