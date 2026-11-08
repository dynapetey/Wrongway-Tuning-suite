#!/bin/sh
set -eu
suite_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)

if ! command -v mono >/dev/null 2>&1 ||
   ! command -v flock >/dev/null 2>&1 ||
   ! python3 -c 'import tkinter' >/dev/null 2>&1; then
    if ! command -v apt-get >/dev/null 2>&1; then
        printf '%s\n' 'Install Python 3 with Tk, Mono and util-linux for your distribution, then rerun this script.' >&2
        exit 1
    fi
    sudo apt-get update
    sudo apt-get install -y python3 python3-tk mono-devel util-linux
fi

mono "$suite_dir/PCM/backend/wrongway-pcm.exe" self-test >/dev/null
data_dir="${XDG_DATA_HOME:-$HOME/.local/share}"
config_dir="${XDG_CONFIG_HOME:-$HOME/.config}"
install -D -m 644 "$suite_dir/desktop/wrongway-tuning.directory" \
    "$data_dir/desktop-directories/wrongway-tuning.directory"
install -D -m 644 "$suite_dir/desktop/wrongway-tuning.menu" \
    "$config_dir/menus/applications-merged/wrongway-tuning.menu"
desktop_dir="$data_dir/applications"
mkdir -p "$desktop_dir"
python3 - "$suite_dir/launch.sh" "$desktop_dir/wrongway-suite.desktop" <<'PY'
from pathlib import Path
import sys

launcher, destination = sys.argv[1:]
quoted = ''.join('\\' + char if char in '"\\`$' else char for char in launcher)
quoted = quoted.replace('\\', '\\\\').replace('%', '%%')
Path(destination).write_text(
    '[Desktop Entry]\n'
    'Type=Application\n'
    'Name=WrongWay Suite\n'
    'Comment=GM scanner, P59 BIN editor and PCM Hammer flashing tools\n'
    f'Exec=/bin/sh "{quoted}"\n'
    'Terminal=false\n'
    'Icon=applications-system\n'
    'Categories=Utility;\n',
    encoding='utf-8',
)
PY
printf '%s\n' 'Installed. Open the application launcher and choose Tuning → WrongWay Suite.'
