#include "md_protocol.h"
#include <string.h>

uint16_t md_u16(const uint8_t *p) { return (uint16_t)(p[0] | ((uint16_t)p[1] << 8)); }
uint32_t md_u32(const uint8_t *p) { return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24); }
void md_put16(uint8_t *p, uint16_t v) { p[0] = (uint8_t)v; p[1] = (uint8_t)(v >> 8); }
void md_put32(uint8_t *p, uint32_t v) { p[0] = (uint8_t)v; p[1] = (uint8_t)(v >> 8); p[2] = (uint8_t)(v >> 16); p[3] = (uint8_t)(v >> 24); }

static uint32_t crc_step(uint32_t crc, uint8_t value)
{
    crc ^= value;
    for (int bit = 0; bit < 8; ++bit) crc = (crc >> 1) ^ (0xedb88320u & (0u - (crc & 1u)));
    return crc;
}

uint32_t md_crc32(const uint8_t *a, size_t a_len, const uint8_t *b, size_t b_len)
{
    uint32_t crc = 0xffffffffu;
    for (size_t i = 0; i < a_len; ++i) crc = crc_step(crc, a[i]);
    for (size_t i = 0; i < b_len; ++i) crc = crc_step(crc, b[i]);
    return ~crc;
}

size_t md_encode(uint8_t *out, size_t capacity, uint8_t type, uint32_t sequence, const uint8_t *payload, uint16_t length)
{
    if (length > MD_MAX_PAYLOAD || capacity < MD_HEADER + (size_t)length) return 0;
    memcpy(out, "MDP1", 4); out[4] = 1; out[5] = type;
    md_put16(out + 6, length); md_put32(out + 8, sequence);
    if (length != 0) memcpy(out + MD_HEADER, payload, length);
    md_put32(out + 12, md_crc32(out + 4, 8, payload, length));
    return MD_HEADER + (size_t)length;
}

static void drop_first(md_parser *p) { --p->count; memmove(p->buffer, p->buffer + 1, p->count); }

void md_parser_feed(md_parser *p, const uint8_t *data, size_t length, md_packet_fn packet, md_error_fn error, void *ctx)
{
    for (size_t i = 0; i < length; ++i) {
        p->buffer[p->count++] = data[i];
        while (p->count >= 4) {
            if (memcmp(p->buffer, "MDP1", 4) != 0) { drop_first(p); continue; }
            if (p->count < MD_HEADER) break;
            uint16_t size = md_u16(p->buffer + 6);
            if (p->buffer[4] != 1 || size > MD_MAX_PAYLOAD) {
                ++p->rejected; error(ctx); drop_first(p); continue;
            }
            size_t total = MD_HEADER + (size_t)size;
            if (p->count < total) break;
            if (md_crc32(p->buffer + 4, 8, p->buffer + MD_HEADER, size) != md_u32(p->buffer + 12)) {
                ++p->rejected; error(ctx); drop_first(p); continue;
            }
            md_packet view = { .type = p->buffer[5], .sequence = md_u32(p->buffer + 8), .length = size, .payload = p->buffer + MD_HEADER };
            packet(ctx, &view);
            p->count -= total;
            memmove(p->buffer, p->buffer + total, p->count);
        }
    }
}
