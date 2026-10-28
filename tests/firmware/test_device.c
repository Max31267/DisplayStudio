#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifdef _WIN32
#include <io.h>
#include <fcntl.h>
#else
#include <unistd.h>
#endif
#include "md_device.h"

static md_device device;
static md_parser parser;
static uint8_t pixels[MD_WIDTH * MD_HEIGHT * 2]; // Mock LCD RAM; never linked into ESP32 firmware.
static uint8_t last_reply[MD_HEADER + 10];
static size_t last_reply_size;
static bool bridge;
static uint64_t synced_time;

static bool draw(void *ctx, int x, int y, int w, int h, const uint8_t *data)
{
    (void)ctx;
    for (int row = 0; row < h; ++row)
        memcpy(pixels + ((y + row) * MD_WIDTH + x) * 2, data + row * w * 2, (size_t)w * 2);
    return true;
}
static void send_reply(void *ctx, const uint8_t *data, size_t length)
{
    (void)ctx; assert(length <= sizeof(last_reply));
    memcpy(last_reply, data, length); last_reply_size = length;
    if (bridge) { (void)fwrite(data, 1, length, stdout); fflush(stdout); }
}
static void time_sync(void *ctx, uint64_t seconds) { (void)ctx; synced_time = seconds; }
static uint32_t heap(void *ctx) { (void)ctx; return 123456; }
static void packet(void *ctx, const md_packet *p) { (void)ctx; (void)md_device_handle(&device, p); }
static void error(void *ctx) { (void)ctx; md_device_corruption(&device); }

static void send_packet(uint8_t type, uint32_t seq, const uint8_t *body, uint16_t length)
{
    uint8_t data[MD_MAX_PACKET];
    size_t size = md_encode(data, sizeof(data), type, seq, body, length);
    assert(size != 0);
    last_reply_size = 0;
    // Exercise maximum fragmentation on the real parser.
    for (size_t i = 0; i < size; ++i) md_parser_feed(&parser, data + i, 1, packet, error, NULL);
    assert(last_reply_size >= MD_HEADER);
}

int main(int argc, char **argv)
{
    md_device_init(&device, draw, send_reply, time_sync, heap, NULL);
    if (argc == 3 && strcmp(argv[1], "--bridge") == 0) {
        bridge = true;
#ifdef _WIN32
        _setmode(_fileno(stdin), _O_BINARY); _setmode(_fileno(stdout), _O_BINARY);
#endif
        uint8_t input[1024];
        int count;
        while (true) {
#ifdef _WIN32
            count = _read(_fileno(stdin), input, sizeof(input));
#else
            count = (int)read(STDIN_FILENO, input, sizeof(input));
#endif
            if (count <= 0) break;
            md_parser_feed(&parser, input, (size_t)count, packet, error, NULL);
        }
        FILE *dump = fopen(argv[2], "wb");
        if (!dump) return 2;
        size_t written = fwrite(pixels, 1, sizeof(pixels), dump);
        fclose(dump);
        return written == sizeof(pixels) ? 0 : 3;
    }

    assert(md_crc32((const uint8_t *)"123456789", 9, NULL, 0) == 0xcbf43926u);
    send_packet(MD_HELLO, 0, NULL, 0);
    assert(last_reply[5] == MD_CAPABILITIES && md_u32(last_reply + 8) == 0);
    assert(md_u16(last_reply + 16) == 240 && md_u32(last_reply + 22) == 123456);
    uint8_t clock[8] = {0}; md_put32(clock, 1700000000u);
    send_packet(MD_TIME_SYNC, 1, clock, sizeof(clock)); assert(synced_time == 1700000000u);

    uint8_t begin[5] = {1, 0, 0, 0, 1};
    send_packet(MD_FRAME_BEGIN, 2, begin, sizeof(begin)); assert(last_reply[5] == MD_ACK);
    uint8_t tile[MD_MAX_PAYLOAD]; md_put16(tile, 0); md_put16(tile + 4, 240); md_put16(tile + 6, 16);
    for (int i = 8; i < MD_MAX_PAYLOAD; ++i) tile[i] = (uint8_t)i;
    uint32_t seq = 3;
    for (int y = 0; y < 240; y += 16) {
        md_put16(tile + 2, (uint16_t)y);
        send_packet(MD_PIXELS, seq++, tile, sizeof(tile)); assert(last_reply[5] == MD_ACK);
    }
    uint8_t end[4] = {1, 0, 0, 0};
    send_packet(MD_FRAME_END, seq++, end, sizeof(end)); assert(device.committed_frames == 1);
    assert(memcmp(pixels, tile + 8, MD_WIDTH * 16 * 2) == 0);

    // Reject bounds before invoking panel DMA.
    begin[0] = 2; begin[4] = 0; send_packet(MD_FRAME_BEGIN, seq++, begin, sizeof(begin));
    md_put16(tile, 240);
    uint32_t before = md_crc32(pixels, sizeof(pixels), NULL, 0);
    send_packet(MD_PIXELS, seq++, tile, sizeof(tile)); assert(last_reply[5] == MD_REQUEST_FULL);
    assert(md_crc32(pixels, sizeof(pixels), NULL, 0) == before);

    send_packet(MD_HELLO, 100, NULL, 0);
    begin[4] = 1; send_packet(MD_FRAME_BEGIN, 101, begin, sizeof(begin));
    end[0] = 2; send_packet(MD_FRAME_END, 102, end, sizeof(end)); assert(last_reply[5] == MD_REQUEST_FULL);
    assert(device.committed_frames == 1); // Full flag requires coverage of all 225 tiles.
    send_packet(MD_HELLO, 200, NULL, 0);
    send_packet(MD_PING, 202, NULL, 0); assert(last_reply[5] == MD_REQUEST_FULL);

    uint8_t corrupt[MD_HEADER];
    md_encode(corrupt, sizeof(corrupt), MD_PING, 201, NULL, 0); corrupt[12] ^= 1;
    md_parser_feed(&parser, corrupt, sizeof(corrupt), packet, error, NULL);
    assert(parser.rejected == 1 && last_reply[5] == MD_REQUEST_FULL);
    send_packet(MD_HELLO, 300, NULL, 0); assert(last_reply[5] == MD_CAPABILITIES);
    md_device_disconnect(&device); assert(!device.connected && device.needs_full);
    puts("PASS  firmware CRC, fragmentation, full coverage, pixels, sequence, bounds, corruption and reconnect");
    return 0;
}
