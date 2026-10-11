#pragma once
#include <stdint.h>
#include <stdbool.h>
typedef uint64_t backing_store_int_t;
bool backing_store_write(uint32_t address,backing_store_int_t value);
