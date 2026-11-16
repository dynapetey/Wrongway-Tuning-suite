"""Remove zero padding after the backup GPT in a hybrid xorriso image."""
import os
import struct
import sys


def trim(path):
    sector = 512
    with open(path, "r+b") as image:
        size = os.fstat(image.fileno()).st_size
        image.seek(sector)
        header = image.read(sector)
        if header[:8] != b"EFI PART" or len(header) != sector:
            raise ValueError("Missing primary GPT header")
        backup_lba, = struct.unpack_from("<Q", header, 32)
        end = (backup_lba + 1) * sector
        if backup_lba < 2 or not 0 <= size - end <= 1024 * 1024:
            raise ValueError("Unexpected GPT backup location")
        image.seek(backup_lba * sector)
        if image.read(sector)[:8] != b"EFI PART":
            raise ValueError("Missing backup GPT header")
        if any(image.read()):
            raise ValueError("Unexpected data after the backup GPT")
        image.truncate(end)


if __name__ == "__main__":
    trim(sys.argv[1])
