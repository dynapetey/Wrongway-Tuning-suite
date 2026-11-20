#!/usr/bin/env bash
set -euo pipefail

REPO_DIR="${1:-$HOME/Wrongway-Tuning-suite}"
TELEMETRY_SRC="${2:-$HOME/Wrongway-Telemetry/Wrongway-Tuning-suite-main}"

if [ ! -d "$REPO_DIR/.git" ]; then
    echo "Error: $REPO_DIR is not a git repository." >&2
    exit 1
fi

cd "$REPO_DIR"

# 1. Sync updated source & VCM assets from extracted Telemetry.zip if present
if [ -d "$TELEMETRY_SRC" ]; then
    echo "==> Syncing files from $TELEMETRY_SRC..."
    cp -a "$TELEMETRY_SRC/." "$REPO_DIR/"
fi

# 2. Install .NET 8 SDK and Mono compiler if missing
if ! command -v dotnet >/dev/null 2>&1 || ! command -v mcs >/dev/null 2>&1; then
    echo "==> Installing .NET 8 SDK and Mono compiler (mono-mcs)..."
    sudo apt-get update
    if ! sudo apt-get install -y dotnet-sdk-8.0 mono-mcs; then
        echo "==> Adding Microsoft Ubuntu package feed for dotnet-sdk-8.0..."
        . /etc/os-release
        wget -q "https://packages.microsoft.com/config/ubuntu/${VERSION_ID}/packages-microsoft-prod.deb" -O /tmp/packages-microsoft-prod.deb
        sudo dpkg -i /tmp/packages-microsoft-prod.deb
        rm -f /tmp/packages-microsoft-prod.deb
        sudo apt-get update
        sudo apt-get install -y dotnet-sdk-8.0 mono-mcs
    fi
fi

# 3. Build WrongWayScanner (.NET 8) and install binary + Assets/VCM
echo "==> Building WrongWayScanner..."
dotnet publish Scanner/Source/WrongWayScanner.csproj -c Release -o build/scanner
install -m 755 build/scanner/WrongWayScanner Scanner/WrongWayScanner
if [ -d build/scanner/Assets/VCM ]; then
    rm -rf Scanner/Assets/VCM
    cp -a build/scanner/Assets/VCM Scanner/Assets/VCM
fi

# 4. Rebuild Mono PCM backend and refresh SHA256 manifests
echo "==> Building PCM/backend/wrongway-pcm.exe..."
mcs -out:PCM/backend/wrongway-pcm.exe \
    -r:PCM/backend/PcmHacking.dll \
    -r:PCM/backend/Newtonsoft.Json.dll \
    PCM/backend/source/LinuxBackend.cs

python3 - <<'PY'
import hashlib, json
from pathlib import Path
for sha_path in [Path("PCM/backend/SHA256.json"), Path("PCM/SHA256.json")]:
    if sha_path.is_file():
        data = json.loads(sha_path.read_text())
        base = sha_path.parent
        for rel in list(data.keys()):
            target = base / rel
            if target.is_file():
                data[rel] = hashlib.sha256(target.read_bytes()).hexdigest()
        sha_path.write_text(json.dumps(data, indent=2) + "\n")
        print(f"Updated {sha_path}")
PY

# 5. Run all self-tests and unit tests
echo "==> Running Scanner self-test..."
./Scanner/WrongWayScanner --self-test

echo "==> Running GT + VCM Controls PTY hardware simulation tests..."
python3 Scanner/Tests/test_gt.py ./Scanner/WrongWayScanner

echo "==> Running PCM unit & Mono backend tests..."
PYTHONPATH=PCM python3 -m unittest discover -s PCM/tests -p "test_*.py" -v

# 6. Commit & push to GitHub
echo "==> Committing and pushing to origin main..."
git add -A
if ! git diff --cached --quiet; then
    git commit -m "Add VCM Telemetry math/filter engine, native DVI/VPW VCM Controls, and bundled HP Tuners presets"
fi
git push origin main

echo "==> Done! Run 'bash install.sh && ./launch.sh' to install desktop launcher and start WrongWay Suite."