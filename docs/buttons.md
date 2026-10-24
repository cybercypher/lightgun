# Capture 2 — button map (RawInput keyboard, gun 1)

Sequence performed: `Trigger, A, Trigger, B, Trigger, Coin, Trigger, Start, Trigger`.
`tip=1` x38 = separator trigger pulls. Gun 1 keyboard path
`HID#VID_2E2C&PID_0631&MI_00#a&12225dc` (legacy keyboard GUID). msg 256=down, 257=up.

| Gun button | vkey | make | key |
|---|---|---|---|
| A | 0x20 | 0x39 | SPACE |
| B | 0x42 | 0x30 | B |
| Coin | 0x43 | 0x2E | C |
| Start | 0x51 | 0x10 | Q |
| Trigger | — | — | digitizer tip bit (IF1), no keyboard event |

Matches MP1st report. (Later MOUSE traffic is the desk Logitech mouse, unrelated.)

## Feeder → Pico bridge mouse, per player

Gun 1 = Bridge Mouse 1 (P1), Gun 2 = Bridge Mouse 2 (P2). Aim is absolute
X/Y, 0..32767 over the primary screen.

| Gun input | Bridge mouse button | TeknoParrot `RawMouseButton` | Typical use |
|---|---|---|---|
| Trigger, aimed on screen | Left | `LeftButton` | fire |
| Trigger, aimed off screen | Right | `RightButton` | reload (DemulShooter) |
| B | Right | `RightButton` | reload |
| A | Middle | `MiddleButton` | pedal / action / grenade |
| Coin | Back (button 4) | `Button4` | coin |
| Start | Forward (button 5) | `Button5` | start |

Terminator Salvation's gun board does not see Coin/Start on Button4/5
(`docs/terminator-salvation-input.md`). Recoil switch: host-silent (gun-local).
Pedal: not detected by PC (console-only).
