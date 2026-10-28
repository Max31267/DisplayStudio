idf_component_register(
    SRCS "main.cpp" "../portable/md_protocol.c" "../portable/md_device.c"
    INCLUDE_DIRS "." "../portable"
    REQUIRES esp_lcd esp_driver_spi esp_driver_gpio esp_driver_usb_serial_jtag esp_timer
)
