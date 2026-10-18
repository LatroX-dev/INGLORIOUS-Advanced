// Native metadata for the two established runtime harnesses. Other board
// harnesses place their generated ig_board.h earlier in the include path.
#pragma once
#define IG_HAS_ENCODER 1
#ifdef IG_TEST_PRO
#    define IG_ENCODER_KEY_ROW 0
#    define IG_ENCODER_KEY_COL 1
#    define IG_LIGHT_TEST_PATTERN 1
#    define IG_RECOVERY_KEY1_ROW 9
#    define IG_RECOVERY_KEY1_COL 4
#    define IG_RECOVERY_KEY2_ROW 4
#    define IG_RECOVERY_KEY2_COL 5
#    define IG_LIGHT_TEST_KEYS {{6,0},{5,5},{4,3},{7,2},{7,0},{4,0},{6,0},{7,0},{5,0},{2,2}}
#else
#    define IG_ENCODER_KEY_ROW 2
#    define IG_ENCODER_KEY_COL 3
#    define IG_LIGHT_TEST_PATTERN 2
#    define IG_RECOVERY_KEY1_ROW 1
#    define IG_RECOVERY_KEY1_COL 3
#    define IG_RECOVERY_KEY2_ROW 3
#    define IG_RECOVERY_KEY2_COL 3
#    define IG_LIGHT_TEST_KEYS {{3,0},{3,1},{3,2},{2,0},{2,1},{2,2},{1,0},{1,1},{1,2},{4,0}}
#    define IG_EXTERNAL_SPI_CS B6
#endif
