#!/bin/sh
# /app/shell/logs_watchdog.sh
# Keeps the G'AIM'E gun's daily logs from filling the 3.9 MB /app partition.
#
# Failure mode this fixes (diagnosed 2026-10-21 on sn11111): the daemon writes one
# /app/<day>.log per boot/day; over ~24 h of host polling the heartbeat lines are
# ~85 B each and reach ~3.7 MB/day, which filled /app to 100% so the app could not
# create its own log and the whole application layer (aim, buttons, config
# endpoint) died at startup.
#
# Two layers, no dependencies, no crond required:
#   1. re-point every log file on /app into tmpfs (RAM). /tmp is wiped at reboot,
#      so nothing accumulates on flash.
#   2. a size-triggered cleanup that keeps the 2 newest logs once /tmp/logs
#      exceeds ~5 MB total.
#
# Reversible: everything this script changes is inside /app (file-tree only);
# logs/gunref/app_backup.tgz is the bit-verified restore point.

LOG_SRC_DIR=/app
LOG_DST_DIR=/tmp/logs

# /tmp is a fresh tmpfs every boot, and the /app entries are already symlinks
# from a previous run -- so the destination dir and every target file MUST be
# created before the links can resolve, or the daemon's log write hits a
# dangling link and the whole app layer dies (observed 2026-10-21).
mkdir -p "${LOG_DST_DIR}"

# Path 1: move /app's real log files into tmpfs, replacing them with symlinks.
# Idempotent: existing symlinks are left alone (so a later run only trims RAM).
# Target created BEFORE the link for the same reason as above.
ls -1 ${LOG_SRC_DIR}/*.log 2>/dev/null | while read f; do
    n=$(basename "$f")
    if [ -L "$f" ]; then
        continue
    fi
    : > "${LOG_DST_DIR}/${n}"
    rm -f "$f"
    ln -sf "${LOG_DST_DIR}/${n}" "${LOG_SRC_DIR}/${n}"
done

# Path 2: size-triggered cleanup, threshold 5 MB (tmpfs has 27.5 MB to work with).
while true; do
    total=$(du -sk ${LOG_DST_DIR} 2>/dev/null | cut -f1)
    if [ -n "$total" ] && [ "$total" -gt 5000 ]; then
        # keep the 2 newest logs, delete the rest
        ls -tr ${LOG_DST_DIR}/*.log 2>/dev/null | head -n -2 | xargs -r rm -f
    fi
    sleep 60
done
