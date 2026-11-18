# WrongWay Scanner + Hammerdroid 0.4.9

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
  Use the **Table:** dropdown in the histogram header to switch between
  multiple imported or saved histogram tables. The histogram updates during
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

Formula settings are saved with the layout. Missing or ambiguous sources
produce blank readings. Lookahead methods use future samples already in a log.

## VCM control frame preview

**Tools → VCM control preview** shows draft J1850 VPW frames for P01/P59
special functions, including CASE learn, relay overrides, and cylinder balance.
The same preview is available from the command line.

```sh
./WrongWayScanner --vcm-control-preview CrankRelearn
```

Live control requires PCM-specific frame verification and hardware testing.

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
