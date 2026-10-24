#!/usr/bin/env python3
"""Sanity-check the hand-written USB descriptors in usb_descriptors.c.

This does not replace building the firmware with the Pico SDK; it catches the
mechanical mistakes that hand-written descriptor tables are prone to:
wrong wTotalLength, mismatched report length, wrong interface/endpoint
counts, and dangling string indices.

Usage: python3 validate_descriptors.py [path/to/usb_descriptors.c]
"""

import re
import sys
from pathlib import Path

DEFAULT = Path(__file__).resolve().parent.parent / "usb_descriptors.c"

# String descriptor indices in usb_descriptors.h
SYMBOLS = {
    "TUSB_DESC_DEVICE": 1,
    "TUSB_DESC_CONFIGURATION": 2,
    "TUSB_DESC_STRING": 3,
    "TUSB_DESC_INTERFACE": 4,
    "TUSB_DESC_ENDPOINT": 5,
    "TUSB_DESC_CS_INTERFACE": 0x24,
    "TUSB_DESC_HID": 0x21,
    "TUSB_DESC_HID_REPORT": 0x22,
    "TUSB_CLASS_HID": 0x03,
    "TUSB_CLASS_CDC": 0x02,
    "TUSB_CLASS_CDC_DATA": 0x0A,
    "CDC_COMM_SUBCLASS_ACM": 0x02,
    "CDC_COMM_PROTOCOL_ATCOMMAND": 0x01,
    "CDC_FUNC_DESC_HEADER": 0x00,
    "CDC_FUNC_DESC_CALL_MANAGEMENT": 0x01,
    "CDC_FUNC_DESC_ACM": 0x02,
    "CDC_FUNC_DESC_UNION": 0x06,
    "STRID_MOUSE1": 4,
    "STRID_MOUSE2": 5,
}

STRID_LANGID = 0
STRID_MANUFACTURER = 1
STRID_PRODUCT = 2
STRID_SERIAL = 3
STRID_MOUSE1 = 4
STRID_MOUSE2 = 5


def extract_array(source: str, name: str):
    match = re.search(rf"{name}\s*\[\s*\]\s*=\s*\{{(.*?)\}};", source, re.S)
    if not match:
        raise SystemExit(f"array {name} not found")
    body = re.sub(r"//[^\n]*", "", match.group(1))
    numbers = []
    for token in body.split(","):
        token = token.strip()
        if not token:
            continue
        if token.startswith("0x") or token.startswith("0X"):
            numbers.append(int(token, 16))
        elif token in SYMBOLS:
            numbers.append(SYMBOLS[token])
        else:
            numbers.append(int(token))
    return numbers


def main() -> int:
    path = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT
    source = path.read_text()

    config = extract_array(source, "desc_configuration")
    hid = extract_array(source, "desc_hid_report")

    errors = []
    if len(config) != 125:
        errors.append(f"config descriptor is {len(config)} bytes, expected 125")
    if config[0] != 9 or config[1] != 0x02:
        errors.append("config descriptor does not start with a 9-byte CONFIGURATION")
    if config[2] | (config[3] << 8) != len(config):
        errors.append(
            f"wTotalLength {config[2] | (config[3] << 8)} != actual {len(config)}"
        )
    if config[4] != 4:
        errors.append(f"bNumInterfaces {config[4]} != 4")

    if len(hid) != 63:
        errors.append(f"HID report descriptor is {len(hid)} bytes, expected 63")

    # Walk the configuration descriptor.
    i = 0
    interfaces = []
    endpoints = []
    report_len_claims = []
    interface_strings = []
    while i < len(config):
        length = config[i]
        if length < 2 or i + length > len(config):
            errors.append(f"bad descriptor length {length} at offset {i}")
            break
        dtype = config[i + 1]
        if dtype == 0x04:  # interface
            interfaces.append(config[i + 2])
            interface_strings.append(config[i + 8])
        elif dtype == 0x05:  # endpoint
            endpoints.append(config[i + 2])
        elif dtype == 0x21:  # HID
            report_len_claims.append(config[i + 7] | (config[i + 8] << 8))
        i += length

    if i != len(config):
        errors.append("descriptor walk did not end exactly at the end of the array")
    if interfaces != [0, 1, 2, 3]:
        errors.append(f"interfaces {interfaces} != [0, 1, 2, 3]")
    if len(endpoints) != 5:
        errors.append(f"{len(endpoints)} endpoints, expected 5")
    if len(set(endpoints)) != len(endpoints):
        errors.append(f"duplicate endpoint addresses: {endpoints}")
    if report_len_claims != [len(hid), len(hid)]:
        errors.append(f"HID wDescriptorLength {report_len_claims} != {len(hid)}")
    for str_index in interface_strings:
        if str_index not in (0, STRID_MOUSE1, STRID_MOUSE2):
            errors.append(f"interface string index {str_index} does not exist")

    # Count input bits by walking HID short items properly (the data byte of
    # Logical Minimum -127 is 0x81 and must not be mistaken for an Input item).
    bits = 0
    count = 0
    size = 0
    i = 0
    while i < len(hid):
        b = hid[i]
        data_size = (0, 1, 2, 4)[b & 0x03]
        item_type = (b >> 2) & 0x03
        tag = (b >> 4) & 0x0F
        data = int.from_bytes(bytes(hid[i + 1:i + 1 + data_size]), "little") if data_size else 0
        if item_type == 1 and tag == 9:      # Report Count (Global)
            count = data
        elif item_type == 1 and tag == 7:    # Report Size (Global)
            size = data
        elif item_type == 0 and tag == 8:
            bits += count * size
        i += 1 + data_size

    if bits != 48:
        errors.append(f"HID input report is {bits} bits, expected 48 (6 bytes)")

    print(f"config: {len(config)} bytes, interfaces={len(interfaces)}, "
          f"endpoints={len(endpoints)}, HID report={len(hid)} bytes")
    if errors:
        print("FAIL")
        for error in errors:
            print("  -", error)
        return 1

    print("OK: descriptor tables are internally consistent")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
