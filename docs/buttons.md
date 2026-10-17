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
Feeder mapping (DemulShooter/Teknoparrot via ViGEm Xbox360, per gun):
Trigger→RT, A→A, B→B, Coin→Back, Start→Start. Recoil switch: host-silent (gun-local).
Pedal: not detected by PC (console-only).
