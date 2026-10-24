# Bridge protocol (host -> Pico)

Binary, fixed-length frames over the USB CDC interface. USB CDC is a byte
stream: the Pico re-synchronises on the `A5 5A` magic, so partial writes are
safe.

## Frame (12 bytes, little endian)

| off | size | field | notes |
|----:|-----:|-------|-------|
| 0 | 1 | magic 0 | `0xA5` |
| 1 | 1 | magic 1 | `0x5A` |
| 2 | 1 | version | `0x01` |
| 3 | 1 | player | `0` = mouse 1, `1` = mouse 2 |
| 4 | 1 | buttons | bit0 left (trigger), bit1 right, bit2 middle, bit3 back, bit4 forward |
| 5 | 1 | wheel | signed -127..127; one tick per frame, 0 otherwise |
| 6 | 2 | x | u16 LE, `0..32767` |
| 8 | 2 | y | u16 LE, `0..32767` |
| 10 | 1 | seq | free-running counter, `0..255` |
| 11 | 1 | crc8 | CRC-8 over bytes 0..10 |

CRC-8 parameters: polynomial `0x07`, initial value `0x00`, no input/output
reflection, no final XOR.

Coordinates are absolute and screen-oriented: `(0,0)` is the top-left corner
and `(32767,32767)` the bottom-right. Host calibration and game-window mapping
happen before the frame is built; the Pico does not transform coordinates.

## Rate

- The host may send at any rate; the firmware rate-limits HID reports to one
  per 4 ms (250 Hz).
- The bridge is **silent when idle**: it reports only when a frame arrives and
  never resends an unchanged position. An unprompted absolute report would
  re-position the shared Windows cursor and fight a real mouse.
- One frame updates one player. For two players send two frames per tick, or
  send each player independently (they do not have to be in lock-step).
- Reports are emitted only when a frame arrives, so an idle bridge is
  completely silent.

## Examples

Player 1, trigger down, centre of the screen:

```
A5 5A 01 00 01 00 FF 3F FF 3F 07 8A
```

Player 2, no buttons, top-left:

```
A5 5A 01 01 00 00 00 00 00 00 08 38
```

Both frames verify with the CRC-8 in `host/gun_bridge_sender.py`.

## Errors and resynchronisation

- A frame is accepted only if magic, version and CRC all match and
  `player < 2`; otherwise the parser drops one byte and scans again.
- Out-of-range coordinates are clamped to `0..32767`.
- Unknown button bits are masked off.
- If the CDC host stops sending, the mice keep their last position; no timeout
  jump is generated.
- Windows discards an **all-zero** absolute-mouse report (no buttons, 0/0), so
  the firmware nudges that one case to raw (1,1). That is 1/32767 of the range,
  far below one pixel.

## USB view

- VID `0x1209`, PID `0x0A11`, serial `GAIME-BRIDGE-0001`.
- Interface 0 (`MI_00`): absolute HID mouse 1, usage page `0x01`, usage
  `0x02`, report ID 0, 5-byte input report
  `[buttons][x_lo][x_hi][y_lo][y_hi]`.
- Interface 1 (`MI_01`): absolute HID mouse 2, same descriptor.
- Report: 6 bytes - `[buttons][wheel][x_lo][x_hi][y_lo][y_hi]`; five buttons
  (left, right, middle, back, forward) and a signed wheel delta.
- Interfaces 2/3: CDC ACM (`COMx` on Windows), 115200 8N1 nominal (ignored by
  USB CDC; any baud works).
