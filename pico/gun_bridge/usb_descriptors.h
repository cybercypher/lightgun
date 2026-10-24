#ifndef GUN_BRIDGE_USB_DESCRIPTORS_H
#define GUN_BRIDGE_USB_DESCRIPTORS_H

#include "tusb.h"

// String descriptor indices (shared by the device descriptor and the
// per-interface strings in the configuration descriptor).
enum {
    STRID_LANGID = 0,
    STRID_MANUFACTURER = 1,
    STRID_PRODUCT = 2,
    STRID_SERIAL = 3,
    STRID_MOUSE1 = 4,
    STRID_MOUSE2 = 5,
};

// HID report descriptor shared by both absolute-mouse interfaces.
extern uint8_t const desc_hid_report[];
extern uint16_t const desc_hid_report_len;

#endif
