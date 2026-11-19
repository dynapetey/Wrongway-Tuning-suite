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
def session(mode, *args):
    fd, slave = pty.openpty()
    p = subprocess.Popen([APP, mode, os.ttyname(slave), *args], text=True,
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

def adapter(fd, corrupt=False, adc_channel=0):
    commands = [
        (0x22, b'\x03', b'\x03OBDX Pro GT'),
        (0x22, b'\x01', b'\x01\x01\x06\x02'),
        (0x22, b'\x00', b'\x00\x02\x01'),
        (0x22, b'\x04', bytes.fromhex('04 12 A0 0B 44')),
        (0x3A, b'\x00\x00', bytes([0, adc_channel]) + bytes.fromhex('05 80')),
    ]
    for cmd, request, reply_payload in commands:
        expect(fd, frame(cmd, request))
        packet = frame(cmd + 0x10, reply_payload)
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

def controls(fd, os_id=12587811, rpm=None, speed=0):
    elm(fd)
    adapter(fd)
    enable_vpw(fd)
    want_pcm(fd, 10)
    reply(fd, 10, os_id.to_bytes(4, 'big'))
    if rpm is not None:
        expect(fd, frame(0x10, bytes.fromhex('6C 10 F0 22 00 0C 01')))
        # Wrong PID and a truncated reply must not satisfy the stopped-engine check.
        os.write(fd, frame(8, bytes.fromhex('6C F0 10 62 00 0D') + bytes([speed])))
        os.write(fd, frame(8, bytes.fromhex('6C F0 10 62 00')))
        os.write(fd, frame(8, bytes.fromhex('6C F0 10 62 00 0C') + (rpm * 4).to_bytes(2, 'big')))
        expect(fd, frame(0x10, bytes.fromhex('6C 10 F0 22 00 0D 01')))
        os.write(fd, frame(8, bytes.fromhex('6C F0 10 62 00 0D') + bytes([speed])))

release_fans = frame(0x10, bytes.fromhex('6C 10 F0 AE 01 00 00 00 00 00 00'))
accept_fans = frame(8, bytes.fromhex('6C F0 10 EE 01 E1'))
for action, payload in [('Fan1On', '80 80'), ('Fan1Off', '80 00'), ('Fan2On', '40 40'), ('Fan2Off', '40 00')]:
    with session('--vcm-control-live', action, '--confirm') as (fd, p):
        controls(fd, rpm=0)
        expect(fd, frame(0x10, bytes.fromhex(f'6C 10 F0 AE 01 00 00 {payload} 00 00')))
        os.write(fd, frame(8, bytes.fromhex('6C F0 11 EE 01 E1')) + accept_fans)
        expect(fd, release_fans)
        os.write(fd, accept_fans)
        expect(fd, frame(0x25))
        out, err = p.communicate(timeout=5)
        assert p.returncode == 0 and json.loads(out)['ReleaseReply'] == '6CF010EE01E1', (out, err)
print('PASS four P59 fan relay commands, PCM reply validation and automatic release.')

for os_id, rpm, expected in [(1252075, None, 'unavailable'), (12587811, 650, 'switch the engine off')]:
    with session('--vcm-control-live', 'Fan1On', '--confirm') as (fd, p):
        controls(fd, os_id, rpm)
        expect(fd, frame(0x25))
        out, err = p.communicate(timeout=5)
        assert p.returncode != 0 and expected in err, (out, err)
print('PASS fan overrides are blocked for an unverified OS or a running engine.')

with session('--vcm-control-live', 'Fan1On', '--confirm') as (fd, p):
    controls(fd, rpm=0, speed=1)
    expect(fd, frame(0x25))
    out, err = p.communicate(timeout=5)
    assert p.returncode != 0 and 'Park the vehicle' in err, (out, err)
print('PASS a moving vehicle cannot receive a fan override even with zero RPM.')

for cancel in [False, True]:
    with session('--vcm-control-live', 'Fan1On', '--confirm') as (fd, p):
        controls(fd, rpm=0)
        expect(fd, frame(0x10, bytes.fromhex('6C 10 F0 AE 01 00 00 80 80 00 00')))
        if cancel:
            os.write(fd, accept_fans)
            os.kill(p.pid, signal.SIGINT)
        else:
            os.write(fd, frame(8, bytes.fromhex('6C F0 10 7F AE 22')))
        expect(fd, release_fans)
        os.write(fd, accept_fans)
        expect(fd, frame(0x25))
        out, err = p.communicate(timeout=5)
        assert p.returncode != 0 and ('canceled' if cancel else 'status 0x22') in err.lower(), (out, err)
print('PASS rejected or stopped fan commands release the relays.')

with session('--vcm-control-live', 'ClearTroubleCodes', '--confirm') as (fd, p):
    controls(fd, rpm=0)
    expect(fd, frame(0x33, bytes.fromhex('00 F0 00')))
    os.write(fd, frame(0x43, bytes.fromhex('00 F0 00')))
    expect(fd, frame(0x10, bytes.fromhex('68 6A F0 04')))
    os.write(fd, frame(8, bytes.fromhex('48 6B 10 44')))
    expect(fd, frame(0x33, bytes.fromhex('00 F0 01')))
    os.write(fd, frame(0x43, bytes.fromhex('00 F0 01')))
    expect(fd, frame(0x25))
    out, err = p.communicate(timeout=5)
    assert p.returncode == 0 and json.loads(out)['Reply'] == '486B1044', (out, err)
print('PASS clearing DTCs waits for the PCM Mode $44 acknowledgement.')

with session('--vcm-control-live', 'ClearTroubleCodes', '--confirm') as (fd, p):
    controls(fd, rpm=0)
    expect(fd, frame(0x33, bytes.fromhex('00 F0 00')))
    os.write(fd, frame(0x43, bytes.fromhex('00 F0 00')))
    expect(fd, frame(0x10, bytes.fromhex('68 6A F0 04')))
    os.write(fd, frame(8, bytes.fromhex('6C F0 10 7F 04 22')))
    expect(fd, frame(0x33, bytes.fromhex('00 F0 01')))
    os.write(fd, frame(0x43, bytes.fromhex('00 F0 01')))
    expect(fd, frame(0x25))
    out, err = p.communicate(timeout=5)
    assert p.returncode != 0 and 'status 0x22' in err, (out, err)
print('PASS a rejected DTC clear still restores the destination filter.')

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

with session('--probe-gt') as (fd, p):
    elm(fd)
    adapter(fd, adc_channel=1)
    expect(fd, frame(0x25))
    out, err = p.communicate(timeout=5)
    assert p.returncode == 1 and 'ADC channel 0' in err, (out, err)
print('PASS a voltage response from the wrong ADC channel is rejected.')

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

def vpw_crc(data):
    crc = 0xff
    for value in data:
        crc ^= value
        for _ in range(8):
            crc = ((crc << 1) ^ (0x1d if crc & 0x80 else 0)) & 255
    return bytes([crc ^ 255])

def sae_page(fd, page, pids, source=0x10, add_crc=False):
    mask = sum(1 << (32 - (pid - page)) for pid in pids)
    data = bytes([0x48, 0x6b, source, 0x41, page]) + mask.to_bytes(4, 'big')
    os.write(fd, frame(8, data + (vpw_crc(data) if add_crc else b'')))

catalog = json.loads(subprocess.check_output([APP, '--catalog', '12587811'], text=True))
gm_candidates = sorted({channel['Pid'] for channel in catalog['Channels']
                        if channel['Pid'] is not None and 0xff < channel['Pid'] <= 0xffff})
assert len(gm_candidates) >= 3
assert 0x1141 in gm_candidates
with session('--discover-gt') as (fd, p):
    elm(fd)
    adapter(fd)
    enable_vpw(fd)
    expect(fd, frame(0x10, b'\x68\x6a\xf0\x01\x00'))
    sae_page(fd, 0, [1], source=0x18)
    sae_page(fd, 0x20, [0x23])
    sae_page(fd, 0, [0x0c, 0x11, 0x20])
    expect(fd, frame(0x10, b'\x68\x6a\xf0\x01\x20'))
    sae_page(fd, 0x20, [0x22], add_crc=True)
    want_pcm(fd, 10)
    reply(fd, 10, (12587811).to_bytes(4, 'big'))

    for index, pid in enumerate(gm_candidates):
        pid_bytes = pid.to_bytes(2, 'big')
        expect(fd, frame(0x10, b'\x6c\x10\xf0\x22' + pid_bytes + b'\x01'))
        if pid == 0x1141:
            os.write(fd, frame(8, b'\x6c\xf0\x10\x7f\x22\x78'))
            os.write(fd, frame(9, b'\x6c\xf0\x10\x62' + pid_bytes + b'\x79'))
        elif index == 0:
            os.write(fd, frame(8, b'\x6c\xf0\x18\x62' + pid_bytes + b'\x01'))
            os.write(fd, frame(8, b'\x6c\xf0\x10\x7f\x22\x31'))
        elif index != 1:
            os.write(fd, frame(8, b'\x6c\xf0\x10\x7f\x22\x31'))
    expect(fd, frame(0x25))
    out, err = p.communicate(timeout=5)
    assert p.returncode == 0, (out, err)
    discovery = json.loads(out)
    assert discovery == {'OsId': 12587811, 'SaePids': [0x0c, 0x11, 0x22],
                         'SaeComplete': True, 'GmPids': [0x1141],
                         'GmProbed': len(gm_candidates), 'Warning': None, 'Count': 4}, discovery
print('PASS ECM SAE pages and GM discovery, other modules ignored, timeout and negative reply excluded.')

with session('--discover-gt') as (fd, p):
    elm(fd)
    adapter(fd)
    enable_vpw(fd)
    expect(fd, frame(0x10, b'\x68\x6a\xf0\x01\x00'))
    sae_page(fd, 0, [0x0c, 0x20])
    expect(fd, frame(0x10, b'\x68\x6a\xf0\x01\x20'))
    want_pcm(fd, 10)
    os.write(fd, frame(8, b'\x6c\xf0\x10\x7f\x3c\x22'))
    expect(fd, frame(0x25))
    out, err = p.communicate(timeout=5)
    assert p.returncode == 0, (out, err)
    discovery = json.loads(out)
    assert discovery['SaePids'] == [0x0c] and not discovery['SaeComplete'], discovery
    assert discovery['OsId'] is None and discovery['GmProbed'] == 0, discovery
    assert 'page 0x20' in discovery['Warning'], discovery
print('PASS incomplete SAE pages report partial support and an unidentified OS skips enhanced probing.')
