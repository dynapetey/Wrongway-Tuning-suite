using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
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
            if (args is ["--self-test"])
            {
                Console.WriteLine($"Live profile checks passed ({LiveScanner.SelfTest()} channels).");
                ScannerLayout.SelfTest();
                Console.WriteLine("Layout persistence and histogram checks passed.");
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
            if (args is ["--scanner-status"])
            {
                Console.WriteLine(ScannerReadout.Read().GetAwaiter().GetResult());
                return 0;
            }
            if (args is ["--serial-test", var port])
            {
                LiveScanner.SerialTest(port).GetAwaiter().GetResult();
                Console.WriteLine("Linux serial loopback passed.");
                return 0;
            }
            if (args is [var probe, var device] && probe is "--probe-gt" or "--identify-gt")
            {
                using var cancellation = new CancellationTokenSource();
                Console.CancelKeyPress += (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    cancellation.Cancel();
                };
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
                Console.Error.WriteLine("WrongWayScanner --catalog [OS] | --scanner-status");
                Console.Error.WriteLine("WrongWayScanner --probe-gt DEVICE | --identify-gt DEVICE");
                Console.Error.WriteLine("WrongWayScanner --tune BIN XDF | --tune-inspect BIN XDF");
                return 2;
            }

            StartupFile = args.FirstOrDefault();
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
