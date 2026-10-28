#ifndef MD_BACKLIGHT_H
#define MD_BACKLIGHT_H
#include <stdint.h>

#define MD_BACKLIGHT_PWM_HZ 20000
#define MD_BACKLIGHT_DUTY_MAX 1023u
static inline uint32_t md_backlight_duty(uint8_t percent)
{
    if (percent > 100) percent = 100;
    return ((uint32_t)percent * MD_BACKLIGHT_DUTY_MAX + 50u) / 100u;
}
#endif
