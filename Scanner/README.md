# WrongWay Scanner + Hammerdroid 0.4.4

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
- **Gauges** shows engine speed, MAP and throttle at the current cursor or
  latest scan sample. **Graph** shows means and counts in 500 RPM by 10 kPa
  bins during log review. Samples are matched to the most recent RPM and MAP
  readings within 500 ms. Drag the panel dividers to resize the workspace.
- **Wideband AFR** adds a calculated channel from a voltage input. The starting
  formula is volts × 2 + 9.57. Adjust the scale and offset to match your gauge.
- **Export CSV** writes the recorded channel updates, preserving sparse fields.
  Choose a new filename. The app can reopen this CSV and other comma-delimited
  logs with a time column in seconds or milliseconds.

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

Select **WrongWayP59**, **Basics**, or **FuelTrims** and click **Record**.
Choose a new file, and the app saves each complete frame as it arrives.
**Read DTCs** requests stored codes. The live scanner targets GM P01/P59
J1850 VPW. The hardware controls share one operation at a time.

The GT and PCM identification paths passed a simulated serial test. USB
and RFCOMM hardware, real PCM logging, and stored-code reads still need a
vehicle test. 

## Edit a P59 tune

1. In the scanner, click **Tune BIN**. Open a complete 1 MiB P59 BIN, then
   click **Load XDF** and select the XML definition for its OS. The editor
   checks the BIN with PCM Hammer and matches the OS in the XDF header.
2. Choose a category tab such as Engine, Trans or Fuel Sys. Use the second
   tab row to narrow the category or **All** to search the full XDF. The
   editor displays scaled values with XDF axis labels, units and optional
   heat-map shading. You can edit a cell, row, column,
   or entire table using Set to, Add, Multiply, or Change by %.
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

Verified with OS 12587811 and its July 17, 2025 XDF. The editor loaded 1,303
parameters, including all 21 text fields and the D1201 Reverse gear ratio.
Three switches with masks wider than their defined storage appear under
**Skipped XDF items**. The tuning editor works with offline BIN files.
PCM flashing uses a separate workflow.

## Command-line checks

```sh
./WrongWayScanner --self-test
./WrongWayScanner --tune "/path/to/tune.bin" "/path/to/definition.xdf"
./WrongWayScanner --tune-inspect "/path/to/tune.bin" "/path/to/definition.xdf"
./WrongWayScanner --probe-gt /dev/ttyUSB0
./WrongWayScanner --identify-gt /dev/rfcomm0
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
