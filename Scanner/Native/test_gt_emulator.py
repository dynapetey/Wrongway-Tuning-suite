#!/usr/bin/env python3
"""PTY-based OBDX Pro GT DVI and P59 Mode 01 simulator for experimental J2534 driver."""
import ctypes
import hashlib
from inspect_p59_calibration import inspect as inspect_calibration
import sys
import os
import pty
import select
import threading
import time
from pathlib import Path

LIB = str(Path(__file__).with_name("libobdx_j2534.so"))
class Message(ctypes.Structure):
    _fields_ = [("ProtocolID", ctypes.c_uint32), ("RxStatus", ctypes.c_uint32),
                ("TxFlags", ctypes.c_uint32), ("Timestamp", ctypes.c_uint32),
                ("DataSize", ctypes.c_uint32), ("ExtraDataIndex", ctypes.c_uint32),
                ("Data", ctypes.c_uint8 * 4128)]
class Emulator:
    def __init__(self, calibration=None):
        self.calibration = calibration
        self.iat_f = 68.0
        self.ect_f = 176.0
        self.master, slave = pty.openpty()
        self.path = os.ttyname(slave)
        self.slave = slave  # Keep slave open to avoid premature master EIO before client connects.
        self.buffer = bytearray()
        self.mode = "elm"
        self.stop = threading.Event()
        self.commands = []
        self.thread = threading.Thread(target=self.run, daemon=True)
    def closed_loop_eligible(self):
        if not self.calibration: return None
        table=self.calibration['closed_loop_temperature_table']
        iats=table['iat_f']; thresholds=table['ect_threshold_f']
        idx=min(range(len(iats)),key=lambda i:abs(iats[i]-self.iat_f))
        return self.ect_f > thresholds[idx]
    def start(self): self.thread.start()
    def packet(self, command, payload=b""):
        body = bytes([command, len(payload)]) + payload
        return body + bytes([~sum(body) & 255])
    def run(self):
        while not self.stop.is_set():
            ready, _, _ = select.select([self.master], [], [], .1)
            if not ready: continue
            try: self.buffer.extend(os.read(self.master, 4096))
            except OSError: break
            while self.buffer:
                if self.mode == "elm":
                    if self.buffer[0] == 0x25 and len(self.buffer) >= 3:
                        # Native DVI reset packet precedes ELM commands
                        del self.buffer[:3]
                        continue
                    if b"\r" not in self.buffer: break
                    command, _, remaining = self.buffer.partition(b"\r")
                    self.buffer[:] = remaining
                    if command == b"ATZ": os.write(self.master, b"ELM327\r>")
                    elif command == b"DXDP1":
                        os.write(self.master, b"OK\r>")
                        self.mode = "dvi"
                    else: os.write(self.master, b"ERROR\r>")
                    continue
                if len(self.buffer) < 3: break
                cmd, n = self.buffer[0], self.buffer[1]
                if len(self.buffer) < n + 3: break
                pkt = bytes(self.buffer[:n + 3]);del self.buffer[:n + 3]
                if sum(pkt) & 255 != 255: continue
                payload = pkt[2:-1]
                self.commands.append((cmd, payload))
                if cmd == 0x31: os.write(self.master, self.packet(0x41))
                elif cmd == 0x33: os.write(self.master, self.packet(0x43))
                elif cmd == 0x10 and len(payload) >= 5:
                    # Valid simulated VPW response to SAE 01 00 and four trim requests
                    if payload[3] == 1:
                        pid = payload[4]
                        replies = {
                            0x00: bytes([0x41, 0x00, 0x2F, 0x92, 0x00, 0x00]),
                            0x05: bytes([0x41, 0x05, 0x78]),
                            0x0C: bytes([0x41, 0x0C, 0x1F, 0x40]),
                            0x06: bytes([0x41, 0x06, 0x80]),
                            0x07: bytes([0x41, 0x07, 0x90]),
                            0x08: bytes([0x41, 0x08, 0x70]),
                            0x09: bytes([0x41, 0x09, 0x80])
                        }
                        if self.calibration:
                            replies[0x03]=bytes([0x41,0x03,0x02 if self.closed_loop_eligible() else 0x01]*1 + [0x02 if self.closed_loop_eligible() else 0x01])
                            replies[0x05]=bytes([0x41,0x05,max(0,min(255,round((self.ect_f-32)*5/9+40)))])
                            replies[0x0F]=bytes([0x41,0x0F,max(0,min(255,round((self.iat_f-32)*5/9+40)))])
                        if pid in replies:
                            os.write(self.master, self.packet(0x08, bytes([0x48,0x6B,0x10]) + replies[pid]))
                elif cmd == 0x25: self.mode = "elm"
    def close(self):
        self.stop.set()
        try: os.close(self.master)
        except OSError: pass
        try: os.close(self.slave)
        except OSError: pass
        self.thread.join(1)

def run():
    # The calibration image is a real 1 MiB P59 BIN. It is intentionally not
    # interpreted as executable PCM firmware or live sensor data.
    tune = Path(sys.argv[1]) if len(sys.argv) > 1 else None
    if tune:
        raw = tune.read_bytes()
        if len(raw) != 1048576:
            raise ValueError("Expected a 1 MiB P59 calibration BIN")
        print("P59 calibration fixture:", tune.name, "size:", len(raw),
              "SHA256:", hashlib.sha256(raw).hexdigest(), flush=True)
        info=inspect_calibration(tune)
        print("BIN-derived calibration flags:", {key: value["enabled"] for key,value in info["calibration_flags"].items()},flush=True)
        print("SAE PID support NOT derived from P59 diagnostic firmware. Emulator replies below remain simulated.",flush=True)
    e = Emulator(inspect_calibration(tune) if tune else None);e.start()
    lib = ctypes.CDLL(LIB)
    uint = ctypes.c_uint32
    lib.PassThruOpen.argtypes = [ctypes.c_char_p,ctypes.POINTER(uint)]
    lib.PassThruConnect.argtypes = [uint,uint,uint,uint,ctypes.POINTER(uint)]
    lib.PassThruWriteMsgs.argtypes = [uint,ctypes.POINTER(Message),ctypes.POINTER(uint),uint]
    lib.PassThruReadMsgs.argtypes = [uint,ctypes.POINTER(Message),ctypes.POINTER(uint),uint]
    dev=uint();chan=uint()
    try:
        result=lib.PassThruOpen(e.path.encode(),ctypes.byref(dev))
        print("Open:", result, "device:",dev.value,flush=True)
        if result: return 1
        result=lib.PassThruConnect(dev,1,0,10400,ctypes.byref(chan))
        print("Connect VPW:",result,"channel:",chan.value,flush=True)
        if result:return 2
        for pid in ((0,3,5,6,7,8,9,12,15) if e.calibration else (0,5,6,7,8,9,12)):
            if pid==3 and e.calibration:
                e.iat_f=68; e.ect_f=100
            elif pid==5 and e.calibration:
                e.ect_f=176
            elif pid==6 and e.calibration:
                e.ect_f=176
            msg=Message();msg.ProtocolID=1
            payload=bytes([0,0x68,0x6A,0xF0,1,pid])
            msg.DataSize=len(payload)
            for i,b in enumerate(payload):msg.Data[i]=b
            n=uint(1)
            w=lib.PassThruWriteMsgs(chan,ctypes.byref(msg),ctypes.byref(n),500)
            reply=Message();n=uint(1)
            r=lib.PassThruReadMsgs(chan,ctypes.byref(reply),ctypes.byref(n),1000)
            data=bytes(reply.Data[:reply.DataSize])
            print(f"PID {pid:02X}: write={w} read={r} count={n.value} rx={data.hex(' ')}",flush=True)
            if pid==3 and e.calibration and data[6:8]!=bytes([1,1]):
                print('FAIL: cold-case status must remain open-loop',flush=True);return 4
            if w or r or n.value != 1 or data[4:6] != bytes([0x41,pid]):
                return 3
        if e.calibration:
            # Exercise warm-case fuel-system status through the same J2534 driver.
            e.ect_f=176;msg=Message();msg.ProtocolID=1
            payload=bytes([0,0x68,0x6A,0xF0,1,3]);msg.DataSize=len(payload)
            for i,b in enumerate(payload):msg.Data[i]=b
            n=uint(1);w=lib.PassThruWriteMsgs(chan,ctypes.byref(msg),ctypes.byref(n),500)
            reply=Message();n=uint(1);r=lib.PassThruReadMsgs(chan,ctypes.byref(reply),ctypes.byref(n),1000)
            data=bytes(reply.Data[:reply.DataSize]);print('PID 03 warm:',data.hex(' '),flush=True)
            if w or r or data[4:8]!=bytes([0x41,3,2,2]):return 5
            print('PASS: calibration-threshold cold and warm scenario flags via J2534 (simulated engine state)',flush=True)
        print("PASS: simulated DVI handshake, J1850 VPW, SAE discovery and both fuel trim banks",flush=True)
        return 0
    finally:
        if chan.value:print("Disconnect:",lib.PassThruDisconnect(chan),flush=True)
        if dev.value:print("Close:",lib.PassThruClose(dev),flush=True)
        e.close()
if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--serve":
        emulator = Emulator()
        emulator.start()
        print(emulator.path, flush=True)
        try:
            while True: time.sleep(.5)
        finally:
            emulator.close()
    else:
        raise SystemExit(run())
