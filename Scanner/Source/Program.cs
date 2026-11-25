using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using System.Globalization;
using System.Text.Json;

namespace WrongWay;

static class Program
{
    public static string? StartupFile { get; private set; }
    public static (string Bin, string Xdf)? StartupTune { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args is ["--self-test"] or ["--self-test", _])
            {
                Console.WriteLine($"Live profile checks passed ({LiveScanner.SelfTest()} channels).");
                ScannerLayout.SelfTest();
                ImperialUnits.SelfTest();
                Console.WriteLine("Layout persistence and histogram checks passed.");
                MathChannel.SelfTest();
                HistogramSnapshot.SelfTest();
                Console.WriteLine("Formula, VCM window methods, voltage units and time alignment checks passed.");
                VcmTelemetryControls.SelfTest();
                Console.WriteLine("VCM frame preview and XML import checks passed.");
                TuneMaths.SelfTest();
                Console.WriteLine("Tuner maths presets and formula inputs passed.");
                if (args.Length == 2)
                {
                    UserTuneParameter.SelfTest(args[1]);
                    TuneMaths.SelfTest(args[1]);
                    Console.WriteLine("User-defined parameters, protected ranges and BIN checksum checks passed.");
                    Console.WriteLine("Tuner maths preview, selection, axis inputs, validation and Undo checks passed.");
                }
                return 0;
            }
            if (args is ["--discover-j2534", var j2534Port])
            {
                var result = J2534Gt.Discover(j2534Port, Console.Error.WriteLine, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(result));
                return 0;
            }
            if (args is ["--record-j2534", var j2534Device, var j2534Profile, var j2534Path, var j2534Seconds])
            {
                if (!int.TryParse(j2534Seconds, out int seconds) || seconds is < 1 or > 300)
                    throw new ArgumentException("J2534 recording duration must be 1–300 seconds.");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
                int samples = 0;
                try
                {
                    J2534Gt.Record(j2534Device, j2534Profile, j2534Path,
                        Console.Error.WriteLine, _ => samples++, timeout.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (samples > 0) { }
                if (samples == 0) throw new IOException("J2534 returned no complete samples.");
                Console.WriteLine($"J2534: recorded {samples} SAE samples to {j2534Path}");
                return 0;
            }
            if (args is ["--catalog"] or ["--catalog", _])
            {
                uint os = LiveScanner.DefaultOs;
                if (args.Length == 2 && !uint.TryParse(args[1], out os))
                    throw new ArgumentException("Enter a decimal PCM OS ID, such as 12587811.");
                var channels = LiveScanner.Catalog(os);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    SchemaVersion = 1, Os = os, Description = "Bundled PCM Logger definitions",
                    PidCount = channels.Where(channel => channel.Pid.HasValue).Select(channel => channel.Pid).Distinct().Count(),
                    RamChannelCount = channels.Count(channel => channel.RamAddress.HasValue),
                    MaximumRecordingBytes = LiveScanner.MaximumLiveBytes, Channels = channels
                }));
                return 0;
            }
            if (args is ["--record-live", var recordPort, var recordLayout, var recordPath, var recordSeconds])
            {
                if (!int.TryParse(recordSeconds, out int seconds) || seconds is < 1 or > 300)
                    throw new ArgumentException("Choose a recording duration between 1 and 300 seconds.");
                var layout = ScannerLayout.Load(recordLayout);
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
                int samples = 0;
                try
                {
                    LiveScanner.Record(recordPort, LiveScanner.Custom, layout.LiveChannels, recordPath,
                        Console.Error.WriteLine, os => Console.Error.WriteLine($"PCM OS {os}"),
                        _ => samples++, cancellation.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (samples > 0) { }
                if (samples == 0) throw new IOException("No complete live samples were recorded.");
                Console.WriteLine($"Recorded {samples} complete samples to {recordPath}");
                return 0;
            }
            if (args is ["--scanner-status"])
            {
                Console.WriteLine(ScannerReadout.Read().GetAwaiter().GetResult());
                return 0;
            }
            if (args is ["--telemetry-sessions"])
            {
                Console.WriteLine(JsonSerializer.Serialize(VcmTelemetryControls.LoadSessions()));
                return 0;
            }
            if (args is ["--telemetry-export", var logInput, var bundleOutput])
            {
                if (Path.GetFullPath(logInput) == Path.GetFullPath(bundleOutput))
                    throw new ArgumentException("Choose a separate path for the telemetry bundle.");
                var log = ScanLog.Load(logInput);
                var layout = File.Exists(ScannerLayout.DefaultPath) ? ScannerLayout.Load(ScannerLayout.DefaultPath) : ScannerLayout.Default;
                MathChannel.Build(log, layout.Maths ?? MathSetting.Defaults);
                VcmTelemetryControls.ExportTelemetryBundle(log, null, null, bundleOutput);
                Console.WriteLine($"Exported telemetry bundle to {bundleOutput}");
                return 0;
            }
            if (args is ["--import-vcm-xml", var xmlInput, var layoutOutput])
            {
                var baseLayout = File.Exists(layoutOutput) ? ScannerLayout.Load(layoutOutput) : ScannerLayout.Default;
                var imported = VcmConfigImporter.ImportLayoutXml(xmlInput, baseLayout);
                imported.Save(layoutOutput);
                Console.WriteLine($"Imported {Path.GetFileName(xmlInput)} into {layoutOutput}");
                return 0;
            }
            if (args.Length >= 2 && args[0] == "--vcm-control-preview")
            {
                if (args.Length > 3 || !Enum.TryParse<VcmControlAction>(args[1], ignoreCase: true, out var action)
                    || !Enum.IsDefined(action))
                    throw new ArgumentException("Choose a named VCM control and an optional value.");
                double? target = args.Length == 3
                    ? double.Parse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture) : null;
                int? cylinder = null;
                if (action == VcmControlAction.CylinderPowerBalance && args.Length == 3)
                    cylinder = int.Parse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture);
                var frame = VcmTelemetryControls.BuildVpwCommand(new VcmControlRequest(action, target, cylinder));
                Console.WriteLine(JsonSerializer.Serialize(new { Action = action.ToString(), DraftFrameHex = Convert.ToHexString(frame) }));
                return 0;
            }
            if (args is ["--vcm-control-live", var controlDevice, var control, "--confirm"])
            {
                if (!Enum.GetNames<VcmControlAction>().Any(name => name.Equals(control, StringComparison.OrdinalIgnoreCase)) ||
                    !Enum.TryParse<VcmControlAction>(control, ignoreCase: true, out var action) ||
                    !VcmTelemetryControls.AvailableLive(action))
                    throw new ArgumentException("Choose Fan1On, Fan1Off, Fan2On, Fan2Off, FuelPumpOn, FuelPumpOff, ReleaseFuelPumpControl, ReleaseAllControls or ClearTroubleCodes.");
                using var cancellation = new CancellationTokenSource();
                Console.CancelKeyPress += (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    cancellation.Cancel();
                };
                var result = HammerdroidProbe.RunVcmControl(controlDevice, action, Console.Error.WriteLine, cancellation.Token)
                    .GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(result));
                return 0;
            }
            if (args.Length > 0 && args[0] == "--vcm-control-live")
                throw new ArgumentException("Usage: --vcm-control-live /dev/ttyACM0 ACTION --confirm");
            if (args is ["--serial-test", var port])
            {
                LiveScanner.SerialTest(port).GetAwaiter().GetResult();
                Console.WriteLine("Linux serial loopback passed.");
                return 0;
            }
            if (args is [var probe, var device] && probe is "--probe-gt" or "--identify-gt" or "--discover-gt")
            {
                using var cancellation = new CancellationTokenSource();
                Console.CancelKeyPress += (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    cancellation.Cancel();
                };
                if (probe == "--discover-gt")
                {
                    var discovery = HammerdroidProbe.DiscoverPids(device, Console.Error.WriteLine, cancellation.Token)
                        .GetAwaiter().GetResult();
                    Console.WriteLine(JsonSerializer.Serialize(discovery));
                    return 0;
                }
                var result = HammerdroidProbe.Inspect(device, probe == "--identify-gt",
                    Console.Error.WriteLine, cancellation.Token).GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(result));
                return result.PcmError is null ? 0 : 3;
            }
            if (args is ["--inspect", var file])
            {
                var log = ScanLog.Load(file);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    log.ScannerVersion, log.Frames, log.Blocks, log.Duration, log.Warnings,
                    Channels = log.Channels.Select(c => new
                    {
                        c.Key, c.Pid, c.Name, c.Unit, Updates = c.Readings.Count,
                        NumericUpdates = c.Readings.Count(r => r.Number is not null)
                    })
                }));
                return 0;
            }
            if (args is ["--export", var input, var output])
            {
                if (File.Exists(output))
                    throw new IOException("The export path already exists.");
                CsvLog.Export(ScanLog.Load(input), output);
                return 0;
            }
            if (args is ["--tune-inspect", var tuneBin, var tuneXdf])
            {
                var image = new TuneImage(tuneBin);
                var definition = TuneDefinition.Load(tuneXdf, image);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    image.Os, definition.Title, definition.Skipped,
                    Parameters = definition.Parameters.Select(parameter => new
                    {
                        parameter.Title, parameter.Editable, parameter.Rows, parameter.Columns,
                        parameter.BaseAddress, parameter.Units
                    })
                }));
                return 0;
            }
            if (args is ["--tune", var binPath, var xdfPath])
            {
                StartupTune = (binPath, xdfPath);
                args = [];
            }
            if (args.Length > 1 || args is [var option] && option.StartsWith("--"))
            {
                Console.Error.WriteLine("WrongWayScanner [log.hpl|log.csv]");
                Console.Error.WriteLine("WrongWayScanner --inspect FILE | --export INPUT OUTPUT");
                Console.Error.WriteLine("WrongWayScanner --catalog [OS] | --scanner-status | --telemetry-sessions");
                Console.Error.WriteLine("WrongWayScanner --telemetry-export LOG OUTPUT.json | --import-vcm-xml INPUT.xml LAYOUT.json");
                Console.Error.WriteLine("WrongWayScanner --vcm-control-preview ACTION [VALUE]");
                Console.Error.WriteLine("WrongWayScanner --probe-gt DEVICE | --identify-gt DEVICE | --discover-gt DEVICE");
                Console.Error.WriteLine("WrongWayScanner --tune BIN XDF | --tune-inspect BIN XDF");
                return 2;
            }

            StartupFile = args.FirstOrDefault();
            LinuxClipboard.Initialize();
            return AppBuilder.Configure<ScannerApp>().UsePlatformDetect()
                .StartWithClassicDesktopLifetime([], ShutdownMode.OnMainWindowClose);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}

sealed class ScannerApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (Program.StartupTune is { } paths)
            {
                var window = new TuneWindow();
                window.Load(paths.Bin, paths.Xdf);
                desktop.MainWindow = window;
            }
            else
                desktop.MainWindow = new ScannerWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
