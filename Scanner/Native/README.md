# Linux OBDX Pro GT J2534 transport

Experimental native J1850 VPW transport used by the scanner adapter selector.

The suite scanner project builds and packages this driver automatically on Linux x64 (requires `gcc`). To build it separately:

```sh
gcc -D_DEFAULT_SOURCE -std=c11 -O2 -fPIC -shared -pthread -o libobdx_j2534.so obdx_j2534.c
cp libobdx_j2534.so /path/to/published/Scanner/
```

Run the transport emulator from this directory with `python3 test_gt_emulator.py`. Run the integrated scanner check with `python3 test_scanner.py /path/to/published/Scanner/WrongWayScanner`. It uses a pseudo terminal and simulated PCM responses. It does not execute the PCM firmware or establish hardware compatibility.

Select **J2534 (Linux VPW)** for SAE discovery or SAE-only recording. FuelTrims records STFT/LTFT for both banks; Basics also records coolant temperature and RPM. Serial OBDX DVI remains the default for GM enhanced channels and the A/C-pressure wideband input.

CLI examples:

```sh
./WrongWayScanner --discover-j2534 /dev/ttyUSB0
./WrongWayScanner --record-j2534 /dev/ttyUSB0 FuelTrims /tmp/trims.csv 10
```

The driver supports serial opening, DVI entry, VPW transmit/receive, device/channel management and version/error queries. Filters, periodic messages, full IOCTL coverage, other protocols, programming voltage and flashing are unsupported. Real GT and PCM streaming still require verification.

The installed USB executable passed scanner self-tests including 36 raw-byte PID decoder cases. The standard PID definitions include corrected O2 millivolt, oil-pressure, vehicle-speed and fuel-system-status conversions. The A/C input AFR formula remains `([7101.10]/0.5)+9.57`.
