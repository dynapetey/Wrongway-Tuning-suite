#!/bin/sh
set -eu

xfwm4 --compositor=off &
manager=$!
readout=
cleanup() {
    if [ -n "$readout" ]; then
        kill "$readout" 2>/dev/null || :
    fi
    kill "$maximizer" "$manager" 2>/dev/null || :
}
(
    attempts=0
    until wmctrl -r 'WrongWay Scanner + Hammerdroid' -b add,maximized_vert,maximized_horz 2>/dev/null; do
        attempts=$((attempts + 1))
        [ "$attempts" -lt 90 ] || exit 0
        sleep 1
    done
) &
maximizer=$!
trap cleanup EXIT

cd /opt/wrongway-suite
port=/dev/virtio-ports/org.wrongway.readout
if [ -r "$port" ] && [ -w "$port" ]; then
    python3 Scanner/readout-bridge.py guest /opt/wrongway-suite/Scanner/WrongWayScanner &
    readout=$!
fi

./launch.sh || {
    if command -v zenity >/dev/null 2>&1; then
        zenity --error --text='WrongWay Suite closed with an error.'
    fi
}
