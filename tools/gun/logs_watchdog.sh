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
#
# rev 2 (2026-10-22): on every boot after the first, /app/*.log are already
# symlinks while /tmp is fresh, so the targets are missing again. Rev 1 skipped
# symlinks entirely and left those links dangling; a log write that fails kills
# the whole app layer, so the targets are now recreated here (both at startup and
# once a minute, in case /tmp/logs is ever lost while running).

LOG_SRC_DIR=/app
LOG_DST_DIR=/tmp/logs

# /tmp is a fresh tmpfs every boot, and the /app entries are already symlinks
# from a previous run -- so the destination dir and every target file MUST be
# created before the links can resolve, or the daemon's log write hits a
# dangling link and the whole app layer dies (observed 2026-10-21).
mkdir -p "${LOG_DST_DIR}"

# ensure_link_targets: for every /app/*.log that is already a symlink into tmpfs,
# make sure the target file exists. `[ -e "$f" ]` is false for a dangling link,
# so `: > "$f"` creates the target through it; an existing target is untouched
# (never truncated), so a live log is safe to re-check while the daemon appends.
ensure_link_targets() {
    for f in ${LOG_SRC_DIR}/*.log; do
        [ -L "$f" ] || continue
        [ -e "$f" ] || : > "$f"
    done
}

# Path 1: move /app's real log files into tmpfs, replacing them with symlinks.
# Idempotent: symlinks from an earlier boot just get their target recreated, and
# a real file is only converted once (target created BEFORE the link).
ls -1 ${LOG_SRC_DIR}/*.log 2>/dev/null | while read f; do
    n=$(basename "$f")
    if [ -L "$f" ]; then
        [ -e "$f" ] || : > "$f"
        continue
    fi
    : > "${LOG_DST_DIR}/${n}"
    rm -f "$f"
    ln -sf "${LOG_DST_DIR}/${n}" "${LOG_SRC_DIR}/${n}"
done

# Path 2: keep the link targets alive + size-triggered cleanup, threshold 5 MB
# (tmpfs has 27.5 MB to work with).
while true; do
    [ -d "${LOG_DST_DIR}" ] || mkdir -p "${LOG_DST_DIR}"
    total=$(du -sk ${LOG_DST_DIR} 2>/dev/null | cut -f1)
    if [ -n "$total" ] && [ "$total" -gt 5000 ]; then
        # keep the 2 newest logs, delete the rest. Only non-empty files count:
        # the empty targets recreated below are always the newest mtime and
        # would otherwise push the real logs out and get them deleted.
        ls -tr ${LOG_DST_DIR}/*.log 2>/dev/null | while read f; do
            [ -s "$f" ] && echo "$f"
        done | head -n -2 | xargs -r rm -f
    fi
    # after a cleanup (or if /tmp/logs vanished) the links would dangle again
    ensure_link_targets
    sleep 60
done
