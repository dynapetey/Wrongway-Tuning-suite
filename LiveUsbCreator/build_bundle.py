#!/usr/bin/env python3
"""Package the Windows launcher and tested WrongWay live overlays."""
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import zipfile


BASE_FILES = ("casper/filesystem.squashfs", "casper/vmlinuz", "casper/initrd.lz")
MEDIA_FILES = (
    ".disk/base_installable",
    ".disk/casper-uuid-generic",
    ".disk/cd_type",
    ".disk/live-uuid-generic",
    "casper/filesystem.manifest",
    "casper/filesystem.manifest-remove",
    "casper/filesystem.size",
    "EFI/boot/grubx64.efi",
    "EFI/boot/mmx64.efi",
)
CREATOR_FILES = (
    "Create WrongWay Live USB.cmd",
    "WrongWayUsbCreator.ps1",
    "LiveUsbCore.ps1",
    "README.md",
)
PERSISTENCE_BYTES = 4278190080


def sha256(path: Path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def manifest_entry(name, kind, path):
    return {
        "Name": name,
        "Kind": kind,
        "Bytes": path.stat().st_size,
        "Hash": sha256(path),
    }


def pack(base_tree, update, output):
    source_dir = Path(__file__).resolve().parent
    if output.exists():
        raise FileExistsError(output)

    files = [(name, base_tree / name) for name in MEDIA_FILES]
    files.extend((
        ("EFI/boot/grub.cfg", source_dir / "boot.cfg"),
        ("boot/grub/grub.cfg", source_dir / "boot.cfg"),
        ("casper/zz-wrongway.squashfs", base_tree / "casper/zz-wrongway.squashfs"),
        ("casper/zzz-wrongway-update.squashfs", update),
        ("WRONGWAY-README.txt", source_dir / "WRONGWAY-README.txt"),
    ))
    manifest = [manifest_entry(name, "base", base_tree / name) for name in BASE_FILES]
    manifest.extend(manifest_entry(name, "bundle", path) for name, path in files)

    with tempfile.TemporaryDirectory(prefix="wrongway-bundle-") as temp:
        persistence = Path(temp) / "writable"
        with persistence.open("wb") as filesystem:
            filesystem.truncate(PERSISTENCE_BYTES)
        subprocess.run(["mkfs.ext4", "-q", "-F", "-L", "writable", "-m", "0", "-O", "^orphan_file",
                        "-E", "lazy_itable_init=0,lazy_journal_init=0", persistence], check=True)
        subprocess.run(["e2fsck", "-fn", persistence], check=True)
        compressed = Path(temp) / "writable.gz"
        with persistence.open("rb") as source, compressed.open("wb") as target:
            with gzip.GzipFile(filename="", mode="wb", compresslevel=6, fileobj=target, mtime=0) as packed:
                shutil.copyfileobj(source, packed, length=1024 * 1024)
        manifest.append({
            **manifest_entry("writable", "gzip", persistence),
            "StoredHash": sha256(compressed),
        })

        # Install the firmware entry point last, after the verified payload.
        efi = "EFI/boot/bootx64.efi"
        files.append((efi, base_tree / efi))
        manifest.append(manifest_entry(efi, "bundle", base_tree / efi))
        with zipfile.ZipFile(output, "x", compression=zipfile.ZIP_STORED) as archive:
            for name in CREATOR_FILES:
                if name.endswith(".cmd"):
                    archive.writestr(name, (source_dir / name).read_text().replace("\n", "\r\n"))
                else:
                    archive.write(source_dir / name, name)
            for name, path in files:
                archive.write(path, f"payload/files/{name}")
            archive.writestr("payload/files/.disk/info", "WrongWay Tuning Live 0.4.8\n")
            manifest.insert(-1, {
                "Name": ".disk/info", "Kind": "bundle", "Bytes": len(b"WrongWay Tuning Live 0.4.8\n"),
                "Hash": hashlib.sha256(b"WrongWay Tuning Live 0.4.8\n").hexdigest(),
            })
            archive.write(compressed, "payload/writable.gz")
            archive.writestr("payload/manifest.json", json.dumps({
                "Schema": 1, "SuiteVersion": "0.4.8", "Files": manifest
            }, indent=2) + "\n")

    print(f"{sha256(output)}  {output.name}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-tree", type=Path, required=True,
                        help="Prepared and verified Mint 21.1 Xfce live tree plus initial WrongWay layer")
    parser.add_argument("--update-layer", type=Path, required=True,
                        help="WrongWay Suite 0.4.8 SquashFS update layer")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    pack(args.base_tree, args.update_layer, args.output)


if __name__ == "__main__":
    main()
