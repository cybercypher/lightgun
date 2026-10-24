#!/usr/bin/env bash
# Wait until the game shows a given screen, by colour signature.
#
#   tools/win/wait-screen.sh mission [timeoutSeconds]
#   tools/win/wait-screen.sh gameplay [timeoutSeconds]
#   tools/win/wait-screen.sh any [timeoutSeconds]
#
# Signatures (measured from screenshots):
#   mission  - MISSION SELECT: a band of saturated red text across the top
#              centre on a dark background.
#   gameplay - the in-game HUD: bright cyan reticle somewhere on screen plus
#              the ammo bar at the bottom left.
#   any      - a non-black frame (the game is drawing).
#
# Prints the screen name and the capture path on success; exit 1 on timeout.

set -u
WANT=${1:-mission}
TIMEOUT=${2:-240}
ROOT=/home/USER/lightgun
GAME_Y=31
OUT="$ROOT/logs/shots/wait.png"

deadline=$(( $(date +%s) + TIMEOUT ))
while [ "$(date +%s)" -lt "$deadline" ]; do
    powershell.exe -NoProfile -ExecutionPolicy Bypass \
        -File '\\wsl.localhost\Ubuntu-24.04\home\USER\lightgun\tools\win\Screen.ps1' \
        -Out '\\wsl.localhost\Ubuntu-24.04\home\USER\lightgun\logs\shots\wait.png' \
        -Rect "0,${GAME_Y},3840,2141" -Scale 0.25 >/dev/null 2>&1
    RES=$("$ROOT/.venv/bin/python" - "$OUT" "$WANT" <<'EOF'
import sys
from PIL import Image
import numpy as np
img = np.asarray(Image.open(sys.argv[1]).convert("RGB")).astype(int)
h, w, _ = img.shape
want = sys.argv[2]
top = img[int(h*0.02):int(h*0.12), int(w*0.2):int(w*0.8)]
r, g, b = top[:,:,0], top[:,:,1], top[:,:,2]
red = ((r > 120) & (g < 80) & (b < 80)).mean()
dark = (r + g + b < 180).mean()
mean = img.mean()
cyan = ((img[:,:,2] - img[:,:,0] > 70) & (img[:,:,2] > 120) & (img[:,:,1] > 90) & (img[:,:,0] < 110)).mean()
if want == "mission":
    ok = red > 0.04 and dark > 0.25
elif want == "gameplay":
    ok = cyan > 0.0008 and mean > 20
else:
    ok = mean > 12
print(f"{'found' if ok else 'no'} red={red:.3f} dark={dark:.2f} cyan={cyan:.5f} mean={mean:.0f}")
sys.exit(0 if ok else 1)
EOF
)
    if [ $? -eq 0 ]; then
        echo "$WANT: $RES"
        exit 0
    fi
    sleep 4
done
echo "timeout waiting for $WANT (last: $RES)" >&2
exit 1
