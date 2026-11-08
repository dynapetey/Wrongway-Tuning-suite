#!/bin/bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
root_dir=$(pwd -P)
if ! command -v apt-get >/dev/null; then
  printf '%s\n' 'Install Python 3.10+, Python Tk and Mono for your distribution, then run bash launch.sh.'
  exit 1
fi
if ! python3 -c 'import tkinter' >/dev/null 2>&1 || ! command -v mono >/dev/null || ! mono backend/wrongway-pcm.exe self-test >/dev/null 2>&1; then
  sudo apt-get update
  sudo apt-get install -y python3 python3-tk mono-devel
fi
chmod +x launch.sh test.sh backend/build.sh
desktop_dir="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
mkdir -p "$desktop_dir"
python3 - "$root_dir" "$desktop_dir/wrongway-tuner.desktop" <<'PY'
import sys
from pathlib import Path
root = sys.argv[1]
def escape(value):
    return value.replace('\\', '\\\\').replace('"', '\\"').replace(chr(96), '\\' + chr(96)).replace('$', '\\$').replace('%', '%%')
Path(sys.argv[2]).write_text('[Desktop Entry]\nType=Application\nName=WrongWay Tuner\nComment=GM P01/P59 calibration editor and PCM tools\nExec=sh "' + escape(root + '/launch.sh') + '"\nTerminal=false\nCategories=Utility;\n')
PY
printf '%s\n' 'Installed. Open WrongWay Tuner from your applications menu, or run bash launch.sh.'
printf '%s\n' 'If USB access is denied: sudo usermod -aG dialout "$USER"; then sign out and in.'
