// SPDX-License-Identifier: GPL-2.0-or-later
#include "ig_profile_bank.h"
#include <limits.h>
#include <string.h>

static const uint8_t header_magic[8] = {'I','G','P','B','A','N','K','1'};
static const uint8_t commit_magic[8] = {'I','G','P','C','M','T','1','!'};
_Static_assert(IG_PB_RECORD_COUNT == 2u * IG_PB_PROFILE_COUNT, "fixed profile pairs");
_Static_assert(IG_PB_BANK_SIZE == 49152u, "bank geometry");
_Static_assert(IG_PB_PAYLOAD_CAPACITY == 7680u, "record geometry");
_Static_assert(IG_PB_RECORD_SIZE % IG_PB_PAGE_SIZE == 0u, "erase geometry");

static uint16_t u16(const uint8_t *p) { return (uint16_t)(p[0] | ((uint16_t)p[1] << 8)); }
static uint32_t u32(const uint8_t *p) { return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24); }
static uint64_t u64(const uint8_t *p) { return u32(p) | ((uint64_t)u32(p + 4) << 32); }
static void w16(uint8_t *p, uint16_t v) { p[0] = (uint8_t)v; p[1] = (uint8_t)(v >> 8); }
static void w32(uint8_t *p, uint32_t v) { for (unsigned i = 0; i < 4; ++i) p[i] = (uint8_t)(v >> (i * 8)); }
static void w64(uint8_t *p, uint64_t v) { for (unsigned i = 0; i < 8; ++i) p[i] = (uint8_t)(v >> (i * 8)); }
static bool all_ff(const uint8_t *p, size_t n) { for (size_t i = 0; i < n; ++i) if (p[i] != 255u) return false; return true; }
static uint32_t crc_extend(uint32_t crc, const uint8_t *p, size_t n) {
    while (n--) { crc ^= *p++; for (unsigned i = 0; i < 8; ++i) crc = (crc >> 1) ^ (UINT32_C(0xEDB88320) & (0u - (crc & 1u))); }
    return crc;
}
uint32_t ig_pb_crc32(const uint8_t *data, size_t length) { return ~crc_extend(UINT32_MAX, data, length); }
static ig_pb_result arguments(const ig_pb_bank *b) {
    if (!b || !b->bytes || b->length != IG_PB_BANK_SIZE || b->board == 0 || b->board == UINT16_MAX) return IG_PB_ERR_ARGUMENT;
    if (!b->validate) return IG_PB_ERR_NO_VALIDATOR;
    return IG_PB_OK;
}
static bool overlaps_bank(const ig_pb_bank *b, const uint8_t *p, size_t length) {
    uintptr_t pointer = (uintptr_t)p, start = (uintptr_t)b->bytes;
    return pointer >= start ? pointer - start < b->length : start - pointer < length;
}

static ig_pb_record_state decode(const ig_pb_bank *b, unsigned index, ig_pb_metadata *m) {
    const uint8_t *r = b->bytes + index * IG_PB_RECORD_SIZE, *c = r + IG_PB_COMMIT_OFFSET;
    uint8_t owner = (uint8_t)(index / 2u + 1u);
    memset(m, 0, sizeof(*m));
    if (all_ff(r, IG_PB_RECORD_SIZE)) return IG_PB_EMPTY;
    // These envelopes and their checksum locations are frozen across versions.
    // A torn header can contain FF in its version field: it is not future data
    // unless its complete envelope checksum also verifies.
    bool header = memcmp(r, header_magic, 8) == 0 && u32(r + 36) == ig_pb_crc32(r, 36);
    bool commit = memcmp(c, commit_magic, 8) == 0 && u32(c + 28) == ig_pb_crc32(c, 28);
    if ((header && u16(r + 8) != IG_PB_FORMAT) || (commit && u16(c + 8) != IG_PB_FORMAT)) return IG_PB_FUTURE;
    // A committed or merely recognizable header from another owner must never
    // become reclaimable scratch, even when the rest of its record is damaged.
    if ((header && (u16(r + 12) != b->board || r[14] != owner)) ||
        (commit && (u16(c + 10) != b->board || c[12] != owner))) return IG_PB_FOREIGN;
    if (!header || !commit) return IG_PB_TORN;
    if (u16(r + 10) != IG_PB_PAGE_SIZE || r[15] != 0 || u16(r + 18) != 0 ||
        !all_ff(r + 40, IG_PB_PAGE_SIZE - 40) || !all_ff(c + 32, IG_PB_PAGE_SIZE - 32) ||
        c[13] != 0 || c[14] != 0 || c[15] != 0) return IG_PB_TORN;
    m->board = u16(r + 12); m->profile = r[14]; m->schema = u16(r + 16);
    m->length = u32(r + 20); m->generation = u64(r + 24);
    if (!m->schema || !m->generation || !m->length || m->length > IG_PB_PAYLOAD_CAPACITY ||
        u64(c + 16) != m->generation ||
        !all_ff(r + IG_PB_PAGE_SIZE + m->length, IG_PB_PAYLOAD_CAPACITY - m->length) ||
        u32(r + 32) != ig_pb_crc32(r + IG_PB_PAGE_SIZE, m->length) ||
        u32(c + 24) != ig_pb_crc32(r, IG_PB_COMMIT_OFFSET) ||
        !b->validate(b->validation_context, r + IG_PB_PAGE_SIZE, m->length, m->schema)) return IG_PB_TORN;
    return IG_PB_VALID;
}

ig_pb_result ig_pb_scan_bank(const ig_pb_bank *b, ig_pb_scan *s) {
    ig_pb_result rc = arguments(b);
    if (rc != IG_PB_OK || !s) return rc == IG_PB_OK ? IG_PB_ERR_ARGUMENT : rc;
    memset(s, 0, sizeof(*s));
    s->write_status = IG_PB_OK;
    for (unsigned i = 0; i < IG_PB_RECORD_COUNT; ++i) {
        s->state[i] = decode(b, i, &s->metadata[i]);
        if (s->state[i] == IG_PB_FUTURE) s->write_status = IG_PB_ERR_FUTURE;
        else if (s->state[i] == IG_PB_FOREIGN && s->write_status != IG_PB_ERR_FUTURE) s->write_status = IG_PB_ERR_FOREIGN;
    }
    for (unsigned p = 0; p < IG_PB_PROFILE_COUNT; ++p) {
        unsigned a = p * 2u, z = a + 1u;
        bool av = s->state[a] == IG_PB_VALID, zv = s->state[z] == IG_PB_VALID;
        s->latest[p] = -1;
        if (s->state[a] == IG_PB_FUTURE || s->state[z] == IG_PB_FUTURE ||
            s->state[a] == IG_PB_FOREIGN || s->state[z] == IG_PB_FOREIGN) {
            s->pair[p] = IG_PB_PAIR_BLOCKED; s->latest[p] = -2;
        } else if (av && zv && s->metadata[a].generation == s->metadata[z].generation) {
            s->pair[p] = IG_PB_PAIR_AMBIGUOUS; s->latest[p] = -2;
            if (s->write_status == IG_PB_OK) s->write_status = IG_PB_ERR_AMBIGUOUS;
        } else if (av || zv) {
            s->latest[p] = !zv || (av && s->metadata[a].generation > s->metadata[z].generation) ? (int)a : (int)z;
            s->pair[p] = s->state[a] == IG_PB_TORN || s->state[z] == IG_PB_TORN ? IG_PB_PAIR_RECOVERED : IG_PB_PAIR_READY;
        } else s->pair[p] = s->state[a] == IG_PB_EMPTY && s->state[z] == IG_PB_EMPTY ? IG_PB_PAIR_EMPTY : IG_PB_PAIR_DEGRADED;
    }
    return IG_PB_OK;
}

static ig_pb_result read_fallback(const ig_pb_bank *b, ig_pb_fallback *fallback) {
    memset(fallback, 0, sizeof(*fallback));
    if (!b->fallback || !b->fallback(b->fallback_context, fallback) || !fallback->payload ||
        !fallback->schema || !fallback->length || fallback->length > IG_PB_PAYLOAD_CAPACITY ||
        overlaps_bank(b, fallback->payload, fallback->length) ||
        !b->validate(b->validation_context, fallback->payload, fallback->length, fallback->schema)) {
        memset(fallback, 0, sizeof(*fallback)); return IG_PB_ERR_NO_FALLBACK;
    }
    return IG_PB_OK;
}
ig_pb_result ig_pb_read_profile(const ig_pb_bank *b, uint8_t profile, ig_pb_view *view) {
    if (!view || profile < 1 || profile > IG_PB_PROFILE_COUNT) return IG_PB_ERR_ARGUMENT;
    memset(view, 0, sizeof(*view));
    ig_pb_scan s;
    ig_pb_result rc = ig_pb_scan_bank(b, &s);
    if (rc != IG_PB_OK) return rc;
    if (s.write_status == IG_PB_ERR_FUTURE || s.write_status == IG_PB_ERR_FOREIGN) return s.write_status;
    int i = s.latest[profile - 1u];
    if (i == -2) return IG_PB_ERR_AMBIGUOUS;
    if (i >= 0) {
        view->payload = b->bytes + (unsigned)i * IG_PB_RECORD_SIZE + IG_PB_PAGE_SIZE;
        view->length = s.metadata[i].length; view->schema = s.metadata[i].schema;
        view->generation = s.metadata[i].generation; view->source = IG_PB_SOURCE_RECORD;
    } else {
        ig_pb_fallback fallback;
        rc = read_fallback(b, &fallback); if (rc != IG_PB_OK) return rc;
        view->payload = fallback.payload; view->length = fallback.length; view->schema = fallback.schema;
        view->source = s.pair[profile - 1u] == IG_PB_PAIR_EMPTY ? IG_PB_SOURCE_LEGACY : IG_PB_SOURCE_DEGRADED_LEGACY;
    }
    view->pair_state = s.pair[profile - 1u];
    return IG_PB_OK;
}

static void make_header(uint8_t *page, const ig_pb_metadata *m, const uint8_t *payload) {
    memset(page, 255, IG_PB_PAGE_SIZE); memcpy(page, header_magic, 8);
    w16(page + 8, IG_PB_FORMAT); w16(page + 10, IG_PB_PAGE_SIZE); w16(page + 12, m->board);
    page[14] = m->profile; page[15] = 0; w16(page + 16, m->schema); w16(page + 18, 0);
    w32(page + 20, m->length); w64(page + 24, m->generation);
    w32(page + 32, ig_pb_crc32(payload, m->length)); w32(page + 36, ig_pb_crc32(page, 36));
}
ig_pb_result ig_pb_save_profile(const ig_pb_bank *b, const ig_pb_io *io, uint8_t profile,
                                uint64_t expected_generation, uint16_t schema,
                                const uint8_t *payload, uint32_t length) {
    ig_pb_result rc = arguments(b);
    if (rc != IG_PB_OK) return rc;
    if (!io || !io->erase_page || !io->program_page || !payload || profile < 1 || profile > IG_PB_PROFILE_COUNT ||
        !schema || !length || length > IG_PB_PAYLOAD_CAPACITY || overlaps_bank(b, payload, length)) return IG_PB_ERR_ARGUMENT;
    if (!b->validate(b->validation_context, payload, length, schema)) return IG_PB_ERR_PAYLOAD;
    ig_pb_scan s;
    rc = ig_pb_scan_bank(b, &s); if (rc != IG_PB_OK) return rc;
    if (s.write_status != IG_PB_OK) return s.write_status;
    int current = s.latest[profile - 1u];
    uint64_t generation = current < 0 ? 0 : s.metadata[current].generation;
    if (generation != expected_generation) return IG_PB_ERR_CONFLICT;
    if (generation == UINT64_MAX) return IG_PB_ERR_ROLLOVER;
    if (current < 0) {
        ig_pb_fallback fallback;
        rc = read_fallback(b, &fallback); if (rc != IG_PB_OK) return rc;
    }
    unsigned first = (profile - 1u) * 2u;
    // Static ownership makes a torn inactive half safely reclaimable. No
    // record from another profile can ever become this profile's spare.
    unsigned spare = current >= 0 ? (unsigned)current ^ 1u : s.state[first + 1u] == IG_PB_TORN ? first + 1u : first;
    size_t base = spare * IG_PB_RECORD_SIZE;
    // Invalidate the old superseded commit before altering its body pages.
    if (!io->erase_page(io->context, base + IG_PB_COMMIT_OFFSET)) return IG_PB_ERR_IO;
    for (size_t off = 0; off < IG_PB_COMMIT_OFFSET; off += IG_PB_PAGE_SIZE)
        if (!io->erase_page(io->context, base + off)) return IG_PB_ERR_IO;
    if (!all_ff(b->bytes + base, IG_PB_RECORD_SIZE)) return IG_PB_ERR_VERIFY;
    ig_pb_metadata m = {b->board, schema, profile, length, generation + 1u};
    uint8_t page[IG_PB_PAGE_SIZE];
    make_header(page, &m, payload);
    uint32_t body_crc = crc_extend(UINT32_MAX, page, IG_PB_PAGE_SIZE);
    if (!io->program_page(io->context, base, page)) return IG_PB_ERR_IO;
    for (size_t off = 0; off < IG_PB_PAYLOAD_CAPACITY; off += IG_PB_PAGE_SIZE) {
        memset(page, 255, sizeof(page));
        if (off < length) { size_t n = length - off; if (n > IG_PB_PAGE_SIZE) n = IG_PB_PAGE_SIZE; memcpy(page, payload + off, n); }
        body_crc = crc_extend(body_crc, page, IG_PB_PAGE_SIZE);
        if (!io->program_page(io->context, base + IG_PB_PAGE_SIZE + off, page)) return IG_PB_ERR_IO;
    }
    body_crc = ~body_crc;
    if (ig_pb_crc32(b->bytes + base, IG_PB_COMMIT_OFFSET) != body_crc ||
        !all_ff(b->bytes + base + IG_PB_COMMIT_OFFSET, IG_PB_PAGE_SIZE)) return IG_PB_ERR_VERIFY;
    memset(page, 255, sizeof(page)); memcpy(page, commit_magic, 8);
    w16(page + 8, IG_PB_FORMAT); w16(page + 10, b->board); page[12] = profile;
    page[13] = page[14] = page[15] = 0; w64(page + 16, m.generation);
    w32(page + 24, body_crc); w32(page + 28, ig_pb_crc32(page, 28));
    if (!io->program_page(io->context, base + IG_PB_COMMIT_OFFSET, page)) return IG_PB_ERR_IO;
    ig_pb_metadata verified;
    if (decode(b, spare, &verified) != IG_PB_VALID || verified.generation != m.generation ||
        verified.profile != profile || verified.schema != schema || verified.length != length) return IG_PB_ERR_VERIFY;
    return IG_PB_OK;
}
