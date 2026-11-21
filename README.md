# WrongWay Suite 0.4.9 for Linux x86-64

The suite combines the WrongWayScanner 0.4.9 scanner and decoded P59 BIN
editor with a PCM window backed by PCM Hammer 2.0.0. All three use the
dark theme.

## Install and start

Extract the full archive to its permanent folder. On Ubuntu, Debian or Mint,
open a terminal in that folder and run:

```sh
bash install.sh
./launch.sh
```

On Xfce, open the existing application launcher and choose
**Tuning → WrongWay Suite**. Reinstalling updates the same launcher entry.

The suite can also be started with `./launch.sh`.
The installer uses apt when Python Tk, Mono or util-linux is missing. On
other distributions, install those dependencies through the distribution
package manager before running the script. The scanner's .NET runtime is
bundled. The suite uses an X11 desktop, including XWayland.

## Scan, edit and work with the PCM

- Use **Open log** or **Record** for logs and **Tune BIN** for decoded P59
  calibration editing with a matching XDF. **Save BIN copy** writes a local
  copy with updated checksums.
- Use **Layout** to edit gauges, histogram axes and cell statistics, time
  chart traces, display channels and the live recording profile. Save and
  load layouts as JSON to switch setups. **Layout → Import VCM XML** imports
  channel lists, graph tables, layouts and math parameters. Each histogram
  table can have its own sample filter.
- Use **Tools → Maths** to create formula channels for live and recorded
  data. VCM formulas support time windows, boolean logic and `[PID.UnitID]`
  references. Open **Tune BIN → Edit → User Defined Parameters** to define and
  scale calibration scalars and tables by BIN offset.
- **Tools → Telemetry Hub** indexes opened or recorded logs and exports JSON
  telemetry bundles. **VCM control preview** shows draft frames offline.
- **Channels** opens with the catalog for P59 OS 12587811. Search by name or
  PID, or use **+** to add a channel to the recording profile. **Read ECM PIDs**
  queries the ECM and filters the list to reported standard and responding
  enhanced PIDs.
- `./launch.sh scanner --catalog 12587811` exports the catalog as JSON.
  `./launch.sh scanner --scanner-status` reads the running scanner as JSON.
  See [the scanner README](Scanner/README.md#read-the-scanner-from-tools) for
  access from the host of a live VM.
- Choose a numeric map to see its 2D profile, current and reference 3D
  surfaces, and editable table. Click a plotted point to select its table
  cell. Drag to rotate a surface, scroll to zoom, and select **Reset view**
  to restore the default angle. **Compare BIN** supplies the reference surface.
- Open **Tools → PCM & flashing** in the scanner to read the connected P01
  or P59. The PCM window receives the scanner's selected serial port.
- Open **Tools → LM7 to ’95 F-150 wiring guide** for the one-sheet schematic
  and detailed connector views. Both PDFs can be saved offline.
- With an OBDX Pro GT on USB, select **OBDX**, then **Read properties** and
  **Read full backup**. Select the edited BIN for **Validate BIN**,
  **Compare with PCM** or **Test write**. The write dialog reviews the
  selected mode, edited file and original backup before sending a write.
- Close the PCM window to use the adapter in the scanner again. To launch
  the PCM window directly, use `./launch.sh pcm`. A suite session lock
  keeps the launcher from starting a second session for the same user.

Physical GT/P59 flashing remains unverified. See `VALIDATION.txt` and the
component documentation in `Scanner/README.md` and `PCM/README.md`.

## Build from source

With the .NET 8 SDK, build the self-contained Linux x86-64 scanner and editor:

```sh
dotnet publish Scanner/Source/WrongWayScanner.csproj -c Release -o build/scanner
install -m 755 build/scanner/WrongWayScanner Scanner/WrongWayScanner
```

The PCM tool uses Python Tk and the bundled Mono backend. Build instructions
and licensing for that component are in `PCM/README.md`.

## Build the Windows x64 scanner and BIN editor

With the .NET 8 SDK, run from the repository root:

```powershell
dotnet publish Scanner/Source/WrongWayScanner.csproj -c Release -r win-x64 -o build/windows
```

Run `build/windows/WrongWayScanner.exe` and keep the published `Assets` folder
beside it. For live scanning with an OBDX Pro GT over USB, select the COM port
shown in Windows Device Manager. Offline log viewing and BIN editing are also
available. The integrated PCM Hammer window and flashing are Linux-only.

## Create a live USB from Windows

The Windows creator formats a selected USB drive and installs WrongWay Tuning
Live with persistent storage. See the [creator instructions](LiveUsbCreator/README.md)
and download the [Windows release](https://github.com/dynapetey/Wrongway-Tuning-suite/releases/tag/live-usb-creator-v0.4.8).

## Boot the Linux live ISO

Use the [0.4.9 live ISO release](https://github.com/dynapetey/Wrongway-Tuning-suite/releases/tag/live-iso-v0.4.9)
for the latest scanner and **Scan → Read ECM PIDs**. It opens the scanner
automatically and includes the Linux PCM Hammer backend. See the [ISO instructions](LiveUsbCreator/README.md#linux-live-iso)
for writable USB and VM boot, checksums, and how to rebuild the image.
