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

## Feeder → ViGEm Xbox360 pad (DemulShooter / Teknoparrot), per gun

Trigger = X (button 3) only — the RB button and the right-trigger slider were
removed so the trigger is a single clean, easy-to-map button. Standard Xbox /
DirectInput button order (verified against the old RB mapping):

| # | XInput button | Gun function |
|---|---|---|
| 1 | A | Gun A (SPACE) |
| 2 | B | Gun B (B) |
| 3 | X | TRIGGER |
| 4 | Y | — |
| 5 | LB | — |
| 6 | RB | — |
| 7 | Back | Coin (C) |
| 8 | Start | Start (Q) |
| 9 | L3 | — |
| 10 | R3 | — |

Aim: left stick. X Axis = HID usage 0x30, Y Axis = 0x31 — the correct usages
for the feeder's left stick, leave them. Recoil switch: host-silent (gun-local).
Pedal: not detected by PC (console-only).
