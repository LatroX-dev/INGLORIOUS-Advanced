// SPDX-License-Identifier: GPL-2.0-or-later
#pragma once
#if IG_BOARD != 16 || !defined(IG_RETAINED_LOADER_BUILD) || IG_RETAINED_LOADER_BUILD != 1
#error "Board 16 identity requires its retained-loader build"
#endif
#define IG_FW_VERSION_FORMAT 1
#define IG_FW_VERSION_MAJOR 0
#define IG_FW_VERSION_MINOR 11
#define IG_FW_VERSION_PATCH 8
#define IG_FW_BUILD_ID 0x000B0801UL
