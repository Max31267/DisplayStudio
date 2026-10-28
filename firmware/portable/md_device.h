#ifndef MD_DEVICE_H
#define MD_DEVICE_H
#include "md_protocol.h"

// draw must finish using pixels before returning; only then is ACK sent.
typedef bool (*md_draw_fn)(void *ctx, int x, int y, int w, int h, const uint8_t *pixels);
typedef void (*md_send_fn)(void *ctx, const uint8_t *data, size_t length);
typedef void (*md_time_fn)(void *ctx, uint64_t local_seconds);
typedef uint32_t (*md_heap_fn)(void *ctx);
typedef bool (*md_brightness_fn)(void *ctx, uint8_t percent);

typedef struct {
    uint32_t expected_sequence;
    uint32_t frame_id;
    bool connected, frame_open, needs_full, full_frame;
    uint8_t covered[225];
    uint32_t committed_frames;
    md_draw_fn draw;
    md_send_fn send;
    md_time_fn time;
    md_heap_fn heap;
    md_brightness_fn brightness;
    void *ctx;
} md_device;

void md_device_init(md_device *d, md_draw_fn draw, md_send_fn send, md_time_fn time, md_heap_fn heap, md_brightness_fn brightness, void *ctx);
bool md_device_handle(md_device *d, const md_packet *p);
void md_device_corruption(md_device *d);
void md_device_disconnect(md_device *d);
#endif
