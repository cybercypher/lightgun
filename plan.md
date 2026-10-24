

# Pico 2 Absolute Mouse Bridge — Implementation Plan

## 1. Goals
- Present 1 or 2 independent **absolute HID mice** to Windows.
- DemulShooter / TeknoParrot / any RawInput mouse consumer sees them as normal absolute mice.
- Self-powered from USB. No external power supply.
- Low latency, low jitter.
- Trigger (and optional extra buttons) mapped to mouse buttons.
- Stable device paths so P1/P2 can be bound by name in DemulShooter.
- Calibration stays on the host (homography, window-rect, per-game profiles, etc.).

## 2. High-level architecture

```
G’AIM’E Gun(s)  →  PC Host Software  →  Pico 2 (USB)  →  Windows sees Absolute Mouse(es)
(digitizer + keyboard)   (read + calibrate + send)     (HID absolute mouse reports)
```

- Guns remain plugged into the PC.
- Host software owns the G’AIM’E protocol, calibration, player assignment, and game-window mapping.
- Pico is a pure HID absolute-mouse device that receives commands and emits reports.

## 3. Hardware
- **One** Raspberry Pi Pico 2 (RP2350).
- Data-capable micro-USB cable to the PC (powers the board and carries the HID device).
- No other hardware required for Option A.

## 4. Firmware design (Pico)

**Recommended stack:** Pico SDK + TinyUSB (C/C++).  
CircuitPython is acceptable for a fast prototype but has higher latency/jitter.

### 4.1 USB configuration
- Composite HID device with **two absolute mouse interfaces** (or one interface with two report IDs).
- Preferred: two separate HID interfaces so Windows treats them as two distinct mice with stable instance paths.
- Vendor/Product ID: custom (or reuse a known absolute-mouse VID/PID if desired).
- String descriptors: clear names such as “G’AIM’E Bridge Mouse 1” / “Mouse 2”.

### 4.2 Absolute mouse report descriptor
Use a standard absolute mouse report:

- Buttons (bitfield: left, right, middle, …)
- X absolute (16-bit, typically 0–32767 or 0–10000)
- Y absolute (16-bit)
- Optional wheel (can be omitted)

TinyUSB provides `TUD_HID_REPORT_DESC_ABSMOUSE` (or equivalent). Customize logical max and report size as needed. Match the range your host will send.

### 4.3 Communication channel from host → Pico
Simplest reliable options (pick one):

| Method | Pros | Cons | Recommendation |
|--------|------|------|----------------|
| **USB CDC (serial)** | Easy, well-supported, bidirectional | Extra interface | **Best for start** |
| Vendor HID output report | Single device | Slightly more work | Good later |
| UART + USB-serial adapter | Simple debug | Extra hardware | Avoid |

**Recommended protocol (CDC serial, binary or simple text):**

Binary packet example (8–12 bytes, little-endian):

```
[0xAA] [player] [buttons] [x_lo] [x_hi] [y_lo] [y_hi] [checksum]
```

- `player`: 0 or 1
- `buttons`: bit0 = left (trigger), bit1 = right, etc.
- `x`, `y`: absolute values in the agreed range (e.g. 0–32767)
- Optional: rate-limit or “only send on change” from host.

Text protocol is also fine for prototyping (`P0 16384 8192 1\n`).

### 4.4 Pico firmware responsibilities
1. Initialize TinyUSB as composite absolute mouse(s) + CDC.
2. In the main loop:
   - Call `tud_task()`.
   - Read incoming packets from CDC (or vendor HID).
   - Update the corresponding mouse’s absolute X/Y + button state.
   - Send HID report when data changes or at a fixed rate (e.g. 125–250 Hz).
3. Optional: simple heartbeat / LED status.
4. Optional: report current mouse positions back to host for debug.

### 4.5 Key TinyUSB / Pico notes
- Use the official Pico SDK TinyUSB examples (`hid_composite`, multi-interface HID) as starting points.
- For two absolute mice, either:
  - Two HID interfaces, or
  - One interface with two report IDs.
- Separate interfaces give cleaner device paths in DemulShooter.
- Pico 2 (RP2350) is fully compatible; no special changes required.

## 5. Host software side (PC)

Responsibilities that stay on the PC:

- Read G’AIM’E digitizer + keyboard (existing code / protocol).
- Apply per-gun calibration (homography preferred over simple bbox).
- Map to the target window / game client rect (handle letterboxing, borders, exclusive fullscreen).
- Assign guns to player slots by serial.
- Convert calibrated screen coordinates into the absolute range expected by the Pico (e.g. 0–32767).
- Pack and send the packets to the Pico over CDC.
- Handle trigger debounce and map tip → left button.
- Optional: off-screen reload as an extra button or special coordinate.
- Keep coin/start/A/B on the real keyboard interface if desired.

## 6. Data flow summary

1. Host reads gun reports.
2. Host applies calibration + window mapping → screen coordinates.
3. Host scales to Pico absolute range.
4. Host sends packet: player, buttons, x, y.
5. Pico receives packet, updates internal mouse state.
6. Pico emits absolute HID report(s).
7. Windows / DemulShooter / TeknoParrot see normal absolute mouse movement and clicks.

## 7. Implementation phases (for the coding agent)

**Phase 1 — Pico absolute mouse prototype**
- Single absolute mouse via TinyUSB.
- Hard-coded or simple CDC “move to X Y” commands.
- Verify Windows sees an absolute mouse and DemulShooter lists it.

**Phase 2 — Dual mouse + protocol**
- Add second absolute mouse.
- Implement the packet protocol.
- Stable device names / paths.

**Phase 3 — Host integration**
- Wire existing G’AIM’E reader + calibration to the Pico CDC.
- End-to-end test: gun aim → Pico mouse movement.

**Phase 4 — Polish**
- Button mapping (trigger → left click).
- Rate limiting / change-only sending.
- Error recovery (USB reconnect, packet checksum).
- Optional debug telemetry.

**Phase 5 — Validation**
- DemulShooter single- and dual-player titles.
- TeknoParrot titles that expect absolute mice.
- Latency measurement.
- Confirm 100% Windows scaling works as expected.

## 8. Success criteria
- Both guns appear as distinct absolute mice in Device Manager / DemulShooter device list.
- Aim tracks correctly after host calibration.
- Trigger fires reliably as left mouse button.
- No driver signing issues (pure HID device).
- Works with DemulShooter and TeknoParrot without further patches.

## 9. Risks & mitigations
- **Latency**: Keep Pico firmware minimal; send only on change or at high rate. CDC is usually fine.
- **Device enumeration order**: Use clear string descriptors and bind by name in DemulShooter.
- **Coordinate range mismatch**: Agree on one absolute range (recommend 0–32767) and document it.
- **USB reconnect**: Handle device removal/re-enumeration gracefully on both sides.

## 10. Deliverables the coding agent should produce
1. Pico firmware (Pico SDK + TinyUSB) that enumerates as 1–2 absolute mice + CDC.
2. Simple packet protocol spec + example sender (Python or C#).
3. Integration notes for the existing host software.
4. Build instructions (UF2 flashing).
5. Basic test procedure.

---