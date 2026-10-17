#!/usr/bin/env python3
"""G'AIM'E gun probe for Windows.

Enumerates VID 0x2E2C / PID 0x0631 HID interfaces (keyboard IF0, digitizer IF1,
vendor IF2) and logs input reports per physical gun (keyed by serial/path).

Must run on Windows Python (not WSL — WSL has no USB)::
    pip install hidapi
    python tools/gun_probe_win.py list
    python tools/gun_probe_win.py log --seconds 15

Log while aiming at a lit display: move slowly, pull trigger, press A/B/Coin/Start.
Report layout IF1: 6 bytes `01 <flags> Xlo Xhi Ylo Yhi`, tip=bit0, in_range=bit1.
"""
import argparse
import sys
import time

VID, PID = 0x2E2C, 0x0631


def load_hid():
    try:
        import hid  # hidapi
    except ImportError:
        sys.exit("need hidapi: pip install hidapi (run on Windows Python, not WSL)")
    return hid


def cmd_list(hid):
    devs = hid.enumerate(VID, PID)
    if not devs:
        sys.exit("no GAIME gun found (VID 0x2E2C / PID 0x0631)")
    print(f"found {len(devs)} HID interface(s):")
    for d in devs:
        print(
            f"  path={d.get('path')!r} serial={d.get('serial_number')!r} "
            f"release={d.get('release_number')} "
            f"usage_page=0x{(d.get('usage_page') or 0):04x} usage=0x{(d.get('usage') or 0):02x} "
            f"interface={d.get('interface_number')} product={d.get('product_string')!r}"
        )
    serials = sorted({d.get("serial_number") for d in devs})
    print(f"physical gun(s) by serial: {serials}")
    if len(serials) > 1:
        print("multi-gun separation: key each feeder by serial/path (like guncon2 -index N).")


def open_iface(hid, usage_page):
    devs = hid.enumerate(VID, PID)
    target = next((d for d in devs if d.get("usage_page") == usage_page), None)
    if target is None:
        sys.exit(f"no interface with usage page 0x{usage_page:04x}")
    dev = hid.device()
    dev.open_path(target["path"])
    dev.set_nonblocking(True)
    return dev, target


def cmd_log(hid, page, seconds):
    dev, target = open_iface(hid, page)
    print(
        f"logging usage_page=0x{page:04x} serial={target.get('serial_number')!r} "
        f"for {seconds}s (aim at display, move, trigger, buttons)...",
        file=sys.stderr,
    )
    deadline = time.time() + seconds
    last = None
    n_reports = 0
    xs, ys = [], []
    while time.time() < deadline:
        data = dev.read(64, timeout_ms=200)
        if not data:
            continue
        raw = bytes(data)
        if raw == last:
            continue
        last = raw
        n_reports += 1
        if page == 0x0D and len(raw) >= 6 and raw[0] == 1:
            flags, x = raw[1], int.from_bytes(raw[2:4], "little")
            y = int.from_bytes(raw[4:6], "little")
            xs.append(x)
            ys.append(y)
            print(
                f"{raw[:8].hex()} tip={flags & 1} in_range={(flags >> 1) & 1} "
                f"X={x:5d} Y={y:5d}"
            )
        else:
            print(raw.hex())
    dev.close()
    if xs:
        print(
            f"# {n_reports} digitizer reports: X {min(xs)}..{max(xs)} "
            f"Y {min(ys)}..{max(ys)} (expect clamp ~99..9900)",
            file=sys.stderr,
        )
    else:
        print(f"# {n_reports} reports logged", file=sys.stderr)


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("list")
    r = sub.add_parser("log")
    r.add_argument("--page", type=lambda s: int(s, 0), default=0x0D,
                   help="0x0D digitizer (IF1) or 0x01 keyboard (IF0)")
    r.add_argument("--seconds", type=float, default=15)
    args = ap.parse_args()
    hid = load_hid()
    if args.cmd == "list":
        cmd_list(hid)
    else:
        cmd_log(hid, args.page, args.seconds)


if __name__ == "__main__":
    main()
