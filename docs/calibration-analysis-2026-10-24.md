# Calibration analysis — 2026-10-24 gallery runs

Source: `tray-20261024.log`, 22:41–22:56. Per gun: Box Cal (24 taps) → host
5-point (5 pulls) → gallery (Gun 1: 35 shots, Gun 2: 41 shots). Screen
3840x2160, 96 DPI, 32 px white border on, 1€ smoothing on, target radius
118.8 px. Shots were the old 26-sample press latch (~260 ms at the measured
~99 Hz), mapped by the new least-squares 5-point fit.

The numbers below can be reproduced with
`python tools/gallery_report.py tray-20261024.log --since 22:41 --until 22:56`
(the "edge raw" source there is the `trigger DOWN raw=(x,y)` line logged with
each shot).

## Results

| | Gun 1 (sn11111) | Gun 2 (sn22222) |
|---|---|---|
| Median miss as played | 59 px (p90 85) | 62 px (p90 102) |
| Scale error, shot vs target | X +2.9 %, Y +4.8 % (too wide) | X +1.9 %, Y +8.7 % (too wide) |
| Miss with the edge raw instead of the latch (same cal) | 45 px | 62 px |
| Refit raw → target on the gallery shots, leave-one-out median | 34 px (edge raw), 36 px (latch) | 40 px (edge raw), 42 px (latch) |
| Same refit, affine / quadratic model | 37 / 31 px | 40 / 41 px |
| Screen the gun can reach (raw 99..9900, refit) | X 37–3764, Y 17–2074 | X 55–3761, Y 42–2041 |

Aiming was open loop: the edge-raw shots, mapped by the same cal, also show a
slope above 1 (Gun 1 X 1.020 / Y 1.044, Gun 2 X 1.017 / Y 1.085). If the
crosshair had been steered onto the targets, the slope would be ~1. So the
shot data is valid for fitting.

## Findings

1. **The 5-point corners were the error source.** The targets sat 60 px from
   the corners. After Box Cal the gun's raw output stops at 99 / 9900 before it
   reaches them: both guns logged exactly X = 99 at top-left and X = 9900 at
   top-right, and the refit puts the expected raw for the bottom corners at
   Y ≈ 10 020 (Gun 1) and ≈ 10 190 (Gun 2), past the clamp. The bottom-right
   samples also read well inward of the refit (Gun 2: 9731, 9670 instead of
   ~9949, ~10 190). Corner samples that read inward give a raw span that is
   too narrow, so the map comes out too wide — the 2–9 % above.
2. **The error floor is aim noise, not mapping shape.** After the best
   straight-line fit the leave-one-out median is 34–42 px. Affine and quadratic
   models do no better, so a homography or a non-linear fit is not worth
   building. Box Cal tap spread (3 taps per point) is ~19–24 px std, the same
   order.
3. **The ~260 ms latch did not help.** With the same cal, the edge raw is
   better on Gun 1 (45 vs 59 px) and equal on Gun 2 (62 vs 62 px). The trigger
   jolt the latch was built for (144–810 counts, measured in an earlier
   session) does not show in this data. Median |edge − latch| was ~85 raw
   counts, which is the ~260 ms of aim motion, not a jolt.
4. **The outer band is unreachable.** Up to ~120 px at the bottom and ~80 px
   at the right cannot be reached: the gun's raw output clamps first. For the
   same reason a pull aimed off-screen still maps inside, so off-screen reload
   (right button) mostly cannot fire.
5. **The test had two gaps.** Gallery targets never came within 159 px of the
   sides or 269 px of the top, so the edges were never measured. The `noise`
   figures on gallery close (587–1260 px) are taken while moving between
   targets and mean nothing; hold-still noise needs measure mode (M).

## Changes made from this (patch3)

- **Host calibration (`CalibrationWindow`):** 3×3 grid at W/14 from each edge
  (274 px at 4K, the Box Cal inset — clear of the clamp), 3 pulls per target
  (27 samples), least-squares per axis with one outlier pass
  (residual > max(80 px, 3 × median) dropped if ≥ 70 % remain). The tray log
  line `calibrated (27 samples, N kept, median residual R px)` reports the fit.
  `RectCalib.Map()` and `cal_<sn>.json` are unchanged.
- **Press latch (`GunState`):** time-based, measured from the moment the
  trigger switch closes (first tip report), `latch-ms.txt` ms back, default
  0 ms. Ring of 128 timestamped samples. Every press also records the aim at
  0 / 25 / 50 / 100 / 150 / 250 ms and the unfiltered edge report.
- **Gallery:** a `gallery [lag]` line per pull with the miss at each lag; one
  target in four in the left / right / bottom outer band.
- **Box Cal headroom (`GunBoxCal`, hypothesis):** `boxcal-headroom.txt` = N
  pulls the eight base points N % toward the centre. Absent = the box's own
  values, byte-identical to before. The aim: the gun's raw range covers more
  than the screen, so the whole screen is reachable and off-screen maps
  outside. Needs a host calibration afterwards.
- **`tools/gallery_report.py`:** the analysis above as a script (misses, edge
  vs inner, scale, lag sweep, refit, leave-one-out, reach, clamp count,
  `--write-cal`).

## Expected outcome (hypotheses to check)

- New host calibration: median miss near the refit numbers (~34–42 px), scale
  error within ±1 %.
- Lag sweep: the best lag at or near 0 ms.
- Headroom 12 %: the gun reaches every screen edge with roughly 250 or more
  raw counts to spare before the 99 / 9900 clamp (estimate from the refit
  spans: Gun 2 Y needs 10 585 raw counts for the screen today), and the gallery
  HUD shows OUT when the gun is aimed past an edge.
