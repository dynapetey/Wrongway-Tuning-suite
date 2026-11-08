from contextlib import contextmanager
from pathlib import Path
import json
import os
import pty
import select
import signal
import subprocess
import sys
import time

APP = Path(sys.argv[1]).resolve()
VIN = b'1GCEC14T02Z123456'
assert len(VIN) == 17

def frame(command, payload=b''):
    length = len(payload).to_bytes(2 if command == 9 else 1, 'big')
    body = bytes([command]) + length + payload
    return body + bytes([~sum(body) & 255])

def receive(fd, length):
    data = bytearray()
    deadline = time.monotonic() + 6
    while len(data) < length:
        remaining = deadline - time.monotonic()
        assert remaining > 0 and select.select([fd], [], [], remaining)[0], (length, data)
        data.extend(os.read(fd, length - len(data)))
    return bytes(data)

def expect(fd, value):
    got = receive(fd, len(value))
    assert got == value, (got.hex(), value.hex())

@contextmanager
def session(mode):
    fd, slave = pty.openpty()
    p = subprocess.Popen([APP, mode, os.ttyname(slave)], text=True,
                         stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    try:
        yield fd, p
    finally:
        if p.poll() is None:
            p.kill()
            p.wait()
        os.close(fd)
        os.close(slave)

def elm(fd, no_prompt=False):
    expect(fd, frame(0x25))
    expect(fd, b'ATZ\r')
    os.write(fd, b'ATZ\rOBDX Pro GT\r>')
    expect(fd, b'AT@1\r')
    os.write(fd, b'AT@1\rOBDX Pro GT\r' + (b'' if no_prompt else b'>'))
    if no_prompt:
        return
    expect(fd, b'DXDP1\r')
    os.write(fd, b'DXDP1\rOK\r>')

def adapter(fd, corrupt=False):
    commands = [
        (0x22, 3, b'OBDX Pro GT'),
        (0x22, 1, b'\x01\x06\x02'),
        (0x22, 0, b'\x02\x01'),
        (0x22, 4, bytes.fromhex('12 A0 0B 44')),
        (0x3A, 0, bytes.fromhex('05 80')),
    ]
    for cmd, sub, value in commands:
        expect(fd, frame(cmd, bytes([sub])))
        packet = frame(cmd + 0x10, bytes([sub]) + value)
        if corrupt:
            os.write(fd, packet[:-1] + bytes([packet[-1] ^ 1]))
            return
        os.write(fd, packet[:2])
        os.write(fd, packet[2:])

def enable_vpw(fd):
    for command, payload in [(0x31, b'\x01\x01'), (0x33, b'\x00\xf0\x01'), (0x31, b'\x02\x01')]:
        expect(fd, frame(command, payload))
        os.write(fd, frame(command + 0x10))

def want_pcm(fd, block):
    expect(fd, frame(0x10, bytes([0x6C, 0x10, 0xF0, 0x3C, block])))

def reply(fd, block, value, command=8):
    os.write(fd, frame(command, bytes([0x6C, 0xF0, 0x10, 0x7C, block]) + value))

with session('--identify-gt') as (fd, p):
    elm(fd)
    adapter(fd)
    enable_vpw(fd)
    want_pcm(fd, 10)
    os.write(fd, frame(8, b'\x6c\xf0\x11\x7c\x0a\xff\xff\xff\xff'))
    reply(fd, 10, (12587811).to_bytes(4, 'big'), command=9)
    for block, part in enumerate([b'\x00' + VIN[:5], VIN[5:11], VIN[11:]], 1):
        want_pcm(fd, block)
        reply(fd, block, part)
    expect(fd, frame(0x25))
    out, err = p.communicate(timeout=5)
    assert p.returncode == 0, (out, err)
    info = json.loads(out)
    assert info['Pcm'] == {'OsId': 12587811, 'Vin': VIN.decode()}
    assert info['Adapter']['Name'] == 'OBDX Pro GT'
    assert info['Adapter']['Firmware'] == '1.6.2' and info['Adapter']['Hardware'] == '2.1'
    assert info['Adapter']['Serial'] == '12A00B44'
    assert abs(info['Adapter']['Voltage'] - (1408 * 0.009047468 + .2)) < 1e-9
print('PASS GT fields, checksum/framing, unrelated VPW frame ignored, PCM OS and VIN, automatic reset.')

for label, vin, blocks, vin_error in [
    # The first two VPW CRC bytes look like VIN characters (ASCII 7 and P).
    ('VPW CRC', '1GCE114T02H123456', [b'\x001GCE17', b'14T02HP', b'123456F'], None),
    ('corrupt VPW CRC', None, [b'\x001GCE16'], 'VPW CRC'),
    ('short VIN block', None, [b'\x00' + VIN[:5], VIN[5:10]], 'block 02'),
    ('invalid VIN character', None, [b'\x00' + VIN[:5], VIN[5:11], b'I' + VIN[12:]], 'invalid VIN'),
]:
    with session('--identify-gt') as (fd, p):
        elm(fd)
        adapter(fd)
        enable_vpw(fd)
        want_pcm(fd, 10)
        reply(fd, 10, (12587811).to_bytes(4, 'big'))
        for block, value in enumerate(blocks, 1):
            want_pcm(fd, block)
            reply(fd, block, value, command=9 if block == 2 else 8)
        expect(fd, frame(0x25))
        out, err = p.communicate(timeout=5)
        info = json.loads(out)
        assert info['Pcm'] == {'OsId': 12587811, 'Vin': vin}, (label, out, err)
        if vin_error:
            assert p.returncode == 3 and vin_error in info['PcmError'], (label, out, err)
        else:
            assert p.returncode == 0 and info['PcmError'] is None, (label, out, err)
    print(f'PASS VIN identification with {label}.')

with session('--probe-gt') as (fd, p):
    elm(fd)
    adapter(fd)
    expect(fd, frame(0x25))
    out, err = p.communicate(timeout=5)
    assert p.returncode == 0 and json.loads(out)['Pcm'] is None, (out, err)
print('PASS adapter-only path and serial release.')

with session('--identify-gt') as (fd, p):
    elm(fd)
    adapter(fd)
    enable_vpw(fd)
    want_pcm(fd, 10)
    os.write(fd, frame(8, b'\x6C\xF0\x10\x7F\x3C\x22'))
    expect(fd, frame(0x25))
    out, err = p.communicate(timeout=5)
    assert p.returncode == 3 and 'status 22' in json.loads(out)['PcmError'], (out, err)
print('PASS PCM rejection preserves adapter result and resets the GT.')

with session('--probe-gt') as (fd, p):
    elm(fd, no_prompt=True)
    out, err = p.communicate(timeout=5)
    assert p.returncode == 1 and 'AT@1' in err and 'ELM prompt' in err, (out, err)
print('PASS incomplete ELM prompt reported explicitly.')

with session('--probe-gt') as (fd, p):
    elm(fd)
    adapter(fd, corrupt=True)
    expect(fd, frame(0x25))
    out, err = p.communicate(timeout=5)
    assert p.returncode == 1 and 'bad checksum' in err, (out, err)
print('PASS corrupt DVI packet rejected and adapter reset.')

with session('--identify-gt') as (fd, p):
    elm(fd)
    adapter(fd)
    enable_vpw(fd)
    want_pcm(fd, 10)
    os.kill(p.pid, signal.SIGINT)
    expect(fd, frame(0x25))
    out, err = p.communicate(timeout=5)
    assert p.returncode != 0 and 'canceled' in err.lower(), (out, err)
print('PASS cancellation stops the transaction and resets the GT.')

with session('--identify-gt') as (fd, p):
    elm(fd)
    adapter(fd)
    enable_vpw(fd)
    want_pcm(fd, 10)
    want_pcm(fd, 10)  # First request deliberately unanswered.
    reply(fd, 10, (12587811).to_bytes(4, 'big'))
    for block, part in enumerate([VIN[:5], VIN[5:11], VIN[11:]], 1):
        want_pcm(fd, block)
        reply(fd, block, part)
    expect(fd, frame(0x25))
    out, err = p.communicate(timeout=5)
    assert p.returncode == 0 and 'Retrying 2/3' in err, (out, err)
print('PASS bounded retry after an unanswered PCM request.')
