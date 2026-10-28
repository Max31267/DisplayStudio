#include "md_device.h"
#include <string.h>

static void reply(md_device *d, uint8_t type, uint32_t sequence, const uint8_t *body, uint16_t length)
{
    uint8_t buffer[MD_HEADER + 14];
    size_t size = md_encode(buffer, sizeof(buffer), type, sequence, body, length);
    d->send(d->ctx, buffer, size);
}

static bool reject(md_device *d, uint32_t sequence)
{
    d->frame_open = false; d->needs_full = true;
    reply(d, MD_REQUEST_FULL, sequence, NULL, 0);
    return false;
}

void md_device_init(md_device *d, md_draw_fn draw, md_send_fn send, md_time_fn time, md_heap_fn heap, md_brightness_fn brightness, void *ctx)
{
    memset(d, 0, sizeof(*d)); d->needs_full = true;
    d->draw = draw; d->send = send; d->time = time; d->heap = heap; d->brightness = brightness; d->ctx = ctx;
}

void md_device_corruption(md_device *d) { (void)reject(d, d->expected_sequence); }
void md_device_disconnect(md_device *d) { d->connected = false; d->frame_open = false; d->needs_full = true; }

static bool draw(md_device *d, const md_packet *p)
{
    if (!d->frame_open || p->length < 8) return false;
    int x = md_u16(p->payload), y = md_u16(p->payload + 2);
    int w = md_u16(p->payload + 4), h = md_u16(p->payload + 6);
    if (w == 0 || h != 16 || x % 16 || y % 16 || w % 16 || x + w > MD_WIDTH || y + h > MD_HEIGHT ||
        p->length != 8u + (unsigned)w * (unsigned)h * 2u) return false;
    if (!d->draw(d->ctx, x, y, w, h, p->payload + 8)) return false;
    for (int tile = x / 16; tile < (x + w) / 16; ++tile) d->covered[y / 16 * 15 + tile] = 1;
    return true;
}

bool md_device_handle(md_device *d, const md_packet *p)
{
    bool extended = p->length == 1 && p->payload[0] == 1;
    if (p->type == MD_HELLO && (p->length == 0 || extended)) {
        d->connected = true; d->expected_sequence = p->sequence + 1u;
        d->frame_open = false; d->needs_full = true;
        uint8_t caps[14]; md_put16(caps, MD_WIDTH); md_put16(caps + 2, MD_HEIGHT);
        md_put16(caps + 4, MD_MAX_PAYLOAD); md_put32(caps + 6, d->heap(d->ctx));
        md_put32(caps + 10, d->brightness ? 1u : 0u);
        reply(d, MD_CAPABILITIES, p->sequence, caps, extended ? 14 : 10); return true;
    }
    if (!d->connected || p->sequence != d->expected_sequence) return reject(d, p->sequence);
    ++d->expected_sequence;
    bool valid = false;
    switch (p->type) {
    case MD_FRAME_BEGIN:
        valid = p->length == 5 && !d->frame_open && p->payload[4] <= 1 && (!d->needs_full || p->payload[4] == 1);
        if (valid) {
            d->frame_id = md_u32(p->payload); d->full_frame = p->payload[4] == 1; d->frame_open = true;
            memset(d->covered, 0, sizeof(d->covered));
        }
        break;
    case MD_PIXELS: valid = draw(d, p); break;
    case MD_FRAME_END:
        valid = d->frame_open && p->length == 4 && md_u32(p->payload) == d->frame_id;
        if (valid && d->full_frame) for (int i = 0; i < 225; ++i) if (!d->covered[i]) { valid = false; break; }
        if (valid) { d->frame_open = false; d->needs_full = false; ++d->committed_frames; }
        break;
    case MD_TIME_SYNC:
        valid = !d->frame_open && p->length == 8;
        if (valid) d->time(d->ctx, (uint64_t)md_u32(p->payload) | ((uint64_t)md_u32(p->payload + 4) << 32));
        break;
    case MD_PING: valid = !d->frame_open && p->length == 0; break;
    case MD_SET_BRIGHTNESS:
        valid = !d->frame_open && p->length == 1 && p->payload[0] <= 100 && d->brightness;
        if (valid) valid = d->brightness(d->ctx, p->payload[0]);
        break;
    default: break;
    }
    if (!valid) return reject(d, p->sequence);
    reply(d, MD_ACK, p->sequence, NULL, 0); return true;
}
