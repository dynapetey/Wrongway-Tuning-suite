#!/bin/sh
# Bundle an already installed, pinned Commander runtime without any credentials.
set -eu
if [ "$#" -ne 3 ]; then
    echo "Usage: $0 /path/to/node /path/to/node_modules /path/to/output.squashfs" >&2
    exit 2
fi
source_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)
node_binary=$(realpath "$1")
modules=$(realpath "$2")
output=$(realpath -m "$3")
test -x "$node_binary"
test -f "$modules/@wonderwhy-er/desktop-commander/dist/index.js"
test ! -e "$output"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT HUP INT TERM
mkdir -p "$work/opt/wrongway-remote" "$work/usr/local/bin" "$work/etc/xdg/autostart"
cp "$node_binary" "$work/opt/wrongway-remote/node"
cp -a "$modules" "$work/opt/wrongway-remote/node_modules"
install -m 755 "$source_dir/wrongway-remote-commander" "$work/usr/local/bin/"
install -m 644 "$source_dir/wrongway-remote-commander.desktop" "$work/etc/xdg/autostart/"
"$work/opt/wrongway-remote/node" "$work/opt/wrongway-remote/node_modules/@wonderwhy-er/desktop-commander/dist/index.js" remote --help
mksquashfs "$work" "$output" -noappend -all-root -comp zstd -processors 2
