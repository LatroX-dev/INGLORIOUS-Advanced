#pragma once
#include "qmk_mock.h"
void debounce_init(void);
bool debounce(matrix_row_t raw[], matrix_row_t cooked[], bool changed);
