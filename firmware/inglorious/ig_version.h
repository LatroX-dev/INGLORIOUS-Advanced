// SPDX-License-Identifier: GPL-2.0-or-later
#pragma once

// Explicit release identity, not a digest or signature of the compiled binary.
#if IG_BOARD == 16
#include "ig_board_version.h"
#else
#define IG_FW_VERSION_FORMAT 1
#define IG_FW_VERSION_MAJOR 0
#define IG_FW_VERSION_MINOR 11
#define IG_FW_VERSION_PATCH 9
#define IG_FW_BUILD_ID 0x000B0901UL
#endif
