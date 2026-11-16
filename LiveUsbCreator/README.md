# WrongWay Live USB Creator for Windows

This creator writes WrongWay Suite 0.4.8 to a bootable x64 UEFI USB. It uses Linux Mint 21.1 Xfce and makes a fresh writable area of about 4 GiB for your settings, logs, and tunes.

## Create the USB

1. Extract the full Windows release ZIP to a folder on your Windows PC.
2. Plug in an **8 GB or larger** USB drive. Back up anything you want to keep on that drive.
3. Open **Create WrongWay Live USB.cmd** and approve the Windows administrator prompt.
4. Select the USB by its disk number, name, capacity, and drive letter. Leave the ISO field blank to download and cache Linux Mint automatically, or use **Browse** to select a local [Linux Mint 21.1 Xfce 64-bit ISO](https://linuxmint.com/edition.php?id=304).
5. Click **Create USB** and confirm the drive to erase. The creator verifies the files, formats the drive, writes the boot files and tuner, and verifies the copy. Eject the USB after the app reports it ready.

Boot the new stick using its **UEFI** entry. WrongWay Scanner opens automatically. Click **Tune BIN** for the calibration editor. Store files in `/home/wrongway` in the live system to keep them across reboots.

The creator uses one FAT32 partition of up to 29 GiB. Windows can still use the free space inside that partition. On the legacy HP Compaq with GRUB already installed, press **C** at its GRUB menu and run the following commands with the USB plugged in.

```
search --no-floppy --label --set=root WRONGWAY
configfile /boot/grub/grub.cfg
```

## Package the release

Run from Linux with Python 3.11+, `mkfs.ext4`, and `e2fsck`. Supply the verified Mint live file tree with the initial WrongWay SquashFS layer, plus the 0.4.8 update layer. The packager creates a new empty persistence filesystem.

```sh
python3 build_bundle.py --base-tree /path/to/verified/live-tree \
  --update-layer /path/to/zzz-wrongway-update.squashfs \
  --output WrongWay-Live-USB-Creator-Windows-v0.4.8.zip
```

## Linux live ISO

The hybrid 0.4.9 ISO boots on x64 UEFI and legacy BIOS. Its ext4 partition
labeled `writable` provides about 4 GiB of storage across boots. Use an
8 GB or larger USB and write the ISO as a disk image to the whole device.
Image writing replaces the partition table and the files on that USB.
In a VM, attach a fresh copy of the ISO as a writable **USB disk** with
raw format to use its persistence partition. A virtual DVD is read-only.
Save settings, logs, and BIN copies under `/home/wrongway` and shut down
before disconnecting the device.

Download both numbered `.iso.gz.part-*` files and `SHA256SUMS` from the
[Linux live ISO release](https://github.com/dynapetey/Wrongway-Tuning-suite/releases/tag/live-iso-v0.4.9).
Run these commands in the download directory to assemble and verify the ISO.

```sh
sha256sum --ignore-missing -c SHA256SUMS
cat WrongWay-Tuning-Live-0.4.9.iso.gz.part-00 WrongWay-Tuning-Live-0.4.9.iso.gz.part-01 \
  | gzip -dc > WrongWay-Tuning-Live-0.4.9.iso
sha256sum -c SHA256SUMS
```

To rebuild, use the existing WrongWay Tuning Live hybrid ISO based on
Linux Mint 21.1 Xfce, and a matching Linux suite directory containing the
published `Scanner/WrongWayScanner`. The build needs `xorriso`,
`squashfs-tools`, and `e2fsprogs`.

```sh
sh LiveUsbCreator/build_iso.sh /path/to/WrongWay-Tuning-Live-0.4.5.iso \
  /path/to/suite 0.4.9 /path/to/WrongWay-Tuning-Live-0.4.9.iso
```
