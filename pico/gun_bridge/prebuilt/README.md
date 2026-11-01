# Prebuilt bridge firmware

`gun_bridge.uf2` is the Pico 2 (RP2350) bridge firmware that ships next to
`GaimeTray.exe` in the release zip. The wizard's **Flash bridge…** button
copies it to a Pico held in BOOTSEL.

| | |
|---|---|
| Board | Pico 2 (RP2350, ARM secure; 82 blocks + the SDK's absolute block) |
| SHA-256 | `783040a8c2caad2b17aec8c104df267d873c83ace6979dba9417fc28e3d955df` |
| Size | 42 496 bytes (83 UF2 blocks, 21 248 bytes of flash payload) |
| Identity | VID `1209` / PID `0A11`, serial `GAIME-BRIDGE-0001`, interfaces `G'AIM'E Bridge Mouse 1` / `2` — matches `../usb_descriptors.c` |
| Added | 2026-10-29, from an existing build (not rebuilt for the release) |

The exact source commit it was built from was not recorded. The firmware
sources last changed in `7a157c8` (2026-10-24); the identity strings above
match the current sources.

## Replacing it

Build from `pico/gun_bridge` (Pico SDK 2.2.0, see `../README.md`). A fresh
`build/gun_bridge.uf2` is packaged instead of this file automatically; to make
it the new prebuilt, copy it here and update the hash above.
