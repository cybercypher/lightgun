#!/usr/bin/env python3
"""Demo host sender for the G'AIM'E Pico 2 absolute-mouse bridge.

Sends 12-byte binary frames over the bridge's USB CDC port.

Examples:
    python gun_bridge_sender.py --demo
    python gun_bridge_sender.py --demo --players 0,1 --seconds 20
    python gun_bridge_sender.py --move 16000 9000 --click
    python gun_bridge_sender.py --port COM7 --demo

Requires pyserial:  pip install pyserial
"""

import argparse
import math
import sys
import time

try:
    import serial
    from serial.tools import list_ports
except ImportError:
    sys.exit("pyserial is required: pip install pyserial")

VID = 0x1209
PID = 0x0A11

MAGIC = b"\xA5\x5A"
VERSION = 1
FRAME_LEN = 12

BTN_LEFT = 0x01
BTN_RIGHT = 0x02
BTN_MIDDLE = 0x04


def crc8(data: bytes) -> int:
    """CRC-8, poly 0x07, init 0x00."""
    crc = 0
    for byte in data:
        crc ^= byte
        for _ in range(8):
            crc = ((crc << 1) ^ 0x07) & 0xFF if crc & 0x80 else (crc << 1) & 0xFF
    return crc


def make_frame(player: int, buttons: int, x: int, y: int, seq: int = 0) -> bytes:
    if player not in (0, 1):
        raise ValueError("player must be 0 or 1")
    x = max(0, min(32767, int(x)))
    y = max(0, min(32767, int(y)))
    body = (
        MAGIC
        + bytes([VERSION, player, buttons & 0x07, 0])
        + x.to_bytes(2, "little")
        + y.to_bytes(2, "little")
        + bytes([seq & 0xFF])
    )
    assert len(body) == FRAME_LEN - 1
    return body + bytes([crc8(body)])


def find_bridge():
    candidates = []
    for port in list_ports.comports():
        if port.vid == VID and port.pid == PID:
            candidates.append(port)
    if not candidates:
        return None
    if len(candidates) > 1:
        print("Multiple bridge CDC ports found, using the first:")
        for port in candidates:
            print(f"  {port.device}  {port.description}")
    return candidates[0].device


def open_port(args):
    device = args.port or find_bridge()
    if not device:
        sys.exit(
            f"No bridge CDC port found (VID {VID:04X}:PID {PID:04X}). "
            "Use --port COMx."
        )
    ser = serial.Serial(device, args.baud, timeout=0.5, write_timeout=1)
    print(f"Opened {device} at {args.baud} baud")
    return ser


def send(ser, frames):
    ser.write(b"".join(frames))
    ser.flush()


def demo(args, ser):
    players = [int(p) for p in args.players.split(",") if p.strip()]
    if any(p not in (0, 1) for p in players):
        sys.exit("--players takes 0, 1 or 0,1")

    period = 1.0 / args.rate
    start = time.monotonic()
    seq = 0
    next_click = start + 1.0
    click_until = 0.0

    print(f"Demo: players {players}, {args.rate} Hz, Ctrl+C to stop")
    try:
        while True:
            now = time.monotonic()
            elapsed = now - start
            if args.seconds and elapsed >= args.seconds:
                break

            # A square path, with a phase offset for player 2.
            phase = (elapsed * 0.25) % 1.0
            if phase < 0.25:
                fx, fy = phase * 4, 0.0
            elif phase < 0.5:
                fx, fy = 1.0, (phase - 0.25) * 4
            elif phase < 0.75:
                fx, fy = 1.0 - (phase - 0.5) * 4, 1.0
            else:
                fx, fy = 0.0, 1.0 - (phase - 0.75) * 4

            if now >= next_click:
                click_until = now + 0.12
                next_click = now + 2.0

            buttons = BTN_LEFT if now < click_until else 0
            frames = []
            for player in players:
                px = 0.1 + 0.8 * ((fx + player * 0.5) % 1.0)
                py = 0.1 + 0.8 * ((fy + player * 0.25) % 1.0)
                frames.append(
                    make_frame(player, buttons, int(px * 32767), int(py * 32767), seq)
                )
            seq = (seq + 1) & 0xFF
            send(ser, frames)
            time.sleep(period)
    except KeyboardInterrupt:
        print("\nStopped")


def move_once(args, ser):
    frames = []
    for player in [int(p) for p in args.players.split(",") if p.strip()]:
        buttons = BTN_LEFT if args.click else 0
        frames.append(make_frame(player, buttons, args.move[0], args.move[1]))
    send(ser, frames)
    if args.click:
        time.sleep(0.12)
        for player in [int(p) for p in args.players.split(",") if p.strip()]:
            send(ser, [make_frame(player, 0, args.move[0], args.move[1])])
    print("sent")


def main():
    parser = argparse.ArgumentParser(description="G'AIM'E Pico bridge host sender")
    parser.add_argument("--port", help="CDC port (default: auto-detect VID:PID)")
    parser.add_argument("--baud", type=int, default=115200, help="ignored by USB CDC")
    parser.add_argument("--players", default="0", help="0, 1 or 0,1 (default 0)")
    parser.add_argument("--demo", action="store_true", help="run a moving square")
    parser.add_argument("--seconds", type=float, default=0, help="demo duration (0 = forever)")
    parser.add_argument("--rate", type=float, default=125, help="demo frame rate (Hz)")
    parser.add_argument("--move", nargs=2, type=int, metavar=("X", "Y"),
                        help="move once to absolute X Y (0..32767)")
    parser.add_argument("--click", action="store_true", help="left-click with --move")
    args = parser.parse_args()

    if not args.demo and not args.move:
        parser.error("choose --demo or --move X Y")

    ser = open_port(args)
    try:
        if args.demo:
            demo(args, ser)
        else:
            move_once(args, ser)
    finally:
        ser.close()


if __name__ == "__main__":
    main()
