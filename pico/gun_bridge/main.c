/*
 * G'AIM'E Pico 2 absolute-mouse bridge
 *
 * The host (PC) reads the lightguns, applies calibration and game-window
 * mapping, then sends 12-byte binary frames over USB CDC. This firmware turns
 * those frames into HID reports for two independent absolute mice, which
 * Windows - and therefore DemulShooter / TeknoParrot - sees as two ordinary
 * absolute HID mice (usage page 0x01, usage 0x02, report ID 0).
 *
 * See ../PROTOCOL.md for the wire format and ../README.md for build/flash.
 */

#include <string.h>

#include "pico/stdlib.h"
#include "tusb.h"

#include "usb_descriptors.h"

#define FRAME_LEN          12
#define MAGIC0             0xA5
#define MAGIC1             0x5A
#define PROTO_VERSION      1
#define MOUSE_COUNT        2

#define REPORT_PERIOD_MS   4     // <= 250 Hz

typedef struct {
    uint8_t  buttons;            // bit0 left, bit1 right, bit2 middle,
                                 // bit3 back, bit4 forward
    int8_t   wheel;              // -127..127, applied once then cleared
    uint16_t x;                  // 0..32767
    uint16_t y;                  // 0..32767
    bool     dirty;
} mouse_state_t;

// Nothing is reported until the host sends a frame: an absolute mouse that
// reports unprompted fights any real mouse sharing the desktop cursor.
static mouse_state_t mice[MOUSE_COUNT] = {
    { .buttons = 0, .wheel = 0, .x = 0, .y = 0, .dirty = false },
    { .buttons = 0, .wheel = 0, .x = 0, .y = 0, .dirty = false },
};

static uint32_t last_tx_ms[MOUSE_COUNT];

static uint8_t rx_buf[64];
static size_t  rx_len;

// ---------------------------------------------------------------------------
// Protocol helpers
// ---------------------------------------------------------------------------

// CRC-8, polynomial 0x07, init 0x00, no reflection, no final XOR.
static uint8_t crc8(const uint8_t *data, size_t len)
{
    uint8_t crc = 0x00;
    for (size_t i = 0; i < len; i++) {
        crc ^= data[i];
        for (int bit = 0; bit < 8; bit++) {
            crc = (uint8_t) ((crc & 0x80) ? ((crc << 1) ^ 0x07) : (crc << 1));
        }
    }
    return crc;
}

static bool send_mouse(uint8_t index)
{
    if (!tud_hid_n_ready(index)) {
        return false;
    }

    // 6-byte report: buttons, wheel, x16, y16
    uint8_t report[6] = {
        (uint8_t) (mice[index].buttons & 0x1F),
        (uint8_t) mice[index].wheel,
        (uint8_t) (mice[index].x & 0xFF),
        (uint8_t) (mice[index].x >> 8),
        (uint8_t) (mice[index].y & 0xFF),
        (uint8_t) (mice[index].y >> 8),
    };

    // Windows ignores an all-zero absolute-mouse report (no buttons, no wheel,
    // 0/0), so nudge the origin by one raw unit. 1/32767 is under a pixel.
    if ((report[0] | report[1] | report[2] | report[3] | report[4] | report[5]) == 0) {
        report[2] = 1;
        report[4] = 1;
    }

    return tud_hid_n_report(index, 0, report, sizeof(report));
}

static void apply_frame(const uint8_t *frame)
{
    uint8_t const player = frame[3];
    if (player >= MOUSE_COUNT) {
        return;
    }

    uint16_t x = (uint16_t) (frame[6] | ((uint16_t) frame[7] << 8));
    uint16_t y = (uint16_t) (frame[8] | ((uint16_t) frame[9] << 8));
    if (x > 32767) x = 32767;
    if (y > 32767) y = 32767;

    mice[player].buttons = (uint8_t) (frame[4] & 0x1F);
    mice[player].wheel = (int8_t) frame[5];
    mice[player].x = x;
    mice[player].y = y;
    mice[player].dirty = true;
}

// Read everything the CDC endpoint has, then consume complete frames.
// Invalid bytes are skipped one at a time, so a truncated frame cannot wedge
// the parser.
static void pump_cdc(void)
{
    while (tud_cdc_available()) {
        int ch = tud_cdc_read_char();
        if (ch < 0) {
            break;
        }
        if (rx_len < sizeof(rx_buf)) {
            rx_buf[rx_len++] = (uint8_t) ch;
        } else {
            memmove(rx_buf, rx_buf + 1, sizeof(rx_buf) - 1);
            rx_buf[sizeof(rx_buf) - 1] = (uint8_t) ch;
        }
    }

    size_t i = 0;
    while ((rx_len - i) >= FRAME_LEN) {
        uint8_t const *frame = &rx_buf[i];

        bool const header_ok = (frame[0] == MAGIC0) && (frame[1] == MAGIC1);
        bool const version_ok = (frame[2] == PROTO_VERSION);
        bool const crc_ok = (frame[FRAME_LEN - 1] == crc8(frame, FRAME_LEN - 1));

        if (header_ok && version_ok && crc_ok) {
            apply_frame(frame);
            i += FRAME_LEN;
        } else {
            i++;
        }
    }

    if (i > 0) {
        memmove(rx_buf, rx_buf + i, rx_len - i);
        rx_len -= i;
    }
}

// ---------------------------------------------------------------------------
// TinyUSB device callbacks
// ---------------------------------------------------------------------------
void tud_mount_cb(void)
{
    for (uint8_t i = 0; i < MOUSE_COUNT; i++) {
        mice[i].dirty = false;
        last_tx_ms[i] = 0;
    }
}

void tud_umount_cb(void)
{
    rx_len = 0;
}

uint8_t const *tud_hid_descriptor_report_cb(uint8_t instance)
{
    (void) instance;
    return desc_hid_report;
}

uint16_t tud_hid_get_report_cb(uint8_t instance, uint8_t report_id,
                               hid_report_type_t report_type,
                               uint8_t *buffer, uint16_t reqlen)
{
    (void) instance;
    (void) report_id;
    (void) report_type;
    (void) buffer;
    (void) reqlen;
    return 0;
}

void tud_hid_set_report_cb(uint8_t instance, uint8_t report_id,
                           hid_report_type_t report_type,
                           uint8_t const *buffer, uint16_t bufsize)
{
    (void) instance;
    (void) report_id;
    (void) report_type;
    (void) buffer;
    (void) bufsize;
}

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------
int main(void)
{
    stdio_init_all();
    tusb_init();

    while (true) {
        tud_task();
        pump_cdc();

        uint32_t const now = to_ms_since_boot(get_absolute_time());

        for (uint8_t i = 0; i < MOUSE_COUNT; i++) {
            if (!tud_hid_n_ready(i)) {
                continue;
            }

            // Only report state the host actually sent. No keepalive: an
            // unprompted absolute report would re-position the shared cursor
            // under a real mouse.
            bool const due = mice[i].dirty && ((now - last_tx_ms[i]) >= REPORT_PERIOD_MS);

            if (due) {
                if (send_mouse(i)) {
                    mice[i].dirty = false;
                    last_tx_ms[i] = now;
                    // Wheel is a delta: follow a tick with a zero-wheel report
                    // so the host does not keep scrolling.
                    if (mice[i].wheel != 0) {
                        mice[i].wheel = 0;
                        mice[i].dirty = true;
                    }
                }
            }
        }

        sleep_ms(1);
    }
}
