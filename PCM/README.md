# WrongWay Tuner Linux 0.2.0

Independent desktop tuner for GM P01/P59 raw BIN files. Custom parameters are
available by default. No account, activation, credits or per-vehicle licensing.
This is a native Linux desktop and serial backend; Wine is not required.

## Install and open

Extract the full archive to a permanent folder. On Ubuntu 22.04/24.04 or Mint:

```sh
bash install.sh
bash launch.sh
```

The installer installs missing Python Tk/Mono development packages using your distribution's
package manager, then creates a local application-menu shortcut. It does not
move your files. Leave the folder in its final location before installing.
Dependencies: Python 3.10+, Tk 8.6 and Mono 6.8+. No pip packages are required.
The supplied backend was built and tested on Ubuntu x86-64. Its POSIX serial
layout targets Linux x86-64; other architectures have not been tested.

Linux receives large OBDX DVI packets through a managed buffer so a 4096-byte
PCM read payload cannot overflow the smaller TTY input queue. The serial
loopback regression check includes a 16 KiB queued frame.
CRC replies and erase replies also retain their longer receive windows;
the backend waits for a pending CRC response before retransmitting a query.

For an OS missing from the bundled lookup, hardware ID `12589162` enables the
P59 read protocol. An OS-and-calibration write additionally requires a supported P59 target.
An original backup is optional; when supplied, it must validate and match the
connected PCM's current OS and serial number.
The same hardware and serial checks allow read-only CRC comparison of an
unknown-OS backup. Any mismatched CRC makes that comparison fail.
This path retains flash-chip size and CRC checks and preserves parameter
blocks. Other unknown hardware remains blocked.

If a USB serial port reports access denied:

```sh
sudo usermod -aG dialout "$USER"
```

Sign out and in afterward. For another distribution install Python, Tk and Mono
through its package manager, then run `bash launch.sh`.

## Editor

- Scalars, single-bit switches, 2D tables; unsigned/signed 8/16/32-bit integers,
  IEEE 32/64-bit floats, either byte order, linear engineering conversions and
  optional limits. Custom definitions may use explicit byte strides.
- Load/save the Windows version's JSON definitions. Add, edit, remove and search
  definitions. Match the definition to the actual BIN OS before editing.
- Supported TunerPro XDF constants, flags and contiguous tables; static axis
  labels, base offsets and invertible linear equations. Import notices identify
  omitted axis labels and unsupported entries.
- Single-cell edits, set/add/multiply/percent range edits, smoothing, clipboard
  tables and CSV/TSV import/export. Batches validate fully before changing bytes.
- Undo/redo, changed-row highlighting and exact byte comparison.
- Atomic BIN/definition/log saves. Overwritten files receive timestamped backups.
- P01/P59 signature/layout checks, segment checksums and undoable checksum repair.
  Repair handles calibration segment checksums before the overall OS checksum.
  Unrecognized or unusual segment layouts are rejected, not guessed.

No verified OS-specific tune XDFs are bundled. Bring the definition that matches
your actual operating system. A filename is not proof of compatibility.
The 128-byte demo BIN is synthetic and cannot pass the flasher's file validator.

## PCM workflow

Select the OBDX Pro GT's USB serial port, normally `/dev/ttyUSB0` or
`/dev/ttyACM0`, and choose OBDX. The backend uses PCM Hammer's native DVI/VPW
device code, without Windows J2534 drivers. ELM/AVT implementations are also
exposed but have not been tested here. An already-bound Bluetooth Classic
`/dev/rfcommN` device may work; USB is the intended first hardware test.

Available operations:

| Operation | Behavior |
| --- | --- |
| Read properties | VIN, OS, calibration, hardware, serial and adapter voltage |
| Read full backup | Upstream read manager with its CRC checks; new output filename |
| Validate BIN | Upstream PCM Hammer layout and checksum checks, offline |
| Compare with PCM | Upstream CRC comparison without erasing flash |
| Test write | Upstream test-write manager without erasing flash |
| Calibration only | Calibration segments; requires matching OS |
| Parameters only | Upstream parameter write mode |
| OS + calibration + boot | Upstream combined mode; includes boot content |
| Full clone | Full upstream write mode, including vehicle-specific content |
| P59 recovery | Full write with P59 selected; manager retains compatibility checks |
| Trouble codes | Standard emissions mode-03 request, code labels such as P0300 |
| Clear codes | Upstream clear request; read again to confirm |

Actual writes require valid target checksums, an explicit review and
confirmation, and any additional upstream risk prompts. Original backups are
optional. A supplied backup must be a separate valid file from the same
controller family; calibration/parameter files and supplied backups must have
matching OS IDs. The library
performs further connected-PCM checks. The desktop blocks closing/disconnecting
during a flash. It does not automatically approve upstream warning prompts.

Use ignition ON, engine OFF and stable battery support for read/write work.
Save an original backup before making changes. A recovery workflow cannot
guarantee restoration of every damaged controller.

## Scanner and logs

The default WrongWay profile includes RPM, MAP, MAF, throttle voltage, IAT,
spark timing, knock retard and A/C pressure sensor voltage. Profiles use PCM
Logger's XML format, including custom supported PID/RAM definitions. The
bundled database supports known standard parameters and some OS-specific RAM
parameters; it does not discover every RAM location automatically.

Live samples are flushed directly to CSV. If A/C **voltage** is present, the
native backend appends its value under `7101.10` and calculates AFR using:

```text
(([7101.10]/.5)+9.57)
```

This assumes the wideband signal is physically connected/scaled as your existing
setup requires. An A/C pressure reading in kPa is not the voltage input.
The included upstream `ACWidebandAFRatio` definition uses its own 9.58 offset;
the WrongWay profile intentionally selects raw sensor voltage and adds your
9.57 conversion independently.

Open CSV, add arithmetic channels using `[column names]`, plot channels and
export RPM/MAP-style histograms with sample counts, mean, minimum and maximum.
Missing numeric input and divide-by-zero rows produce blank calculated values.
The live display retains 150 rows; the full log remains on disk. Session/debug
messages retain their most recent 5,000 lines and can be exported.

## Compatibility and validation limits

This is a P01/P59 release, not feature parity with all of HP Tuners.
`.HPT` files, MPVI interfaces, Windows J2534 DLLs, CAN controllers, automatic
BLE pairing, real-time RAM tuning, segment swaps and complete OEM service
functions are not implemented. Advanced XDF linked/nonlinear/per-cell equations,
patch/function definitions and XDF stride layouts are reported as unsupported.

P59 identification, a full 1 MiB backup, simulated write, and an OS/calibration/
boot replacement from OS `1252075` to `12587811` were validated with an OBDX
Pro GT on Linux on 2026-11-19. The final comparison matched all 19 flash ranges,
including both preserved parameter blocks. See [hardware results](hardware-validation-2026-11-19.md).
Recovery, other write modes, continuous live logging, DTC handling and other
adapters are not established by that test. Review `validation.txt` for the
earlier offline checks.

## Source, tests and rebuilding

The Python editor's source is `wrongway/`, with offline tests in `tests/`:

```sh
bash test.sh
```

The native backend's GPL source is `backend/source/LinuxBackend.cs`. To rebuild:

```sh
sudo apt-get install mono-devel
bash backend/build.sh
```

Rebuilding changes its checksum. Regenerate `backend/SHA256.json` with
`python3 make_manifest.py` afterward. A separate archive of the unmodified
managed PCM Hammer source, corresponding P01 kernel source, GPL text and
third-party notices is provided under `ThirdParty/`.
The Python editor is MIT licensed; the separate native backend/library is GPLv3.

Upstream release: https://github.com/PcmHammer/PcmHammer/releases/tag/2.0.0
