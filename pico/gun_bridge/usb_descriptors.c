/*
 * USB descriptors for the G'AIM'E Pico 2 absolute-mouse bridge.
 *
 * Composite device, four interfaces:
 *   ITF 0 : HID absolute mouse 1 (MI_00)
 *   ITF 1 : HID absolute mouse 2 (MI_01)
 *   ITF 2 : CDC ACM communication
 *   ITF 3 : CDC ACM data
 *
 * The configuration descriptor is written by hand (instead of the TinyUSB
 * TUD_* macros) for two reasons:
 *   - per-interface string descriptors ("... Mouse 1" / "... Mouse 2"), and
 *   - no dependence on TinyUSB macro signatures, which changed between 0.14
 *     and 0.17.
 */

#include <string.h>

#include "tusb.h"
#include "usb_descriptors.h"

// ---------------------------------------------------------------------------
// Device descriptor
// ---------------------------------------------------------------------------
static tusb_desc_device_t const desc_device = {
    .bLength            = sizeof(tusb_desc_device_t),
    .bDescriptorType    = TUSB_DESC_DEVICE,
    .bcdUSB             = 0x0200,
    .bDeviceClass       = 0xEF,   // miscellaneous: composite with IAD
    .bDeviceSubClass    = 0x02,
    .bDeviceProtocol    = 0x01,
    .bMaxPacketSize0    = 64,
    .idVendor           = 0x1209, // pid.codes community VID (see README)
    .idProduct          = 0x0A11, // "GAIME"
    .bcdDevice          = 0x0100,
    .iManufacturer      = STRID_MANUFACTURER,
    .iProduct           = STRID_PRODUCT,
    .iSerialNumber      = STRID_SERIAL,
    .bNumConfigurations = 1,
};

// ---------------------------------------------------------------------------
// HID report descriptor: 3 buttons + absolute X/Y, 0..32767, 5-byte report.
// ---------------------------------------------------------------------------
uint8_t const desc_hid_report[] = {
    0x05, 0x01,        // Usage Page (Generic Desktop)
    0x09, 0x02,        // Usage (Mouse)
    0xA1, 0x01,        // Collection (Application)
    0x09, 0x01,        //   Usage (Pointer)
    0xA1, 0x00,        //   Collection (Physical)
    0x05, 0x09,        //     Usage Page (Button)
    0x19, 0x01,        //     Usage Minimum (1)
    0x29, 0x03,        //     Usage Maximum (3)
    0x15, 0x00,        //     Logical Minimum (0)
    0x25, 0x01,        //     Logical Maximum (1)
    0x95, 0x03,        //     Report Count (3)
    0x75, 0x01,        //     Report Size (1)
    0x81, 0x02,        //     Input (Data,Var,Abs)
    0x95, 0x01,        //     Report Count (1)
    0x75, 0x05,        //     Report Size (5)  padding
    0x81, 0x03,        //     Input (Cnst,Var,Abs)
    0x05, 0x01,        //     Usage Page (Generic Desktop)
    0x09, 0x30,        //     Usage (X)
    0x09, 0x31,        //     Usage (Y)
    0x15, 0x00,        //     Logical Minimum (0)
    0x26, 0xFF, 0x7F,  //     Logical Maximum (32767)
    0x75, 0x10,        //     Report Size (16)
    0x95, 0x02,        //     Report Count (2)
    0x81, 0x02,        //     Input (Data,Var,Abs)
    0xC0,              //   End Collection
    0xC0               // End Collection
};

uint16_t const desc_hid_report_len = sizeof(desc_hid_report);

_Static_assert(sizeof(desc_hid_report) == 51, "HID report descriptor length");

// ---------------------------------------------------------------------------
// Configuration descriptor
//
// 9 (config) + 25 (HID 1) + 25 (HID 2) + 35 (CDC comm) + 23 (CDC data) = 117
// Endpoints: HID1 IN, HID2 IN, CDC notify IN, CDC data OUT, CDC data IN = 5
// ---------------------------------------------------------------------------
static uint8_t const desc_configuration[] = {
    // ---- Configuration header -------------------------------------------
    9, 0x02,               // CONFIGURATION
    125, 0x00,             // wTotalLength
    4,                     // bNumInterfaces
    1,                     // bConfigurationValue
    0,                     // iConfiguration
    0x80,                  // bmAttributes: bus powered, no remote wakeup
    100,                   // bMaxPower: 200 mA

    // ---- Interface 0: absolute mouse 1 ----------------------------------
    9, 0x04,               // INTERFACE
    0x00,                  // bInterfaceNumber
    0x00,                  // bAlternateSetting
    0x01,                  // bNumEndpoints
    0x03,                  // class: HID
    0x00,                  // subclass
    0x02,                  // protocol: mouse
    STRID_MOUSE1,          // iInterface

    9, 0x21,               // HID descriptor
    0x11, 0x01,            // bcdHID 1.11
    0x00,                  // bCountryCode
    0x01,                  // bNumDescriptors
    0x22,                  // descriptor type: report
    51, 0x00,              // wDescriptorLength (desc_hid_report)

    7, 0x05,               // ENDPOINT
    0x81,                  // IN, EP1
    0x03,                  // interrupt
    8, 0x00,               // wMaxPacketSize
    0x01,                  // bInterval: 1 ms

    // ---- Interface 1: absolute mouse 2 ----------------------------------
    9, 0x04,
    0x01,                  // bInterfaceNumber
    0x00,                  // bAlternateSetting
    0x01,                  // bNumEndpoints
    0x03,                  // class: HID
    0x00,                  // subclass
    0x02,                  // protocol: mouse
    STRID_MOUSE2,          // iInterface

    9, 0x21,               // HID descriptor
    0x11, 0x01,
    0x00,
    0x01,
    0x22,
    51, 0x00,

    7, 0x05,
    0x82,                  // IN, EP2
    0x03,
    8, 0x00,
    0x01,

    // ---- Interface Association Descriptor: CDC function = ITF 2 + 3 ------
    8, 0x0B,               // INTERFACE ASSOCIATION
    0x02,                  // bFirstInterface
    0x02,                  // bInterfaceCount
    0x02,                  // bFunctionClass: CDC
    0x02,                  // bFunctionSubClass: ACM
    0x01,                  // bFunctionProtocol: AT commands
    0x00,                  // iFunction

    // ---- Interface 2: CDC ACM communication -----------------------------
    9, 0x04,
    0x02,                  // bInterfaceNumber
    0x00,                  // bAlternateSetting
    0x01,                  // bNumEndpoints
    0x02,                  // class: CDC
    0x02,                  // subclass: abstract control model
    0x01,                  // protocol: AT commands
    0x00,                  // iInterface

    // CDC functional descriptors (CS_INTERFACE = 0x24)
    5, 0x24, 0x00,         // header
    0x20, 0x01,            // bcdCDC 1.20

    5, 0x24, 0x01,         // call management
    0x00,                  // bmCapabilities
    0x03,                  // bDataInterface (interface 3)

    4, 0x24, 0x02,         // abstract control management
    0x02,                  // bmCapabilities

    5, 0x24, 0x06,         // union
    0x02,                  // bMasterInterface (comm)
    0x03,                  // bSlaveInterface0 (data)

    7, 0x05,
    0x83,                  // IN, EP3
    0x03,                  // interrupt
    16, 0x00,
    0x10,                  // bInterval: 16 ms

    // ---- Interface 3: CDC ACM data --------------------------------------
    9, 0x04,
    0x03,                  // bInterfaceNumber
    0x00,                  // bAlternateSetting
    0x02,                  // bNumEndpoints
    0x0A,                  // class: CDC data
    0x00,                  // subclass
    0x00,                  // protocol
    0x00,                  // iInterface

    7, 0x05,
    0x04,                  // OUT, EP4
    0x02,                  // bulk
    64, 0x00,
    0x00,

    7, 0x05,
    0x84,                  // IN, EP4
    0x02,                  // bulk
    64, 0x00,
    0x00,
};

_Static_assert(sizeof(desc_configuration) == 125, "config descriptor length");

// ---------------------------------------------------------------------------
// String descriptors (UTF-16LE, built on demand)
// ---------------------------------------------------------------------------
// 32 UTF-16 code units is enough for every string above.
static uint16_t _desc_str[32];

static char const *const string_desc_arr[] = {
    "\x09\x04",                 // 0: language (special-cased below)
    "G'AIM'E",                   // 1: manufacturer
    "G'AIM'E Bridge",            // 2: product
    "GAIME-BRIDGE-0001",         // 3: serial (also see README, one per PC)
    "G'AIM'E Bridge Mouse 1",    // 4: interface 0
    "G'AIM'E Bridge Mouse 2",    // 5: interface 1
};

// ---------------------------------------------------------------------------
// TinyUSB callbacks
// ---------------------------------------------------------------------------
uint8_t const *tud_descriptor_device_cb(void)
{
    return (uint8_t const *) &desc_device;
}

uint8_t const *tud_descriptor_configuration_cb(uint8_t index)
{
    (void) index;
    return desc_configuration;
}

uint16_t const *tud_descriptor_string_cb(uint8_t index, uint16_t langid)
{
    (void) langid;
    size_t chr_count;

    if (index == 0) {
        memcpy(&_desc_str[1], "\x09\x04", 2);   // 0x0409 = US English
        chr_count = 1;
    } else {
        if (index >= (sizeof(string_desc_arr) / sizeof(string_desc_arr[0]))) {
            return NULL;
        }

        char const *str = string_desc_arr[index];
        chr_count = strlen(str);
        size_t const max_count = (sizeof(_desc_str) / sizeof(_desc_str[0])) - 1;
        if (chr_count > max_count) {
            chr_count = max_count;
        }
        for (size_t i = 0; i < chr_count; i++) {
            _desc_str[1 + i] = (uint8_t) str[i];
        }
    }

    // First uint16_t: bLength in the low byte, bDescriptorType in the high one.
    _desc_str[0] = (uint16_t) ((TUSB_DESC_STRING << 8) | (2 * chr_count + 2));
    return _desc_str;
}
