#!/usr/bin/env python3
"""Calibration report from a GaimeTray log (standard library only).

Reads `gallery [shot]` and `gallery [lag]` lines (and, for older logs, the
`trigger DOWN raw=(x,y)` line logged with each shot) and prints, per gun:

  - median / mean / p90 miss, split into edge-band and inner targets
  - the scale error (shot vs target, per axis)
  - the lag sweep: median miss per press-aim lag (picks latch-ms.txt)
  - a least-squares refit raw -> target per axis, its leave-one-out median
    miss, the screen area the gun can reach (raw 99..9900), and the number
    of samples at the raw clamp

With --write-cal DIR it writes the refit as DIR/cal_<sn>.json, in the same
format as calibrations/cal_<sn>.json.

Usage:
  python tools/gallery_report.py C:\\games\\feeder\\logs\\tray-20261025.log
  python tools/gallery_report.py LOG --since 22:41 --until 22:56
  python tools/gallery_report.py LOG --source 0ms --write-cal out
"""
import argparse
import json
import math
import os
import re
import statistics

TS = r"(\d\d:\d\d:\d\d(?:\.\d+)?)"
RE_SHOT = re.compile(TS + r" gallery \[shot\]: (.+?) miss (\d+) px (?:HIT|miss) "
                     r"target \((-?\d+),(-?\d+)\) shot \((-?\d+),(-?\d+)\)")
RE_LAG = re.compile(TS + r" gallery \[lag\]: (.+?) target \((-?\d+),(-?\d+)\)(.*)$")
RE_LAGCOL = re.compile(r"\| (\S+) \((-?\d+),(-?\d+)\) (\d+)px")
RE_DOWN = re.compile(TS + r" (.+?): trigger DOWN raw=\((-?\d+),(-?\d+)\)")
RE_CAL = re.compile(TS + r" (.+?): calibrated(.*)$")
RE_MEASURE = re.compile(TS + r" gallery \[measure end\]: (.*)$")
RE_SN = re.compile(r"sn(\d+)")

CLAMP_LO, CLAMP_HI = 99, 9900


def secs(ts):
    h, m, s = ts.split(":")
    return int(h) * 3600 + int(m) * 60 + float(s)


def in_window(ts, since, until):
    t = secs(ts)
    return (since is None or t >= since) and (until is None or t <= until)


def fit(raw, px):
    n = len(raw)
    mr = sum(raw) / n
    mp = sum(px) / n
    sxx = sum((r - mr) ** 2 for r in raw)
    sxy = sum((r - mr) * (p - mp) for r, p in zip(raw, px))
    a = sxy / sxx if sxx else float("nan")
    return a, mp - a * mr


def pct(v, q):
    s = sorted(v)
    if not s:
        return float("nan")
    k = (len(s) - 1) * q
    lo, hi = math.floor(k), math.ceil(k)
    return s[lo] + (s[hi] - s[lo]) * (k - lo)


def summary(v):
    if not v:
        return "n=0"
    return "n=%d median %.0f mean %.0f p90 %.0f" % (
        len(v), statistics.median(v), statistics.mean(v), pct(v, 0.9))


def parse(path, since, until):
    shots, lags, downs, cals, measures = [], [], [], [], []
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.rstrip("\r\n")
            m = RE_SHOT.match(line)
            if m and in_window(m.group(1), since, until):
                shots.append(dict(t=secs(m.group(1)), gun=m.group(2), err=int(m.group(3)),
                                  tx=int(m.group(4)), ty=int(m.group(5)),
                                  sx=int(m.group(6)), sy=int(m.group(7))))
                continue
            m = RE_LAG.match(line)
            if m and in_window(m.group(1), since, until):
                cols = {c[0]: (int(c[1]), int(c[2]), int(c[3])) for c in RE_LAGCOL.findall(m.group(5))}
                lags.append(dict(t=secs(m.group(1)), gun=m.group(2),
                                 tx=int(m.group(3)), ty=int(m.group(4)), cols=cols))
                continue
            m = RE_DOWN.match(line)
            if m and in_window(m.group(1), since, until):
                downs.append(dict(t=secs(m.group(1)), gun=m.group(2),
                                  rx=int(m.group(3)), ry=int(m.group(4))))
                continue
            m = RE_CAL.match(line)
            if m and in_window(m.group(1), since, until):
                cals.append(line)
                continue
            m = RE_MEASURE.match(line)
            if m and in_window(m.group(1), since, until):
                measures.append(line)
    return shots, lags, downs, cals, measures


def pair_raw(shots, lags, downs):
    """Per shot, a dict of source -> (rx, ry): lag columns if logged, else the
    trigger DOWN raw logged with the shot (source 'in')."""
    for s in shots:
        s["raw"] = {}
        for l in lags:
            if l["gun"] == s["gun"] and abs(l["t"] - s["t"]) < 0.05 and \
                    l["tx"] == s["tx"] and l["ty"] == s["ty"]:
                s["raw"] = {k: (v[0], v[1]) for k, v in l["cols"].items()}
                s["lagmiss"] = {k: v[2] for k, v in l["cols"].items()}
                break
        if "in" not in s["raw"]:
            for d in downs:
                if d["gun"] == s["gun"] and 0 <= d["t"] - s["t"] < 0.05:
                    s["raw"]["in"] = (d["rx"], d["ry"])
                    break


def report(gun, S, args):
    W, H = args.width, args.height
    band = args.edge_band
    print("\n=== %s" % gun)
    err = [s["err"] for s in S]
    edge = [s["err"] for s in S if min(s["tx"], W - s["tx"], s["ty"], H - s["ty"]) < band]
    inner = [s["err"] for s in S if min(s["tx"], W - s["tx"], s["ty"], H - s["ty"]) >= band]
    print("miss (as played)  : " + summary(err))
    print("  edge band <%dpx : %s" % (band, summary(edge)))
    print("  inner           : " + summary(inner))
    if len(S) >= 3:
        ax, bx = fit([s["tx"] for s in S], [s["sx"] for s in S])
        ay, by = fit([s["ty"] for s in S], [s["sy"] for s in S])
        print("scale (shot vs target): X %+.1f%%  Y %+.1f%%   (+ = mapped too wide)"
              % ((ax - 1) * 100, (ay - 1) * 100))

    lagged = [s for s in S if "lagmiss" in s]
    if lagged:
        keys = list(lagged[0]["lagmiss"].keys())
        med = {k: statistics.median([s["lagmiss"][k] for s in lagged if k in s["lagmiss"]]) for k in keys}
        print("lag sweep (median miss px, %d pulls): " % len(lagged) +
              "  ".join("%s %.0f" % (k, med[k]) for k in keys))
        best = min((k for k in keys if k.endswith("ms")), key=lambda k: med[k])
        print("  best press-aim lag: %s  -> latch-ms.txt = %s" % (best, best[:-2]))

    sources = [k for k in (S[0]["raw"].keys() if S else [])]
    if not sources:
        print("refit: no raw samples in the log for this gun")
        return
    src = args.source if args.source in sources else None
    if src is None:
        if lagged:
            src = min((k for k in sources if k.endswith("ms")),
                      key=lambda k: statistics.median([s["lagmiss"][k] for s in lagged if k in s["lagmiss"]]))
        else:
            src = "in"
    P = [s for s in S if src in s["raw"]]
    if len(P) < 6:
        print("refit: fewer than 6 shots with source %s" % src)
        return
    rx = [s["raw"][src][0] for s in P]
    ry = [s["raw"][src][1] for s in P]
    tx = [s["tx"] for s in P]
    ty = [s["ty"] for s in P]
    ax, bx = fit(rx, tx)
    ay, by = fit(ry, ty)
    loo = []
    for i in range(len(P)):
        a1, b1 = fit(rx[:i] + rx[i + 1:], tx[:i] + tx[i + 1:])
        a2, b2 = fit(ry[:i] + ry[i + 1:], ty[:i] + ty[i + 1:])
        loo.append(math.hypot(a1 * rx[i] + b1 - tx[i], a2 * ry[i] + b2 - ty[i]))
    cal = dict(RawMinX=-bx / ax, RawMaxX=(W - 1 - bx) / ax,
               RawMinY=-by / ay, RawMaxY=(H - 1 - by) / ay,
               ScreenW=W, ScreenH=H, InvertY=False, IsValid=True)
    print("refit on source '%s' (%d shots): leave-one-out %s" % (src, len(P), summary(loo)))
    print("  cal: RawMinX %.1f RawMaxX %.1f RawMinY %.1f RawMaxY %.1f"
          % (cal["RawMinX"], cal["RawMaxX"], cal["RawMinY"], cal["RawMaxY"]))
    print("  reachable screen (raw %d..%d): X %.0f..%.0f  Y %.0f..%.0f"
          % (CLAMP_LO, CLAMP_HI, ax * CLAMP_LO + bx, ax * CLAMP_HI + bx,
             ay * CLAMP_LO + by, ay * CLAMP_HI + by))
    clamped = sum(1 for x, y in zip(rx, ry) if x <= CLAMP_LO or x >= CLAMP_HI or
                  y <= CLAMP_LO or y >= CLAMP_HI)
    print("  samples at the raw clamp: %d" % clamped)
    if args.write_cal:
        m = RE_SN.search(gun)
        if m:
            os.makedirs(args.write_cal, exist_ok=True)
            out = os.path.join(args.write_cal, "cal_%s.json" % m.group(1))
            with open(out, "w") as f:
                json.dump(cal, f, indent=2)
            print("  wrote " + out)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("log")
    ap.add_argument("--since", help="HH:MM[:SS]")
    ap.add_argument("--until", help="HH:MM[:SS]")
    ap.add_argument("--gun", help="only labels containing this text, e.g. sn22222")
    ap.add_argument("--source", help="refit source: in, 0ms, 25ms, ... (default: best lag)")
    ap.add_argument("--write-cal", metavar="DIR", help="write the refit as DIR/cal_<sn>.json")
    ap.add_argument("--width", type=int, default=3840)
    ap.add_argument("--height", type=int, default=2160)
    ap.add_argument("--edge-band", type=int, default=200, help="px from an edge counted as edge band")
    args = ap.parse_args()

    def t(v):
        if v is None:
            return None
        parts = v.split(":")
        while len(parts) < 3:
            parts.append("0")
        return secs(":".join(parts))

    shots, lags, downs, cals, measures = parse(args.log, t(args.since), t(args.until))
    pair_raw(shots, lags, downs)
    for line in cals:
        print(line)
    for line in measures:
        print(line)
    guns = sorted({s["gun"] for s in shots})
    if args.gun:
        guns = [g for g in guns if args.gun in g]
    if not guns:
        print("no gallery [shot] lines in the window")
    for g in guns:
        report(g, [s for s in shots if s["gun"] == g], args)


if __name__ == "__main__":
    main()
