#!/usr/bin/env python3
"""Read scanner JSON across a local QEMU virtio serial port."""

import argparse
import json
import os
import select
import socket
import subprocess
import time


COMMANDS = {b"status": ["--scanner-status"], b"catalog": ["--catalog"]}
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
                    if len(request) < 8:
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_subparsers(dest="mode", required=True)
    guest_mode = modes.add_parser("guest")
    guest_mode.add_argument("scanner")
    host_mode = modes.add_parser("host")
    host_mode.add_argument("socket")
    host_mode.add_argument("command", choices=[command.decode() for command in COMMANDS])
    args = parser.parse_args()
    if args.mode == "guest":
        guest(args.scanner)
        return 0
    return host(args.socket, args.command)


if __name__ == "__main__":
    raise SystemExit(main())
