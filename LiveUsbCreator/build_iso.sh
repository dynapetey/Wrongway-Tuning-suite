#!/bin/sh
set -eu

if [ "$#" -ne 4 ]; then
    printf 'Usage: %s BASE_WRONGWAY_ISO SUITE_DIR VERSION OUTPUT_ISO\n' "$0" >&2
    exit 2
fi

assets=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)
base_iso=$(realpath -- "$1")
suite_dir=$(realpath -- "$2")
version=$3
output=$(realpath -m -- "$4")
xorriso=${XORRISO:-xorriso}

case "$version" in
    ''|*[!0-9.]*|.*|*.) printf 'Invalid suite version: %s\n' "$version" >&2; exit 2 ;;
esac
if [ -e "$output" ]; then
    printf 'Output already exists: %s\n' "$output" >&2
    exit 1
fi

work=$(mktemp -d -- "$(dirname -- "$output")/.wrongway-iso.XXXXXXXX")
trap 'rm -rf -- "$work"' EXIT
trap 'exit 1' HUP INT TERM

# The base supplies Mint, the boot records, PCM Hammer, and the scanner's dependencies.
"$xorriso" -indev "$base_iso" -osirrox on \
    -extract /casper/zz-wrongway.squashfs "$work/base.squashfs"
unsquashfs -s "$work/base.squashfs" >/dev/null
rm -- "$work/base.squashfs"

layer="$work/layer"
target="$layer/opt/wrongway-suite"
install -D -m 755 "$suite_dir/launch.sh" "$target/launch.sh"
install -D -m 755 "$suite_dir/Scanner/WrongWayScanner" "$target/Scanner/WrongWayScanner"
install -D -m 644 "$suite_dir/Scanner/readout-bridge.py" "$target/Scanner/readout-bridge.py"
install -D -m 644 "$suite_dir/Scanner/README.md" "$target/Scanner/README.md"
install -D -m 644 "$suite_dir/README.md" "$target/README.md"
mkdir -p "$target/Scanner/Assets"
cp -R -- "$suite_dir/Scanner/Assets/." "$target/Scanner/Assets/"
install -D -m 755 "$assets/live-session.sh" "$layer/usr/local/bin/wrongway-session"
install -D -m 644 "$assets/71-wrongway-readout.rules" "$layer/etc/udev/rules.d/71-wrongway-readout.rules"
printf '%s\n' "$version" > "$target/VERSION"
mksquashfs "$layer" "$work/update.squashfs" -noappend -all-root -comp xz -processors 2

# Casper looks for an ext4 filesystem labeled writable on bootable USB media.
truncate -s 4G "$work/writable.ext4"
mkfs.ext4 -q -F -L writable -m 0 -O '^orphan_file' \
    -E lazy_itable_init=0,lazy_journal_init=0 "$work/writable.ext4"
e2fsck -fn "$work/writable.ext4"

printf 'WrongWay Tuning Live %s (Linux Mint 21.1 Xfce base)\n' "$version" > "$work/info"
"$xorriso" -indev "$base_iso" -outdev "$work/image.iso" -padding 0 \
    -map "$assets/boot.cfg" /boot/grub/grub.cfg \
    -map "$work/update.squashfs" /casper/zzz-wrongway-update.squashfs \
    -map "$work/info" /.disk/info \
    -boot_image any replay -append_partition 3 0x83 "$work/writable.ext4" -commit -end

python3 "$assets/trim_iso_padding.py" "$work/image.iso"
sfdisk --verify "$work/image.iso"

# Link into place only after xorriso has finished, while refusing any overwrite.
ln -- "$work/image.iso" "$output"
