# WrongWay Suite 0.4.8 for Linux x86-64

The suite combines the WrongWayScanner 0.4.8 scanner and decoded P59 BIN
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
  load layouts as JSON to switch setups.
- Use **Tools → Maths** to create formula channels for live and recorded
  data. Open **Tune BIN → Edit → User Defined Parameters** to define and
  scale calibration scalars and tables by BIN offset.
- **Channels** opens with the catalog for P59 OS 12587811. Search by name or
  PID, or use **+** to add a channel to the recording profile.
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
