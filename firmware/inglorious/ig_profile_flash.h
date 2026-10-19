// SPDX-License-Identifier: GPL-2.0-or-later
#pragma once
#include "ig_profile_bank.h"
#ifndef IG_PROFILE_BANK_ENABLE
#    define IG_PROFILE_BANK_ENABLE 0
#endif
#if IG_PROFILE_BANK_ENABLE
#    if IG_BOARD != 16 || !defined(IG_RETAINED_LOADER_BUILD) || IG_RETAINED_LOADER_BUILD != 1
#        error "Hardware profile storage is enabled only for retained-loader board16"
#    endif
#define IG_PROFILE_BANK_OFFSET 0x2A000U
#define IG_PROFILE_BANK_END 0x36000U
#define IG_PROFILE_IDENTITY_OFFSET 0x37F00U
const uint8_t *ig_profile_flash_bytes(void);
const uint8_t *ig_profile_flash_identity(void);
bool ig_profile_flash_ready(void);
bool ig_profile_flash_begin(void);
void ig_profile_flash_end(void);
const ig_pb_io *ig_profile_flash_io(void);
#endif
