// SPDX-License-Identifier: GPL-2.0-or-later
// The real adapter is linked against these HAL mocks. Its fixed address is
// reserved as ordinary memory in this process only; no physical device opens.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "profile_flash_mocks/hal.h"
#include "../inglorious/ig_profile_flash.h"

static unsigned checks, starts, stops, programs, physical_erase_phases, physical_program_phases;
#define CHECK(x) do { ++checks; if (!(x)) { fprintf(stderr, "FAIL line %d: %s\n", __LINE__, #x); exit(1); } } while (0)
EFlashDriver EFLD1;
static flash_descriptor_t descriptor;
static uint8_t *memory;
static bool absent_descriptor, start_failure, program_failure, silent_corruption;
static uint32_t last_offset;
const flash_descriptor_t *flashGetDescriptor(BaseFlash *driver) {
    CHECK(driver == &EFLD1); return absent_descriptor ? NULL : &descriptor;
}
int eflStart(EFlashDriver *driver, const void *configuration) {
    CHECK(driver == &EFLD1 && configuration == NULL); ++starts;
    if (start_failure) return -1;
    driver->state = 1; return HAL_RET_SUCCESS;
}
void eflStop(EFlashDriver *driver) { CHECK(driver == &EFLD1 && driver->state == 1); ++stops; driver->state = 0; }
int flashProgram(BaseFlash *driver, uint32_t offset, size_t length, const uint8_t *page) {
    CHECK(driver == &EFLD1 && driver->state == 1 && page != NULL);
    CHECK(offset >= IG_PROFILE_BANK_OFFSET && offset <= IG_PROFILE_BANK_END - 256u && offset % 256u == 0 && length == 256u);
    ++programs; last_offset = offset;
    if (program_failure) return -1;
    memset(memory + offset, 0, length); ++physical_erase_phases;
    memcpy(memory + offset, page, length); ++physical_program_phases;
    if (silent_corruption) memory[offset + 97] ^= 1u;
    return FLASH_NO_ERROR;
}
static void good_descriptor(void) {
    descriptor = (flash_descriptor_t){FLASH_ATTR_MEMORY_MAPPED, 8, 1024, 256, NULL, memory, 262144};
    absent_descriptor = false;
}
static void no_writes_outside_bank(void) {
    for (uint32_t i = 0; i < IG_PROFILE_BANK_OFFSET; ++i) CHECK(memory[i] == 0x5A);
    for (uint32_t i = IG_PROFILE_BANK_END; i < 262144; ++i) CHECK(memory[i] == 0x5A);
}
int main(void) {
    memory = VirtualAlloc((void *)(uintptr_t)0x08000000u, 262144, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    CHECK(memory == (void *)(uintptr_t)0x08000000u);
    memset(memory, 0x5A, 262144); good_descriptor();
    const ig_pb_io *writer = ig_profile_flash_io(); CHECK(writer && writer->erase_page && writer->program_page);
    uint8_t page[256]; memset(page, 0x36, sizeof(page));
    CHECK(ig_profile_flash_ready());
    CHECK(ig_profile_flash_bytes() == memory + IG_PROFILE_BANK_OFFSET);
    CHECK(ig_profile_flash_identity() == memory + IG_PROFILE_IDENTITY_OFFSET);
    CHECK(!writer->erase_page(NULL, 0) && !writer->program_page(NULL, 0, page) && programs == 0);
    ig_profile_flash_end(); CHECK(stops == 0);
    for (unsigned variant = 0; variant < 7; ++variant) {
        good_descriptor();
        switch (variant) {
            case 0: absent_descriptor = true; break;
            case 1: descriptor.address = memory + 256; break;
            case 2: descriptor.size = 131072; break;
            case 3: descriptor.attributes = 0; break;
            case 4: descriptor.sectors = page; break;
            case 5: descriptor.sectors_size = 512; break;
            case 6: descriptor.sectors_count = 512; break;
        }
        unsigned before = starts;
        CHECK(!ig_profile_flash_ready() && !ig_profile_flash_begin() && starts == before);
        CHECK(!ig_profile_flash_bytes() && !ig_profile_flash_identity());
    }
    good_descriptor(); start_failure = true;
    CHECK(!ig_profile_flash_begin()); CHECK(!writer->program_page(NULL, 0, page));
    start_failure = false; CHECK(ig_profile_flash_begin());
    unsigned before = starts; CHECK(!ig_profile_flash_begin() && starts == before);
    const size_t invalid[] = {1, 255, 257, IG_PB_BANK_SIZE, IG_PB_BANK_SIZE + 256u, SIZE_MAX, SIZE_MAX - 255u};
    for (unsigned i = 0; i < sizeof(invalid) / sizeof(invalid[0]); ++i)
        CHECK(!writer->erase_page(NULL, invalid[i]) && !writer->program_page(NULL, invalid[i], page) && programs == 0);
    CHECK(!writer->program_page(NULL, 0, NULL) && programs == 0);
    descriptor.size = 131072; CHECK(!writer->program_page(NULL, 0, page) && programs == 0); good_descriptor();
    for (size_t offset = 0; offset < IG_PB_BANK_SIZE; offset += 256) {
        memset(page, (int)(offset / 256), sizeof(page));
        CHECK(writer->program_page(NULL, offset, page));
        CHECK(last_offset == IG_PROFILE_BANK_OFFSET + offset);
        CHECK(memcmp(memory + IG_PROFILE_BANK_OFFSET + offset, page, sizeof(page)) == 0);
        CHECK(writer->erase_page(NULL, offset));
        for (unsigned i = 0; i < 256; ++i) CHECK(memory[IG_PROFILE_BANK_OFFSET + offset + i] == 255);
    }
    CHECK(physical_erase_phases == 384 && physical_program_phases == 384);
    program_failure = true; CHECK(!writer->program_page(NULL, 256, page));
    program_failure = false; silent_corruption = true;
    CHECK(!writer->program_page(NULL, 256, page)); CHECK(!writer->erase_page(NULL, 512));
    silent_corruption = false;
    ig_profile_flash_end(); CHECK(stops == 1); ig_profile_flash_end(); CHECK(stops == 1);
    before = programs; CHECK(!writer->program_page(NULL, 0, page) && programs == before);
    CHECK(ig_profile_flash_begin()); CHECK(writer->erase_page(NULL, 256)); ig_profile_flash_end();
    CHECK(stops == 2); no_writes_outside_bank();
    CHECK(VirtualFree(memory, 0, MEM_RELEASE));
    printf("{\"passed\":true,\"checks\":%u,\"programCalls\":%u,\"hardwareAccess\":false,\"actualAdapter\":true}\n", checks, programs);
    return 0;
}
