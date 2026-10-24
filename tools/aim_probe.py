#!/usr/bin/env python3
"""Find the game's cyan aiming reticle in a screenshot.

    aim_probe.py IMAGE [--expect X Y] [--radius 260] [--min-pixels 40]

Prints JSON: centroid (image px), bbox, pixel count, and the distance from the
expected point. The reticle is a thin cyan ring; the HUD's cyan is excluded by
only looking within `--radius` of the expected point.
"""

import argparse
import json
import sys

from PIL import Image
import numpy as np


def find(path, expect=None, radius=260, min_pixels=40, region=None):
    a = np.asarray(Image.open(path).convert("RGB")).astype(int)
    h, w, _ = a.shape

    if region:
        x0, y0, x1, y1 = region
        x0, y0 = max(0, x0), max(0, y0)
        x1, y1 = min(w, x1), min(h, y1)
    elif expect:
        ex, ey = expect
        x0, y0 = max(0, int(ex - radius)), max(0, int(ey - radius))
        x1, y1 = min(w, int(ex + radius)), min(h, int(ey + radius))
    else:
        x0, y0, x1, y1 = 0, 0, w, h

    sub = a[y0:y1, x0:x1]
    r, g, b = sub[:, :, 0], sub[:, :, 1], sub[:, :, 2]
    mask = (b - r > 70) & (b > 120) & (g > 90) & (r < 110)
    ys, xs = np.nonzero(mask)
    if len(xs) < min_pixels:
        return {"found": False, "pixels": int(len(xs)), "search": [x0, y0, x1, y1]}

    # keep the largest connected cluster by a coarse grid flood (cheap and
    # adequate: the ring is one blob, stray HUD pixels are far away)
    pts = np.stack([xs, ys], axis=1)
    if expect:
        d = np.hypot(pts[:, 0] + x0 - expect[0], pts[:, 1] + y0 - expect[1])
        keep = pts[d < radius * 0.75]
        if len(keep) >= min_pixels:
            pts = keep

    cx = float(pts[:, 0].mean()) + x0
    cy = float(pts[:, 1].mean()) + y0
    out = {
        "found": True,
        "x": round(cx, 1),
        "y": round(cy, 1),
        "pixels": int(len(pts)),
        "bbox": [int(pts[:, 0].min()) + x0, int(pts[:, 1].min()) + y0,
                 int(pts[:, 0].max()) + x0, int(pts[:, 1].max()) + y0],
        "search": [x0, y0, x1, y1],
    }
    if expect:
        out["dx"] = round(cx - expect[0], 1)
        out["dy"] = round(cy - expect[1], 1)
    return out


def main():
    p = argparse.ArgumentParser()
    p.add_argument("image")
    p.add_argument("--expect", nargs=2, type=float)
    p.add_argument("--radius", type=int, default=260)
    p.add_argument("--min-pixels", type=int, default=40)
    p.add_argument("--region", nargs=4, type=int)
    a = p.parse_args()
    res = find(a.image, a.expect, a.radius, a.min_pixels, a.region)
    print(json.dumps(res))
    return 0 if res.get("found") else 1


if __name__ == "__main__":
    sys.exit(main())
