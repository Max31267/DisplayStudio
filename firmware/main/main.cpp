#include <cstring>
#include "sdkconfig.h"
#include "driver/gpio.h"
#include "driver/spi_master.h"
#include "driver/usb_serial_jtag.h"
#include "esp_heap_caps.h"
#include "esp_lcd_panel_io.h"
#include "esp_lcd_panel_ops.h"
#include "esp_lcd_panel_vendor.h"
#include "esp_system.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
extern "C" {
#include "md_device.h"
}

static esp_lcd_panel_handle_t panel;
static SemaphoreHandle_t transfer_done;
static uint8_t *dma_pixels;
static md_parser parser;
static md_device device;
static int64_t last_activity;
static int64_t sync_at;
static uint64_t local_seconds;
static bool synchronized;

static bool IRAM_ATTR on_transfer(esp_lcd_panel_io_handle_t, esp_lcd_panel_io_event_data_t *, void *)
{
    BaseType_t wake = pdFALSE;
    xSemaphoreGiveFromISR(transfer_done, &wake);
    return wake == pdTRUE;
}

static bool draw_pixels(void *, int x, int y, int w, int h, const uint8_t *pixels)
{
    std::memcpy(dma_pixels, pixels, static_cast<size_t>(w * h * 2));
    if (esp_lcd_panel_draw_bitmap(panel, x, y, x + w, y + h, dma_pixels) != ESP_OK) return false;
    // Never recycle a DMA buffer while SPI still owns it.
    ESP_ERROR_CHECK(xSemaphoreTake(transfer_done, pdMS_TO_TICKS(2000)) == pdTRUE ? ESP_OK : ESP_ERR_TIMEOUT);
    return true;
}

static void send_bytes(void *, const uint8_t *bytes, size_t length)
{
    size_t sent = 0;
    while (sent < length) {
        int n = usb_serial_jtag_write_bytes(bytes + sent, length - sent, pdMS_TO_TICKS(100));
        if (n <= 0) return;
        sent += static_cast<size_t>(n);
    }
}

static void set_time(void *, uint64_t seconds) { local_seconds = seconds; sync_at = esp_timer_get_time(); synchronized = true; }
static uint32_t free_heap(void *) { return esp_get_free_heap_size(); }
static void packet_received(void *, const md_packet *packet)
{
    if (md_device_handle(&device, packet)) last_activity = esp_timer_get_time();
}
static void packet_error(void *) { md_device_corruption(&device); }

static void fill(int x, int y, int w, int h, uint16_t color)
{
    for (int offset = 0; offset < h; offset += 16) {
        int rows = h - offset < 16 ? h - offset : 16;
        for (int i = 0; i < w * rows; ++i) {
            dma_pixels[i * 2] = static_cast<uint8_t>(color >> 8);
            dma_pixels[i * 2 + 1] = static_cast<uint8_t>(color);
        }
        ESP_ERROR_CHECK(esp_lcd_panel_draw_bitmap(panel, x, y + offset, x + w, y + offset + rows, dma_pixels));
        ESP_ERROR_CHECK(xSemaphoreTake(transfer_done, pdMS_TO_TICKS(2000)) == pdTRUE ? ESP_OK : ESP_ERR_TIMEOUT);
    }
}

static void digit(int x, int value, uint16_t color)
{
    static const uint8_t segments[] = { 0x3f, 0x06, 0x5b, 0x4f, 0x66, 0x6d, 0x7d, 0x07, 0x7f, 0x6f };
    uint8_t mask = value < 0 ? 0x40 : segments[value];
    // a, b, c, d, e, f, g
    static const int rects[7][4] = {{6,0,28,5},{34,5,5,27},{34,37,5,27},{6,64,28,5},{1,37,5,27},{1,5,5,27},{6,32,28,5}};
    for (int i = 0; i < 7; ++i) if (mask & (1u << i)) fill(x + rects[i][0], 86 + rects[i][1], rects[i][2], rects[i][3], color);
}

static void fallback_clock()
{
    fill(0, 0, MD_WIDTH, MD_HEIGHT, 0x10e4);
    uint16_t color = synchronized ? 0x8f76 : 0xfd80;
    int hours = -1, minutes = -1;
    if (synchronized) {
        uint64_t seconds = local_seconds + static_cast<uint64_t>((esp_timer_get_time() - sync_at) / 1000000);
        hours = static_cast<int>(seconds / 3600 % 24); minutes = static_cast<int>(seconds / 60 % 60);
    }
    digit(17, hours < 0 ? -1 : hours / 10, color); digit(65, hours < 0 ? -1 : hours % 10, color);
    digit(136, minutes < 0 ? -1 : minutes / 10, color); digit(184, minutes < 0 ? -1 : minutes % 10, color);
    fill(117, 105, 6, 6, color); fill(117, 130, 6, 6, color);
    fill(19, 200, 202, 2, synchronized ? 0x4b6a : 0x7800);
}

static void initialize_panel()
{
    // GPIO18/19 remain reserved for native USB. Selected pins are configurable.
    transfer_done = xSemaphoreCreateBinary();
    ESP_ERROR_CHECK(transfer_done ? ESP_OK : ESP_ERR_NO_MEM);
    dma_pixels = static_cast<uint8_t *>(heap_caps_malloc(MD_WIDTH * 16 * 2, MALLOC_CAP_DMA));
    ESP_ERROR_CHECK(dma_pixels ? ESP_OK : ESP_ERR_NO_MEM);
    spi_bus_config_t bus = {};
    bus.mosi_io_num = CONFIG_MD_PIN_MOSI; bus.miso_io_num = -1; bus.sclk_io_num = CONFIG_MD_PIN_SCLK;
    bus.quadwp_io_num = -1; bus.quadhd_io_num = -1; bus.max_transfer_sz = MD_WIDTH * 16 * 2;
    ESP_ERROR_CHECK(spi_bus_initialize(SPI2_HOST, &bus, SPI_DMA_CH_AUTO));
    esp_lcd_panel_io_spi_config_t io_config = {};
    io_config.cs_gpio_num = CONFIG_MD_PIN_CS; io_config.dc_gpio_num = CONFIG_MD_PIN_DC;
    io_config.spi_mode = 0; io_config.pclk_hz = CONFIG_MD_SPI_MHZ * 1000000;
    io_config.trans_queue_depth = 1; io_config.lcd_cmd_bits = 8; io_config.lcd_param_bits = 8;
    io_config.on_color_trans_done = on_transfer;
    esp_lcd_panel_io_handle_t io;
    ESP_ERROR_CHECK(esp_lcd_new_panel_io_spi(SPI2_HOST, &io_config, &io));
    esp_lcd_panel_dev_config_t config = {};
    config.reset_gpio_num = CONFIG_MD_PIN_RST;
#ifdef CONFIG_MD_BGR
    config.rgb_ele_order = LCD_RGB_ELEMENT_ORDER_BGR;
#else
    config.rgb_ele_order = LCD_RGB_ELEMENT_ORDER_RGB;
#endif
    config.data_endian = LCD_RGB_DATA_ENDIAN_BIG;
    config.bits_per_pixel = 16;
    ESP_ERROR_CHECK(esp_lcd_new_panel_st7789(io, &config, &panel));
    ESP_ERROR_CHECK(esp_lcd_panel_reset(panel));
    ESP_ERROR_CHECK(esp_lcd_panel_init(panel));
    ESP_ERROR_CHECK(esp_lcd_panel_set_gap(panel, CONFIG_MD_GAP_X, CONFIG_MD_GAP_Y));
#ifdef CONFIG_MD_SWAP_XY
    ESP_ERROR_CHECK(esp_lcd_panel_swap_xy(panel, true));
#endif
    bool mirror_x = false, mirror_y = false;
#ifdef CONFIG_MD_MIRROR_X
    mirror_x = true;
#endif
#ifdef CONFIG_MD_MIRROR_Y
    mirror_y = true;
#endif
    ESP_ERROR_CHECK(esp_lcd_panel_mirror(panel, mirror_x, mirror_y));
#ifdef CONFIG_MD_INVERT
    ESP_ERROR_CHECK(esp_lcd_panel_invert_color(panel, true));
#endif
    ESP_ERROR_CHECK(esp_lcd_panel_disp_on_off(panel, true));
    if (CONFIG_MD_PIN_BL >= 0) {
        ESP_ERROR_CHECK(gpio_set_direction(static_cast<gpio_num_t>(CONFIG_MD_PIN_BL), GPIO_MODE_OUTPUT));
#ifdef CONFIG_MD_BL_ACTIVE_HIGH
        ESP_ERROR_CHECK(gpio_set_level(static_cast<gpio_num_t>(CONFIG_MD_PIN_BL), 1));
#else
        ESP_ERROR_CHECK(gpio_set_level(static_cast<gpio_num_t>(CONFIG_MD_PIN_BL), 0));
#endif
    }
}

extern "C" void app_main()
{
    initialize_panel();
    usb_serial_jtag_driver_config_t usb = { .tx_buffer_size = 1024, .rx_buffer_size = 16384 };
    ESP_ERROR_CHECK(usb_serial_jtag_driver_install(&usb));
    md_device_init(&device, draw_pixels, send_bytes, set_time, free_heap, nullptr);
    fallback_clock();
    uint8_t incoming[512];
    int64_t last_fallback_second = -1;
    while (true) {
        int n = usb_serial_jtag_read_bytes(incoming, sizeof(incoming), pdMS_TO_TICKS(25));
        if (n > 0) md_parser_feed(&parser, incoming, static_cast<size_t>(n), packet_received, packet_error, nullptr);
        int64_t now = esp_timer_get_time();
        if (now - last_activity > 5000000) {
            if (device.connected) { md_device_disconnect(&device); parser.count = 0; }
            if (now / 1000000 != last_fallback_second) {
                fallback_clock(); last_fallback_second = now / 1000000;
            }
        }
    }
}
