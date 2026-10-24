#!/usr/bin/env bash
# Aim probe: move the Pico bridge to a screen point, screenshot the area, and
# report where the game's reticle actually is.
#
#   tools/win/probe.sh 1920 1080 [halfSize]
#
# Prints one JSON line: aim point, reticle position (screen px), delta.
# Requires logs/bridge/BridgeServer.ps1 running and the venv with pillow.

set -u
X=${1:?usage: probe.sh SCREEN_X SCREEN_Y [halfSize]}
Y=${2:?usage: probe.sh SCREEN_X SCREEN_Y [halfSize]}
HALF=${3:-260}
ROOT=/home/USER/lightgun
GAME_X=8
GAME_Y=31
SHOT="$ROOT/logs/shots/probe.png"

RX=$((X - HALF)); RY=$((Y - HALF))
RW=$((HALF * 2)); RH=$((HALF * 2))
# keep the crop inside the game content rect (3840x2141 at 8,31)
if [ $RX -lt $GAME_X ]; then RX=$GAME_X; fi
if [ $RY -lt $GAME_Y ]; then RY=$GAME_Y; fi
if [ $((RX + RW)) -gt $((GAME_X + 3840)) ]; then RX=$((GAME_X + 3840 - RW)); fi
if [ $((RY + RH)) -gt $((GAME_Y + 2141)) ]; then RY=$((GAME_Y + 2141 - RH)); fi

"$ROOT/tools/win/bridge" move "$X" "$Y" >/dev/null || exit 2
sleep ${SETTLE:-1.2}

powershell.exe -NoProfile -ExecutionPolicy Bypass \
  -File '\\wsl.localhost\Ubuntu-24.04\home\USER\lightgun\tools\win\Screen.ps1' \
  -Out '\\wsl.localhost\Ubuntu-24.04\home\USER\lightgun\logs\shots\probe.png' \
  -Rect "$RX,$RY,$RW,$RH" -Scale 1.0 >/dev/null || exit 3

EX=$((X - RX)); EY=$((Y - RY))
"$ROOT/.venv/bin/python" "$ROOT/tools/aim_probe.py" "$SHOT" --expect "$EX" "$EY" --radius "$HALF" |
  "$ROOT/.venv/bin/python" -c '
import json, sys
d = json.load(sys.stdin)
aim = (float(sys.argv[1]), float(sys.argv[2]))
rx, ry = float(sys.argv[3]), float(sys.argv[4])
if d.get("found"):
    sx = rx + d["x"]; sy = ry + d["y"]
    print(json.dumps({"aim": {"x": aim[0], "y": aim[1]},
                      "reticle": {"x": round(sx, 1), "y": round(sy, 1)},
                      "dx": round(sx - aim[0], 1), "dy": round(sy - aim[1], 1),
                      "pixels": d["pixels"]}))
else:
    print(json.dumps({"aim": {"x": aim[0], "y": aim[1]}, "found": False}))
' "$X" "$Y" "$RX" "$RY"
