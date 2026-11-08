#!/bin/sh
set -eu
cd "$(dirname "$0")"
if ! python3 -c 'import tkinter' >/dev/null 2>&1; then
  echo 'Python Tk is missing. Run bash install.sh from the suite folder.' >&2
  exit 1
fi
if ! command -v mono >/dev/null; then
  echo 'Mono is missing. Run bash install.sh from the suite folder.' >&2
  exit 1
fi
exec python3 -m wrongway.app "$@"
