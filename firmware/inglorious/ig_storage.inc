// SPDX-License-Identifier: GPL-2.0-or-later
// Included once in the pinned QMK wear_leveling.c translation unit by
// Patch-QmkStorage.ps1. No second cache or staging allocation.
_Static_assert(WEAR_LEVELING_LOGICAL_SIZE == 16384, "Existing logical EEPROM layout");
_Static_assert(WEAR_LEVELING_BACKING_SIZE == 32768, "Existing backing layout");
_Static_assert(IG_STORAGE_BASE + 2 * IG_STORAGE_SLOT_BYTES <= WEAR_LEVELING_LOGICAL_SIZE, "Slots stay inside logical EEPROM");
static uint16_t ig_storage_payload_address, ig_storage_payload_length;
static uint8_t ig_storage_phase; // 0=borrowed, 1..4=successful writes, 255=failed.
static uint64_t ig_storage_original_hash;

const uint8_t *ig_storage_view(uint32_t address, size_t length) {
    if (address > WEAR_LEVELING_LOGICAL_SIZE || length > WEAR_LEVELING_LOGICAL_SIZE - address) return NULL;
    return &wear_leveling.cache[address];
}

uint8_t *ig_storage_stage_begin(uint32_t address, size_t length) {
    if (ig_storage_borrowed || ig_storage_fault || !length || length > IG_STORAGE_PAYLOAD_BYTES ||
        (address != IG_STORAGE_PAYLOAD0 && address != IG_STORAGE_PAYLOAD1)) return NULL;
    ig_storage_borrowed = true;
    ig_storage_payload_address = (uint16_t)address;
    ig_storage_payload_length = (uint16_t)length;
    ig_storage_phase = 0;
    ig_storage_original_hash = fnv_64a_buf(wear_leveling.cache, WEAR_LEVELING_LOGICAL_SIZE, FNV1A_64_INIT);
    memset(&wear_leveling.cache[address], 0, length);
    return &wear_leveling.cache[address];
}

wear_leveling_status_t ig_storage_stage_write(uint32_t address, const void *value, size_t length) {
#if IG_PROFILE_BANK_ENABLE
    (void)address; (void)value; (void)length;
    return WEAR_LEVELING_FAILED; // Legacy backing remains immutable with hardware profiles.
#else
    if (!ig_storage_borrowed || ig_storage_fault || !value || ig_storage_phase > 3) return WEAR_LEVELING_FAILED;
    const uint8_t *bytes = value;
    uint32_t payload = ig_storage_payload_address;
    bool valid = false, force = false;
    switch (ig_storage_phase) {
        case 0:
            valid = address == payload - 4 && length == 4 &&
                bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0;
            break;
        case 1:
            valid = address == payload && length == ig_storage_payload_length && value == &wear_leveling.cache[payload];
            force = true;
            break;
        case 2:
            valid = address == payload - 32 && length == 28;
            break;
        case 3:
            valid = address == payload - 4 && length == 4 &&
                bytes[0] == 0xA5 && bytes[1] == 0x5A && bytes[2] == 0x5A && bytes[3] == 0xA5;
            break;
    }
    if (!valid) return WEAR_LEVELING_FAILED;
    // Invalidation updates RAM before any possible consolidation. The staged
    // payload can never be consolidated together with an old valid marker.
    wear_leveling_status_t status = ig_wear_leveling_write_internal(address, value, length, force);
    if (status == WEAR_LEVELING_FAILED) ig_storage_phase = 255;
    else ++ig_storage_phase;
    return status;
#endif
}

wear_leveling_status_t ig_storage_stage_finish(bool commit_attempted) {
    if (!ig_storage_borrowed) return WEAR_LEVELING_FAILED;
#if IG_PROFILE_BANK_ENABLE
    if (commit_attempted) return WEAR_LEVELING_FAILED;
#endif
    bool failed = ig_storage_phase == 255 || (commit_attempted && ig_storage_phase == 0) || (!commit_attempted && ig_storage_phase != 0);
    // An abort may never become a flash recovery operation. In particular a
    // corrupt/full log must fail closed rather than consolidate staged RAM.
    ig_storage_read_only_reload = !commit_attempted || ig_storage_phase == 0;
    wear_leveling_status_t status = ig_wear_leveling_init_internal();
    if (!commit_attempted && status != WEAR_LEVELING_FAILED &&
        fnv_64a_buf(wear_leveling.cache, WEAR_LEVELING_LOGICAL_SIZE, FNV1A_64_INIT) != ig_storage_original_hash) failed = true;
    ig_storage_read_only_reload = false;
    ig_storage_borrowed = false;
    ig_storage_payload_address = ig_storage_payload_length = 0;
    ig_storage_phase = 0;
    if (failed || status == WEAR_LEVELING_FAILED) {
        ig_storage_fault = true;
        return WEAR_LEVELING_FAILED;
    }
    return status;
}
