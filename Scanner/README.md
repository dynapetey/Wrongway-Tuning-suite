# WrongWay Scanner + Hammerdroid 0.4.17

Standalone x86-64 Linux desktop scanner and P59 calibration editor. Extract this archive and run the
`WrongWayScanner` executable with the `Assets` folder beside it. The .NET
runtime is bundled. The app uses the Linux X11 desktop, including XWayland
on Wayland sessions.

```sh
./WrongWayScanner
./WrongWayScanner "/path/to/your-log.hpl"
```

## Open and replay a log

- **Open log** opens compact SS/SYNC HPL logs, including the VCM Scanner
  5.2.3 logs used for verification. Older HPT-wrapped HPL files use a different
  layout and are rejected. A truncated final block produces a warning while
  earlier intact frames remain available.
- Select up to six numeric channels. Each trace has its own vertical scale.
  Use the slider, click on the chart, or press **Play**. Scroll over the chart
  to zoom and use **Full view** to reset the time range. The side panel shows
  the last recorded value at or before the cursor.
- **Edit gauges** adds, removes and reorders dial, bar, and digital gauges.
  Choose each instrument's channel, label, range and color.
- **Edit graph** sets the histogram's table label, X, Y and value channels,
  fixed bin widths or explicit breakpoints, mean/minimum/maximum/count,
  minimum samples, and optional VCM `FilterFunction` cell filter expression.
  Multiple imported or saved histogram tables appear together. Use the
  table selector to choose the table to edit or export. The histogram updates during
  recording and displays how many samples were excluded by the active cell filter.
- **Layout → Import VCM XML…** imports HP Tuners VCM Scanner `*.Channels.xml`,
  `*.Graphs.xml`, `*.Layout.xml`, and `*.MathParameter.xml` files directly into
  the active WrongWay Scanner layout and live recording profile.
- **Edit traces** sets the time chart order, colors and per-trace scale.
  **Channels → Edit** controls labels, units, linear conversions, precision
  and visibility. These settings change the displayed values. CSV exports
  retain the recorded channel names, units and values.
- Changes are saved automatically to `~/.config/wrongway-suite/scanner-layout.json`
  (or under `$XDG_CONFIG_HOME`). **Layout** can export, load or reset the
  settings. Drag the panel dividers to resize the workspace.
- **Tools → Maths** adds and edits saved calculated channels. Use them in
  gauges, the time chart, and the histogram. The Wideband AFR shortcut
  also lets you apply a slope and offset to a selected voltage channel.
- **Export CSV** writes the recorded channel updates, preserving sparse fields.
  Choose a new filename. The app can reopen this CSV and other comma-delimited
  logs with a time column in seconds or milliseconds.

## Scanner Maths & VCM Telemetry Functions

Choose **Tools → Maths → Add**, select a VCM Telemetry preset, or click
**Import .MathParameter.xml…**. Enter a channel name, output units and a formula.

| Reference | Input |
| --- | --- |
| `[12]` or `[0x000C]` | Engine speed in a log carrying the PID |
| `[Engine Speed|RPM]` | A uniquely named input in the specified recorded unit |
| `[7101.10]` | A/C pressure sensor input in volts |
| `[50090.156.slope(250)]` | Rate of change of Throttle Position (%) over a 250 ms window |
| `[50118.238.avg(250)]` | Rolling mean of Commanded Equivalence Ratio over 250 ms |
| `[2517.161.avg(-200)]` | Lookahead mean of DFCO state over the next 200 ms |
| `[13.114.derivative()]` | Instantaneous rate of change (`dx/dt`) of Vehicle Speed |
| `[13.114.integral()]` | Cumulative trapezoidal time integral (`∫ x dt`) |
| `[12.56.delta()]` / `[12.56.prev()]` | Sample-to-sample difference or previous sample |

Supported VCM Telemetry channel methods:
- `.avg(ms)` / `.mean(ms)`, `.min(ms)`, `.max(ms)`, `.sum(ms)`, `.count(ms)`
- `.slope(ms)` / `.rate(ms)` (units per second over window; negative `ms` looks ahead)
- `.derivative()` / `.deriv()` / `.roc()`, `.integral()` / `.integ()`
- `.delta(ms)` / `.diff(ms)`, `.lag(ms)` / `.delay(ms)` / `.prev()`

Supported operators and functions:
- Arithmetic (`+`, `-`, `*`, `/`), comparisons (`=`, `==`, `!=`, `<>`, `<`, `<=`, `>`, `>=`), and boolean logic (`and`, `or`, `not`, `&&`, `||`)
- Built-in functions: `abs`, `sqrt`, `min`, `max`, `pow`, `log`, `log10`, `exp`, `round`, `clamp(val, lo, hi)`, `iff(cond, t, f)`, and `if(cond, t, f)`
- Built-in VCM Telemetry presets: `Wideband AFR`, `CL Filter`, `PE Filter`, `MAF CL Error`, `MAF PE Error`, `PE FOR AFR Error`, `VVE FOR CL Error`, `VVE FOR PE Error`, `VVE FOR AFR Error`, `Combined Fuel Trim`, `Boost Pressure (psi)`, `Injector Duty Cycle`, `Dynamic Air Mass Per Sec`, `Horsepower (Estimated)`, and `Torque (Estimated)`.

The eight original telemetry math XML files are bundled under
`Assets/VCM/Maths/`. Import an individual file in **Tools → Maths**, or import
several together with **Layout → Import VCM XML**.

Formula settings are saved with the layout. Missing or ambiguous sources
produce blank readings. Lookahead methods use future samples already in a log.

## VCM Controls & Special Functions

Select the OBDX Pro GT port, then open **Tools → VCM Controls & Special Functions**.
The live fan relay tests support P59 OS 12587603 and 12587811. With the ignition
on and engine off, select Fan 1 or Fan 2 to command that relay on or off for two
seconds. The scanner checks RPM and vehicle speed, reports the PCM response,
and returns the relays to automatic control when the test ends or is stopped.
**Return fans to PCM** sends that release separately. **Clear DTCs** sends
SAE Mode $04 and verifies the PCM acknowledgement.

Version 0.4.12 reads stopped-engine status through addressed GM PIDs so the
GT's destination filter cannot discard the RPM/speed replies. Clearing DTCs
temporarily allows functional replies, restores the filter on success or
failure, and reports PCM rejections. The panel scrolls at smaller window sizes
and records connection, command and release progress in its own log.
A single connected OBDX Pro GT is selected automatically using its stable
Linux device path. Multiple GT adapters still require a port selection.

On 2026-11-19, the connected OS 12587811 PCM accepted Fan 1 on/off, Fan 2 on/off
and release requests, confirming automatic release after each two-second test.
Those replies confirm request acceptance; relay wiring and physical fan
movement require observation at the vehicle. DTC clearing was exercised with
emulated positive and negative replies; existing vehicle codes were preserved.

CASE learn, fuel trim reset, loop, idle, AFR, spark and cylinder
balance frames are available for offline inspection.

```sh
./WrongWayScanner --vcm-control-preview CrankRelearn
./WrongWayScanner --vcm-control-live /dev/ttyACM0 Fan1On --confirm
```

The fan relay byte layout follows the [P59 12587603 firmware disassembly](https://github.com/LegacyNsfw/12587603)
and the corresponding handler in the 12587811 image. A PCM acknowledgement
confirms it accepted a request. Observe the relay or fan to verify movement.

## Telemetry Hub

Open **Tools → Telemetry Hub** to reopen indexed HPL and CSV logs, see frame
counts and duration, or export a JSON bundle from the current log. The index
and export store peak readings and VIN when the log contains those channels. Sessions are
stored under `$XDG_CONFIG_HOME/wrongway-suite` or `~/.config/wrongway-suite`.
Recorded OS IDs are stored when identified during live capture.

```sh
./WrongWayScanner --telemetry-sessions
./WrongWayScanner --telemetry-export input.hpl bundle.json
./WrongWayScanner --import-vcm-xml input.Graphs.xml layout.json
python3 readout-bridge.py serve-http ./WrongWayScanner --port 8765
```

Open `http://127.0.0.1:8765` for channel readout and recent sessions. The
dashboard also exposes `/api/status`, `/api/catalog`, and `/api/sessions`.
The QEMU guest bridge handles the `sessions` command alongside `status`
and `catalog`.

## Connect an OBDX Pro GT

Choose the GT's Linux serial port with **Find ports** or enter it manually.
USB devices normally appear under `/dev/ttyUSB*` or `/dev/ttyACM*`.
An existing Bluetooth Classic serial binding appears under `/dev/rfcomm*`.
The app uses the serial path that Linux provides. For Bluetooth, pair the GT
with your Linux Bluetooth settings first, then bind the serial channel with
BlueZ's `rfcomm` utility if your distribution supports it.

```sh
sudo rfcomm bind rfcomm0 ADAPTER_MAC 1
```

Use **GT / PCM → Read adapter details** to read the GT model, firmware,
hardware revision, serial number, and OBD port voltage. Choose
**Identify PCM and adapter** to also query the VPW PCM's VIN and OS ID.
If the PCM does not answer, the adapter details remain visible. The app
returns the GT to ELM mode and releases the port after the probe. **Stop**
cancels an active operation.

Use **Scan → Read ECM PIDs** with the ignition on to read the ECM's SAE
support bitmaps and check the bundled GM enhanced PID definitions for the
identified OS. After the scan, **ECM-supported** shows the reported or
responding PIDs. The catalog remains available when you clear the filter.
Reported PIDs without decoding definitions appear as **Unmapped ECM PID**.
A scan checks known enhanced PIDs and can take about a minute. **Stop**
cancels the scan and releases the adapter. Discovery does not record data
or modify the ECM.

Select **WrongWayP59**, **Basics**, **FuelTrims**, or **Custom** and click
**Record**. Use **Edit live channels** to build a custom profile. Pick PIDs
and RAM channels from the catalog, set their recording units and labels,
and change their order. The profile must fit the PCM's 18-byte limit.
The **Channels** pane automatically opens with the PCM Logger definitions
for P59 OS 12587811. Search by name, decimal PID, hex PID such as `0x000C`,
or RAM address where one is defined. Click **+** to add an entry to an
editable copy of the selected profile. **Recorded only** shows the channels
from your current recording or log. Identification and recording use the
OS returned by the PCM. Availability also depends on the vehicle and its
calibration. Choose a new recording file and the app saves each complete
frame as it arrives.
**Read DTCs** requests stored codes. The live scanner targets GM P01/P59
J1850 VPW. The hardware controls share one operation at a time.

The GT and PCM identification paths passed a simulated serial test. USB
and RFCOMM hardware, real PCM logging, and stored-code reads still need a
vehicle test. 

## Read the scanner from tools

```sh
./WrongWayScanner --catalog 12587811
./WrongWayScanner --scanner-status
```

`--catalog` returns the bundled definitions and available units. Omit the OS
to use 12587811. `--scanner-status` returns the running scanner's mode, last
identified OS, profile, cursor time, and decoded numeric readings. It uses a
private Unix socket for the same Linux user and works while the GUI holds the
suite session lock. Both commands return versioned JSON. They only read
application state and definitions.

For the live VM, expose a local QEMU virtio serial device with the name
`org.wrongway.readout`. The guest session runs `readout-bridge.py guest` with
the installed scanner path when that port is available. The device needs read
and write permissions for the guest session user. On the host, use a socket
path inside a private directory.

```sh
# QEMU arguments
-chardev socket,id=wrongway,path=/private/path/scanner-readout.sock,server=on,wait=off
-device virtio-serial-pci
-device virtserialport,chardev=wrongway,name=org.wrongway.readout

# Host commands
python3 readout-bridge.py host /private/path/scanner-readout.sock status
python3 readout-bridge.py host /private/path/scanner-readout.sock catalog
```

## Edit a P59 tune

1. In the scanner, click **Tune BIN**. Open a complete 1 MiB P59 BIN, then
   click **Load XDF** and select the XML definition for its OS. The editor
   checks the BIN with PCM Hammer and matches the OS in the XDF header.
2. Choose a category tab such as Engine, Trans or Fuel Sys. Use the second
   tab row to narrow the category or **All** to search the full XDF. The
   editor displays scaled values with XDF axis labels, units and optional
   heat-map shading. Drag or Shift-click to select a range. Ctrl-click to
   add or remove individual cells. Click a header to select an axis or
   click the top-left header to select the entire table. Set, Add,
   Multiply and Change by % apply to the selection as one undoable edit.
   Enter numeric edits in the units shown by the XDF. Enter **On** or
   **Off** for switches. Hexadecimal fields use a `0x` prefix.
   Text fields display a copyable line. For a writable text field, enter
   the full field and select **Apply text**. A dot marks each blank or
   nonprintable byte. Hover a character to see its raw hexadecimal byte.
   The VIN and other protected fields are read-only.
3. **Compare BIN** highlights differences against a second valid BIN with
   the same OS and segment layout. With no comparison loaded, red text marks
   changes from the original. Hover a cell to see its reference value.
4. Use **Undo** or **Redo** to review edits. **Save BIN copy** writes a new
   file with recalculated P59 checksums and checks the result with PCM Hammer.

**Smooth** averages adjacent selected cells with extra weight on the original
cell. **Interpolate** uses the endpoints of each contiguous row or column, or
the four corners of a rectangular surface. Interpolation follows numeric XDF
breakpoints when they are strictly ordered. Shift-drag a point on the current
2D or 3D graph to edit its decoded value.

The **Table** menu and right-click menu include copy, paste and paste special.
**Copy with axes** matches pasted values by row and column labels. Blank
clipboard cells are skipped. **Multiply by %** treats 100% as unchanged, and
**Apply half of % change** applies half the distance from 100%. Use the Table
menu to save or load TSV values, adjust displayed decimals, and restore a
table from the opened BIN. With the grid focused, Ctrl+A selects all,
Ctrl+C copies, Ctrl+Shift+C copies with axes, Ctrl+V pastes, Ctrl+Z undoes,
Ctrl+Y redoes, Shift+arrow extends the selection and Shift+plus or minus
adjusts selected cells by one displayed decimal step.

Verified with OS 12587811 and its July 17, 2025 XDF. The editor loaded 1,303
parameters, including all 21 text fields and the D1201 Reverse gear ratio.
Three switches with masks wider than their defined storage appear under
**Skipped XDF items**. The tuning editor works with offline BIN files.
PCM flashing uses a separate workflow.

## Tuner Maths

In the tuning editor, select editable numeric cells and click **Maths**, or
choose **Maths → Apply formula to selection**. Choose a preset or enter a
formula, set its constant, and click **Preview**. The preview shows the decoded
values after storage rounding and validates the entire selection. Click
**Apply to selection** to commit one undoable edit.

Use `[X]` for each current decoded cell, `[K]` for the constant, and `[B]`
for the matching cell in an opened comparison BIN. `[Row]` and `[Column]`
read numeric XDF axis labels. For example, `[X] * (1 + [K] / 100)` applies a
percentage correction, and `[X] + ([B] - [X]) * [K] / 100` blends toward the
comparison. The expression engine supports arithmetic, comparisons, `if`,
`min`, `max`, `abs`, `pow`, `sqrt`, `round` and `clamp`.

**Maths → Edit maths presets** adds, edits and removes reusable formulas.
Presets are saved in `~/.config/wrongway-suite/tuner-maths.json`.

## User Defined Parameters

With a validated P59 BIN open, select **Edit → User Defined Parameters**.
Choose **Add** or **Edit**. Give the parameter a name and enter its BIN
file offset, signed or unsigned storage type (or Float32), byte order,
and units. A scalar is 1 × 1. For a contiguous row-major table, specify
the dimensions and optionally supply comma-separated row and column labels.
Enter a reversible conversion using `X` for the raw value, such as
`X * 0.5 + 1`. The definition must fit in one writable calibration segment.

Click **Apply** in both dialogs. The definition appears on the **User Defined**
tab, where you can edit its decoded values and save a checksum-validated BIN
copy. Definitions are saved per OS in `tuner-parameters-<OS>.json` beside the
scanner layout.

## Command-line checks

```sh
./WrongWayScanner --self-test [optional-full-P59-BIN]
./WrongWayScanner --tune "/path/to/tune.bin" "/path/to/definition.xdf"
./WrongWayScanner --tune-inspect "/path/to/tune.bin" "/path/to/definition.xdf"
./WrongWayScanner --probe-gt /dev/ttyUSB0
./WrongWayScanner --identify-gt /dev/rfcomm0
./WrongWayScanner --discover-gt /dev/ttyUSB0
python3 Tests/test_gt.py ./WrongWayScanner
./WrongWayScanner --inspect "/path/to/your-log.hpl"
./WrongWayScanner --export "/path/to/your-log.hpl" "/path/to/new-log.csv"
```

## Build and source

Tested on Debian 13 x86-64. Rebuild with a .NET 8 SDK and NuGet access:

```sh
dotnet publish Source/WrongWayScanner.csproj -c Release
```

The scanner source is distributed under GPL-3.0-only. The native serial
implementation and logging profiles come from the prior WrongWay Linux
backend. The unmodified PCM Hammer 2.0.0 protocol library and corresponding
managed source are in `Source/ThirdParty`. See `Licenses` for dependency notices.

- [PCM Hammer 2.0.0 source release](https://github.com/PcmHammer/PcmHammer/releases/tag/2.0.0)

The GT inspection follows the Hammerdroid Linux protocol workflow at commit
`04f1c4c06a44174b0e63c3987fb0a47ae66bf3f6`.

- [Hammerdroid Linux source](https://github.com/dynapetey/Hammerdroid/tree/04f1c4c06a44174b0e63c3987fb0a47ae66bf3f6/desktop)
- [OBDX DVI framing reference](https://obdxpro.com/Downloads/ReferenceManuals/OBDX%20Pro%20VT%20Reference%20Guide%20v2.pdf)
- [BlueZ RFCOMM commands](https://manpages.debian.org/bookworm/bluez/rfcomm.1.en.html)

## Expanded time chart

Select up to 32 simultaneous time traces using the channel checkboxes or Edit
traces. Each trace has its own color and optional scale. Larger charts scroll
vertically so labels and values remain readable; Ctrl+mouse wheel zooms time.
VCM layout imports retain up to 32 distinct time traces. The live recording
profile still follows the adapter protocol byte budget; plotting more traces
also supports recorded logs and calculated channels.

## Live fuel-pump relay tests

In Tools → VCM Controls, Pump on (2s) and Pump off (2s) send P59 relay
commands and wait for the PCM response. The engine must be stopped and the
vehicle stationary, with ignition on. Each test returns to automatic PCM
control after two seconds, on Stop, or after an uncertain command response.
Return pump to PCM is available separately and clears actuator overrides.
A PCM acknowledgement confirms request acceptance; observe the pump/relay
for physical operation. Supported OS: 12587603 and 12587811.
See [firmware evidence](Source/Reference/P59-Fuel-Pump.md).

### Fuel-trim recording and expanded polling (0.4.15)

Custom profiles on P59 OS 12587603/12587811 use bounded single-row polling when
fuel-trim PIDs 0006–0009 are selected or the inputs exceed 18 bytes. Up to 36
bytes are packed into six groups FE–F9, requested in batches of at most three.
Rows missing a group are discarded; unrelated or duplicate groups cannot
complete a row. The rate depends on bus and adapter response time. Values in a
polled row are collected sequentially, not a simultaneous snapshot. Other
profiles retain the existing stream path.

Startup failures retain the library's parameter-specific error. Each new raw
CSV has a sibling `.diagnostics.log`; gaps longer than 500 ms are reported in
status and diagnostics. The bounded diagnostic command is:

```
WrongWayScanner --record-live /dev/ttyUSB0 layout.json new-log.csv 15
```

The A/C-input wideband AFR formula remains `(volts / 0.5) + 9.57`. Calculated
AFR is blank for zero or railed voltage, a reported stopped engine, or a
reported zero ignition voltage. Raw inputs remain recorded unchanged.
These guards apply to calculated channels; previously exported AFR columns
remain original recorded data.

Packing, decoding all four trims, missing/duplicate/unrelated responses,
cancellation, and wideband shutdown/recovery are exercised by self-tests.
Expanded polling still requires vehicle validation; passing software tests is
not a claim of confirmed vehicle streaming or corrected engine calibration.

### Installed editor definitions (0.4.16)

The native editor automatically loads
`$XDG_DATA_HOME/wrongway-suite/definitions/<OS>/definition.xdf` when opening a
validated BIN (default data root: `~/.local/share`). OS compatibility checks
still apply. A bad installed definition leaves the BIN open with an error and
manual XDF loading available. The July 17, 2025 12587811 definition uses the
native P59 checksum engine on save; its companion Windows TunerPro DLL is
retained for portability and is not executed by the Linux editor.

## Integrated Linux J2534 and PID audit

The suite launcher starts this scanner. The hardware row selects **Serial (OBDX DVI)**
(default) or **J2534 (Linux VPW)**. J2534 provides SAE discovery and SAE-only
FuelTrims/Basics recording. GM enhanced channels and the A/C-input wideband
continue to use serial DVI.

Linux x64 builds require `gcc` and compile `Native/obdx_j2534.c`; build and publish
outputs include `libobdx_j2534.so` beside the scanner automatically. See
[Native/README.md](Native/README.md) for emulator checks and transport limits.

`--self-test` includes 36 raw-byte decoder cases for O2 millivolts, fuel trims,
oil pressure, RPM, coolant, vehicle speed and fuel-system status. Emulator
results validate synthetic responses; real PCM streaming is still unverified.
