#!/bin/sh
set -eu
suite_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)

case "${1:-}" in
    -h|--help)
        printf '%s\n' 'Usage: ./launch.sh [scanner [log.hpl|log.csv]]' \
            '       ./launch.sh scanner --tune BIN XDF' \
            '       ./launch.sh scanner --catalog [OS] | --scanner-status' \
            '       ./launch.sh pcm [--port /dev/ttyUSB0]'
        exit 0
        ;;
    pcm)
        shift
        set -- /bin/sh "$suite_dir/PCM/launch.sh" --pcm-only "$@"
        ;;
    scanner)
        shift
        set -- "$suite_dir/Scanner/WrongWayScanner" "$@"
        ;;
    *)
        set -- "$suite_dir/Scanner/WrongWayScanner" "$@"
        ;;
esac

if [ "$1" = "$suite_dir/Scanner/WrongWayScanner" ]; then
    case "${2:-}" in
        --catalog|--scanner-status|--inspect|--tune-inspect) exec "$@" ;;
    esac
fi

if ! command -v flock >/dev/null 2>&1; then
    printf '%s\n' 'Install util-linux to enable the suite session lock.' >&2
    exit 1
fi
lock_dir="${XDG_RUNTIME_DIR:-${XDG_STATE_HOME:-$HOME/.local/state}}/wrongway-suite"
mkdir -p -m 700 "$lock_dir"
if flock -n -E 73 "$lock_dir/session.lock" "$@"; then
    exit 0
else
    result=$?
fi
if [ "$result" -eq 73 ]; then
    printf '%s\n' 'WrongWay Suite is already open. Use Tools → PCM & flashing in its scanner window.' >&2
fi
exit "$result"
