#pragma once
#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>
#ifndef IG_BOARD
#define IG_BOARD 2
#endif
#define QMK_MCU_FAMILY_WB32 1
#ifndef WB32_FLASH_SECTOR_SIZE
#define WB32_FLASH_SECTOR_SIZE 256U
#endif
#define FLASH_ATTR_ERASED_IS_ONE 1U
#define FLASH_NO_ERROR 0
typedef struct { int unused; } BaseFlash;
typedef struct { uint32_t attributes,size,sectors_size,sectors_count,page_size; const void *sectors; } flash_descriptor_t;
extern BaseFlash EFLD1;
const flash_descriptor_t *flashGetDescriptor(BaseFlash *flash);
int flashProgram(BaseFlash *flash,uint32_t offset,size_t count,const uint8_t *bytes);
