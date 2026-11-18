#!/usr/bin/env python3
"""Read scanner JSON across a local QEMU virtio serial port and serve VCM Telemetry HTTP/JSON."""

import argparse
from http.server import BaseHTTPRequestHandler, HTTPServer
import json
import os
import select
import socket
import subprocess
import time
from urllib.parse import urlparse


COMMANDS = {
    b"status": ["--scanner-status"],
    b"catalog": ["--catalog"],
    b"sessions": ["--telemetry-sessions"],
}
GUEST_PORT = "/dev/virtio-ports/org.wrongway.readout"


def guest_reply(scanner, request):
    try:
        if request == b"catalog":
            status = subprocess.run([scanner, "--scanner-status"], capture_output=True, timeout=4)
            if status.returncode == 0:
                os_id = json.loads(status.stdout)["CatalogOs"]
                return subprocess.check_output([scanner, "--catalog", str(os_id)], timeout=6)
        return subprocess.check_output([scanner, *COMMANDS[request]], stderr=subprocess.PIPE, timeout=6)
    except (OSError, subprocess.SubprocessError, KeyError, ValueError) as error:
        detail = getattr(error, "stderr", None)
        return (json.dumps({"Error": detail.decode(errors="replace").strip() if detail else str(error)}) + "\n").encode()


def guest(scanner):
    port = os.open(GUEST_PORT, os.O_RDWR | os.O_NOCTTY | os.O_NONBLOCK)
    with os.fdopen(port, "r+b", buffering=0) as connection:
        request = bytearray()
        while True:
            select.select([port], [], [])
            incoming = connection.read(256)
            if not incoming:
                time.sleep(0.1)
                continue
            for byte in incoming:
                if byte != 10:
                    if len(request) < 16:
                        request.append(byte)
                    continue
                command = bytes(request)
                request.clear()
                if command not in COMMANDS:
                    continue
                reply = guest_reply(scanner, command)
                remaining = memoryview(reply)
                while remaining and select.select([], [port], [], 2)[1]:
                    try:
                        sent = connection.write(remaining)
                    except (BrokenPipeError, BlockingIOError):
                        break
                    if not sent:
                        break
                    remaining = remaining[sent:]


def host(path, command):
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
        connection.settimeout(12)
        connection.connect(path)
        connection.sendall(command.encode() + b"\n")
        with connection.makefile("rb") as response:
            reply = response.readline(1024 * 1024)
    data = json.loads(reply)
    print(json.dumps(data))
    return int("Error" in data)


DASHBOARD_HTML = """<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>WrongWay VCM Telemetry</title>
<style>
  body { background: #12161e; color: #e0e8f4; font-family: 'DejaVu Sans', sans-serif; margin: 0; padding: 20px; }
  h1 { font-size: 20px; margin: 0 0 6px; color: #639dff; }
  .sub { color: #a9b7cd; font-size: 13px; margin-bottom: 18px; }
  .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(210px, 1fr)); gap: 10px; margin-bottom: 20px; }
  .card { background: #181d26; border: 1px solid #2c3545; border-radius: 4px; padding: 12px; }
  .label { color: #a9b7cd; font-size: 12px; }
  .val { color: #64e0cd; font-size: 24px; font-weight: bold; margin-top: 4px; }
  .unit { color: #a9b7cd; font-size: 12px; margin-left: 4px; }
  table { width: 100%; border-collapse: collapse; background: #181d26; border: 1px solid #2c3545; }
  th, td { text-align: left; padding: 8px 10px; border-bottom: 1px solid #2c3545; font-size: 13px; }
  th { background: #1c2431; color: #639dff; }
</style>
</head>
<body>
  <h1>WrongWay VCM Telemetry · Live Dashboard</h1>
  <div class="sub" id="meta">Connecting to local scanner readout…</div>
  <div class="grid" id="gauges"></div>
  <h1 style="font-size:16px;margin-top:20px;">Recorded Telemetry Sessions</h1>
  <table>
    <thead><tr><th>Session</th><th>Recorded UTC</th><th>OS ID</th><th>Frames</th><th>Duration (s)</th></tr></thead>
    <tbody id="sessions"></tbody>
  </table>
<script>
async function poll() {
  const responses = await Promise.all(['/api/status', '/api/sessions'].map(path => fetch(path)));
  const [status, sessions] = await Promise.all(responses.map(response => response.json()));
  const meta = document.getElementById('meta');
  meta.textContent = status.Error ? 'Scanner offline' :
    `Mode: ${status.Mode || 'OFFLINE'} · OS: ${status.LastIdentifiedOs || status.CatalogOs || '-'} · Frames: ${status.Frames || 0} · Cursor: ${((status.CursorMilliseconds || 0) / 1000).toFixed(2)}s`;
  const cards = document.getElementById('gauges');
  cards.replaceChildren();
  for (const ch of (status.Channels || [])) {
    const card = document.createElement('div');
    card.className = 'card';
    const label = document.createElement('div');
    label.className = 'label';
    label.textContent = ch.Name;
    const value = document.createElement('div');
    value.className = 'val';
    value.textContent = Number.isFinite(ch.Value) ? ch.Value.toFixed(2) : '—';
    const unit = document.createElement('span');
    unit.className = 'unit';
    unit.textContent = ch.Unit || '';
    value.append(unit);
    card.append(label, value);
    cards.append(card);
  }
  const table = document.getElementById('sessions');
  table.replaceChildren();
  for (const session of (Array.isArray(sessions) ? sessions : [])) {
    const row = table.insertRow();
    for (const value of [session.Name, session.RecordedUtc, session.OsId ?? '—', session.Frames, session.DurationSeconds])
      row.insertCell().textContent = value;
  }
}
async function refresh() {
  try {
    await poll();
  } catch {
    document.getElementById('meta').textContent = 'Scanner readout unavailable';
  } finally {
    setTimeout(refresh, 1000);
  }
}
refresh();
</script>
</body>
</html>
"""


def serve_http(scanner, bind_host, port):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, fmt, *args):
            pass

        def send_body(self, body, content_type, status=200):
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            host = self.headers.get("Host")
            if host not in (f"127.0.0.1:{server.server_port}", f"localhost:{server.server_port}"):
                self.send_error(403)
                return
            path = urlparse(self.path).path
            if path in ("/", "/index.html"):
                self.send_body(DASHBOARD_HTML.encode("utf-8"), "text/html; charset=utf-8")
                return
            command = {"/api/status": b"status", "/api/catalog": b"catalog", "/api/sessions": b"sessions"}.get(path)
            if command is None:
                self.send_body(b'{"Error": "Not found"}', "application/json", 404)
                return
            reply = guest_reply(scanner, command)
            if command == b"sessions":
                data = json.loads(reply)
                if isinstance(data, list):
                    for session in data:
                        session.pop("Path", None)
                        session.pop("Vin", None)
                    reply = json.dumps(data).encode("utf-8")
            self.send_body(reply, "application/json; charset=utf-8")

    with HTTPServer((bind_host, port), Handler) as server:
        print(f"WrongWay VCM Telemetry server listening on http://{bind_host}:{server.server_port}", flush=True)
        server.serve_forever()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_subparsers(dest="mode", required=True)
    guest_mode = modes.add_parser("guest")
    guest_mode.add_argument("scanner")
    host_mode = modes.add_parser("host")
    host_mode.add_argument("socket")
    host_mode.add_argument("command", choices=[command.decode() for command in COMMANDS])
    web_mode = modes.add_parser("serve-http")
    web_mode.add_argument("scanner")
    web_mode.add_argument("--bind", choices=["127.0.0.1"], default="127.0.0.1")
    web_mode.add_argument("--port", type=int, default=8765)
    args = parser.parse_args()
    if args.mode == "guest":
        guest(args.scanner)
        return 0
    if args.mode == "serve-http":
        serve_http(args.scanner, args.bind, args.port)
        return 0
    return host(args.socket, args.command)


if __name__ == "__main__":
    raise SystemExit(main())
