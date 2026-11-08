#!/usr/bin/env python3
import hashlib
import json
from pathlib import Path
root = Path(__file__).resolve().parent
backend = root / 'backend'
names = [p for p in backend.iterdir() if p.is_file() and (p.suffix in ('.dll', '.exe', '.bin', '.xml', '.LogProfile'))]
hashes = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(names)}
(backend / 'SHA256.json').write_text(json.dumps(hashes, indent=2) + '\n')
print('Backend manifest:', len(hashes), 'files')
