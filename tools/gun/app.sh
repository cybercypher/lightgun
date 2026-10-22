#!/bin/sh
# /app/shell/app.sh — launcher shell (vendor's file + one watchdog launch line).
#
# Change from vendor-shipped file: the logs-watchdog is started in the start
# branch, after the mount remount, before the gadget apps. Everything else
# (including the commented-out $APP_NAME launch, which is the vendor's own
# shipped state — the app daemon is actually started by /app/bin/usb_monitor)
# is byte-identical to the shipped /app tree. Restore point:
# logs/gunref/app_backup.tgz.
APP_NAME=/app/bin/gun
UVC_APP=uvc-gadget
USB_MONITOR=/app/bin/usb_monitor

# QT env
source /etc/profile

case "$1" in
	start)
		LOG_DIR="/app"
		LOG_COUNT=$(ls -1 ${LOG_DIR}/*.log 2>/dev/null | wc -l)
		if [ $LOG_COUNT -gt 4 ]; then
			OLDEST_LOG=$(ls -tr ${LOG_DIR}/*.log | head -1)
			echo "Deleting oldest log file: $OLDEST_LOG"
			rm -f "$OLDEST_LOG"
		else
			echo "No need to delete files. There are only $LOG_COUNT log files."
		fi

		mount -o rw,remount /
		# logs watchdog: keeps /app/.log under control (see logs_watchdog.sh)
		/app/shell/logs_watchdog.sh &
		$UVC_APP &
		$USB_MONITOR &
		# $APP_NAME &

		;;
	stop)
		# pkill -9 $APP_NAME
		usleep 100000
		;;
	*)
		echo "($0): invalid option, exit..."
		exit ${LINENO}
		;;
esac
