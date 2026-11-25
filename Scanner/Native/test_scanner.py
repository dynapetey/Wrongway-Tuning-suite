#!/usr/bin/env python3
"""Exercise the suite scanner and native driver using synthetic GT responses."""
import csv
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from test_gt_emulator import Emulator

exe = Path(sys.argv[1]).resolve()
with tempfile.TemporaryDirectory(prefix="wrongway-j2534-test-") as directory:
    emulator = Emulator()
    emulator.start()
    try:
        result = subprocess.run([str(exe), "--discover-j2534", emulator.path],
                                capture_output=True, text=True, timeout=35)
        assert result.returncode == 0, result.stderr
        path = Path(directory) / "trims.csv"
        result = subprocess.run([str(exe), "--record-j2534", emulator.path,
                                 "FuelTrims", str(path), "2"],
                                capture_output=True, text=True, timeout=35)
        assert result.returncode == 0, result.stderr
        with path.open() as stream:
            rows = list(csv.reader(stream))
        assert len(rows) > 1, rows
        for row in rows[1:]:
            assert list(map(float, row[1:])) == [0, 12.5, -12.5, 0], row
        print("PASS: suite scanner SAE discovery and both-bank fuel-trim decoding through native GT emulator")
    finally:
        emulator.close()
