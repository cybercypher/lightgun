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
# Revision history:
#   rev 1  first fix: mkdir /tmp/logs, target created before each new symlink.
#          Only converted real files once at startup, and only created targets
#          for the files it converted.
#   rev 2  recreate missing targets for links that already exist (every boot
#          after the first starts with symlinks and an empty /tmp); cleanup
#          ignores empty logs so the empty targets do not win the keep-2 window.
#   rev 3  the scan runs every minute, not just at startup. The gun's RTC is
#          bogus and jumps on each boot, so it creates a NEW day log shortly
#          after boot - after rev 1/2 had finished their one-shot scan. That
#          live file stayed on flash and grew unbounded (seen on 2026-10-22:
#          19700402.log + 19700403.log real files after a reboot).
#
# Safe to convert a log the daemon is using: gun opens/writes/closes its log per
# event and holds no log fd open (checked on the gun 2026-10-22 - fds 0/1/2 are
# /dev/null, no fd points at /app/*.log), so the next write simply follows the
# new symlink into tmpfs.

LOG_SRC_DIR=/app
LOG_DST_DIR=/tmp/logs

# sync_one: make one /app/<n>.log tmpfs-backed. A real file is replaced by a
# symlink (target created first, always); a symlink just gets its target
# recreated if it is dangling. Never truncates an existing target, so a live
# log is safe to re-check.
sync_one() {
    f=$1
    n=$(basename "$f")
    if [ -L "$f" ]; then
        [ -e "$f" ] || : > "$f"
        return
    fi
    [ -f "$f" ] || return
    : > "${LOG_DST_DIR}/${n}"
    rm -f "$f"
    ln -sf "${LOG_DST_DIR}/${n}" "$f"
}

sync_logs() {
    for f in ${LOG_SRC_DIR}/*.log; do
        [ -e "$f" ] || [ -L "$f" ] || continue
        sync_one "$f"
    done
}

mkdir -p "${LOG_DST_DIR}"
sync_logs

while true; do
    [ -d "${LOG_DST_DIR}" ] || mkdir -p "${LOG_DST_DIR}"
    total=$(du -sk ${LOG_DST_DIR} 2>/dev/null | cut -f1)
    if [ -n "$total" ] && [ "$total" -gt 5000 ]; then
        # keep the 2 newest non-empty logs, delete the rest. Empty files are
        # excluded: the empty targets recreated by sync_logs are always the
        # newest mtime and would otherwise push the real logs out.
        ls -tr ${LOG_DST_DIR}/*.log 2>/dev/null | while read f; do
            [ -s "$f" ] && echo "$f"
        done | head -n -2 | xargs -r rm -f
    fi
    # also catches a new day log created after boot, and re-alives targets
    sync_logs
    sleep 60
done
