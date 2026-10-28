#ifndef MD_PROTOCOL_H
#define MD_PROTOCOL_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define MD_WIDTH 240
#define MD_HEIGHT 240
#define MD_HEADER 16
#define MD_MAX_PAYLOAD (8 + MD_WIDTH * 16 * 2)
#define MD_MAX_PACKET (MD_HEADER + MD_MAX_PAYLOAD)

enum md_type {
    MD_HELLO = 1, MD_CAPABILITIES = 2, MD_FRAME_BEGIN = 3, MD_PIXELS = 4,
    MD_FRAME_END = 5, MD_ACK = 6, MD_REQUEST_FULL = 7, MD_TIME_SYNC = 8, MD_PING = 9
};

typedef struct {
    uint8_t type;
    uint32_t sequence;
    uint16_t length;
    const uint8_t *payload;
} md_packet;

typedef struct {
    uint8_t buffer[MD_MAX_PACKET];
    size_t count;
    uint32_t rejected;
} md_parser;

typedef void (*md_packet_fn)(void *ctx, const md_packet *packet);
typedef void (*md_error_fn)(void *ctx);
uint16_t md_u16(const uint8_t *p);
uint32_t md_u32(const uint8_t *p);
void md_put16(uint8_t *p, uint16_t v);
void md_put32(uint8_t *p, uint32_t v);
uint32_t md_crc32(const uint8_t *a, size_t a_len, const uint8_t *b, size_t b_len);
size_t md_encode(uint8_t *out, size_t capacity, uint8_t type, uint32_t sequence, const uint8_t *payload, uint16_t length);
void md_parser_feed(md_parser *parser, const uint8_t *data, size_t length, md_packet_fn packet, md_error_fn error, void *ctx);

#endif
