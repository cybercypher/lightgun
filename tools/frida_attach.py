#!/usr/bin/env python3
"""Frida attach driver for pcsx2-qtx64.exe (Windows side).

Usage (from Windows, via WSL UNC paths):
    python \\\\wsl.localhost\\Ubuntu-24.04\\home\\USER\\lightgun\\tools\\frida_attach.py <script.js>

Attaches by process name, loads the given JS, and mirrors every send()
message to logs/frida-session.log (repo, via UNC) with timestamps.
Keep it running; it detaches when the target exits (logged).
"""
import sys
import time
import datetime
import frida

TARGET = "pcsx2-qtx64.exe"
LOG = r"\\wsl.localhost\Ubuntu-24.04\home\USER\lightgun\logs\frida-session.log"

log = open(LOG, "a", buffering=1, encoding="utf-8", errors="replace")


def ts():
    return datetime.datetime.now().strftime("%H:%M:%S.%f")[:-3]


def on_message(message, data):
    try:
        if message.get("type") == "send":
            log.write("%s %s\n" % (ts(), message.get("payload")))
        elif message.get("type") == "error":
            log.write("%s JSERROR %s @ %s:%s\n" % (
                ts(), message.get("description"),
                message.get("fileName"), message.get("lineNumber")))
        else:
            log.write("%s %r\n" % (ts(), message))
    except Exception as ex:
        try:
            log.write("%s LOGERR %s\n" % (ts(), ex))
        except Exception:
            pass


def main():
    log.write("%s === attach %s script=%s ===\n" % (ts(), TARGET, sys.argv[1]))
    session = None
    deadline = time.time() + 900
    while session is None:
        try:
            session = frida.attach(TARGET)
        except Exception as ex:
            if time.time() > deadline:
                log.write("%s ATTACH-FAILED (timeout) %s\n" % (ts(), ex))
                sys.exit(2)
            log.write("%s waiting for %s...\n" % (ts(), TARGET))
            time.sleep(2)
    log.write("%s attached\n" % ts())

    def on_detach(reason, *a):
        log.write("%s DETACHED %s\n" % (ts(), reason))

    session.on("detached", on_detach)
    with open(sys.argv[1], "r", encoding="utf-8") as f:
        src = f.read()
    script = session.create_script(src)
    script.on("message", on_message)
    script.load()
    log.write("%s script loaded\n" % ts())
    while True:
        time.sleep(2)


if __name__ == "__main__":
    main()
