using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace WrongWay;

sealed class ScannerWindow : Window
{
    private static readonly IBrush BackgroundBrush = DeskUi.Frame;
    private static readonly IBrush Muted = DeskUi.Muted;
    private static readonly IBrush Accent = DeskUi.Blue;
    private static readonly IBrush Warning = DeskUi.Alert;
    private static readonly string PcmLauncher = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "PCM", "launch.sh"));
    private static readonly IBrush[] ChannelInks =
        TraceChart.SeriesBrushes;
    private readonly TextBlock sessionText = new();
    private readonly TextBlock status = new();
    private readonly TextBlock clock = new();
    private readonly TextBox filter = new() { Watermark = "Find a channel or PID" };
    private readonly TextBox port = new() { Width = 170, Watermark = "/dev/ttyUSB0" };
    private readonly ComboBox profile = new() { ItemsSource = LiveScanner.Profiles, SelectedIndex = 0, Width = 152 };
    private readonly StackPanel channelList = new() { Spacing = 3 };
    private readonly Slider slider = new() { Minimum = 0, Maximum = 1, IsEnabled = false };
    private readonly TraceChart chart = new() { MinHeight = 170 };
    private readonly ScanGauge rpmGauge = new("Engine speed", "rpm", 8000, TraceChart.SeriesBrushes[0]);
    private readonly ScanGauge mapGauge = new("Manifold pressure", "kPa", 120, TraceChart.SeriesBrushes[1]);
    private readonly ScanGauge throttleGauge = new("Throttle position", "%", 100, TraceChart.SeriesBrushes[2]);
    private readonly ContentControl graph = new();
    private readonly TabControl instrumentTabs = new();
    private readonly TextBlock channelCount = DeskUi.Label("0 channels", 11, DeskUi.Muted);
    private readonly TextBlock modeText = DeskUi.Section("OFFLINE");
    private readonly List<Channel> active = [];
    private readonly Dictionary<Channel, TextBlock> displayedValues = [];
    private readonly Button openButton;
    private readonly Button exportButton;
    private readonly Button histogramButton;
    private readonly Button widebandButton;
    private readonly Button recordButton;
    private readonly Button codesButton;
    private readonly Button gtButton;
    private readonly Button pcmToolsButton;
    private readonly Button refreshPorts;
    private readonly Button stopButton;
    private readonly Button playButton;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Stopwatch playback = new();
    private ScanLog? log;
    private CancellationTokenSource? hardware;
    private bool closeAfterHardware;
    private bool adjustingSlider;
    private bool live;
    private bool busy;
    private bool pcmToolsOpen;
    private long cursor;
    private long playOrigin;

    public ScannerWindow()
    {
        Title = "WrongWay Scanner + Hammerdroid · Linux";
        Width = 1260;
        Height = 800;
        MinWidth = 900;
        MinHeight = 570;
        Background = BackgroundBrush;
        FontFamily = new FontFamily("DejaVu Sans");
        sessionText.Text = "No log loaded";
        sessionText.Foreground = Muted;
        sessionText.TextTrimming = TextTrimming.CharacterEllipsis;
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.Text = "Ready · open a compact HP Tuners HPL log or a CSV recording.";
        status.Foreground = Muted;
        clock.Text = "0.000 s";
        clock.Foreground = Accent;
        clock.FontWeight = FontWeight.SemiBold;
        clock.MinWidth = 96;

        openButton = Button("Open log", PickLog, DeskUi.Open);
        exportButton = Button("Export CSV", Export, DeskUi.Save);
        histogramButton = Button("Graph", ShowHistogram, DeskUi.Chart);
        widebandButton = Button("Wideband AFR", async () => await ShowWideband());
        recordButton = Button("Record", Record, DeskUi.Record);
        codesButton = Button("Read DTCs", async () => await ReadCodes());
        stopButton = Button("Stop", () => hardware?.Cancel(), DeskUi.Stop);
        playButton = Button("Play", TogglePlayback);
        var zoomOut = Button("Full view", FitView);
        refreshPorts = Button("Find ports", async () => await FindPort());
        var inspectAdapter = new MenuItem { Header = "Read adapter details" };
        inspectAdapter.Click += async (_, _) => await ShowAdapter(includePcm: false);
        var identifyPcm = new MenuItem { Header = "Identify PCM and adapter" };
        identifyPcm.Click += async (_, _) => await ShowAdapter(includePcm: true);
        gtButton = DeskUi.Tool("GT / PCM");
        gtButton.Flyout = new MenuFlyout { Items = { inspectAdapter, identifyPcm } };
        pcmToolsButton = Button("PCM & flashing", OpenPcmTools);
        var pcmToolsMenu = DeskUi.Action("PCM & flashing", pcmToolsButton);
        pcmToolsMenu.IsVisible = File.Exists(PcmLauncher);
        var tuneButton = Button("Tune BIN", async () =>
        {
            var editor = new TuneWindow { WindowStartupLocation = WindowStartupLocation.CenterOwner };
            await editor.ShowDialog(this);
        }, DeskUi.Table);
        var gaugeMenu = new MenuItem { Header = "Gauges" };
        gaugeMenu.Click += (_, _) => instrumentTabs.SelectedIndex = 0;
        var menu = new Menu
        {
            ItemsSource = new[]
            {
                DeskUi.Menu("_File", DeskUi.Action("Open log", openButton), DeskUi.Action("Export CSV", exportButton),
                    DeskUi.Action("Tune BIN", tuneButton)),
                DeskUi.Menu("_Scan", DeskUi.Action("Record", recordButton), DeskUi.Action("Stop", stopButton),
                    DeskUi.Action("Read DTCs", codesButton), DeskUi.Action("Find ports", refreshPorts)),
                DeskUi.Menu("_View", gaugeMenu, DeskUi.Action("RPM × MAP graph", histogramButton),
                    DeskUi.Action("Fit time chart", zoomOut)),
                DeskUi.Menu("_Tools", DeskUi.Action("Wideband AFR", widebandButton), pcmToolsMenu)
            }
        };
        var toolbar = Row(openButton, exportButton, recordButton, stopButton, codesButton,
            histogramButton, widebandButton, tuneButton);
        var hardwareRow = Row(DeskUi.Section("OBDX PRO GT"), port, refreshPorts, gtButton,
            DeskUi.Label("Profile", 12, Muted), profile);

        var columns = new DockPanel { LastChildFill = true };
        var valueTitle = DeskUi.Label("Value", 11, Muted);
        DockPanel.SetDock(valueTitle, Dock.Right);
        columns.Children.Add(valueTitle);
        columns.Children.Add(DeskUi.Label("Name", 11, Muted));
        var sidebar = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        Put(sidebar, DeskUi.Strip(DeskUi.Section("Channels"), DeskUi.Header), 0);
        filter.Margin = new Thickness(5);
        Put(sidebar, filter, 1);
        Put(sidebar, DeskUi.Strip(columns), 2);
        Put(sidebar, new ScrollViewer { Content = channelList }, 3);
        Put(sidebar, DeskUi.Strip(channelCount), 4);

        var gauges = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), MinHeight = 92 };
        Put(gauges, rpmGauge, 0);
        Put(gauges, mapGauge, 0, 1);
        Put(gauges, throttleGauge, 0, 2);
        instrumentTabs.ItemsSource = new[]
        {
            new TabItem { Header = DeskUi.Section("Gauges"), MinHeight = 30, Padding = new Thickness(9, 3), Content = new Border { Background = DeskUi.ChartBackground, Child = gauges } },
            new TabItem { Header = DeskUi.Section("Graph"), MinHeight = 30, Padding = new Thickness(9, 3), Content = graph }
        };
        instrumentTabs.SelectedIndex = 0;
        instrumentTabs.SelectionChanged += (_, args) =>
        {
            if (args.Source == instrumentTabs && instrumentTabs.SelectedIndex == 1)
                RefreshHistogram();
        };

        var chartHeader = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(zoomOut, Dock.Right);
        chartHeader.Children.Add(zoomOut);
        chartHeader.Children.Add(DeskUi.Section("Chart vs. Time"));
        var playbackRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        Put(playbackRow, playButton, 0, 0);
        Put(playbackRow, slider, 0, 1);
        Put(playbackRow, clock, 0, 2);
        var plot = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Put(plot, DeskUi.Strip(chartHeader, DeskUi.Header), 0);
        Put(plot, chart, 1);
        Put(plot, DeskUi.Strip(playbackRow), 2);

        var main = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("285,5,*"),
            RowDefinitions = new RowDefinitions("198,5,*"), Margin = new Thickness(5)
        };
        main.ColumnDefinitions[0].MinWidth = 195;
        main.ColumnDefinitions[2].MinWidth = 340;
        main.RowDefinitions[0].MinHeight = 132;
        main.RowDefinitions[2].MinHeight = 240;
        var channelsPanel = DeskUi.FrameOf(sidebar);
        Grid.SetRowSpan(channelsPanel, 3);
        Put(main, channelsPanel, 0, 0);
        var channelSplitter = DeskUi.Splitter(horizontal: false);
        Grid.SetRowSpan(channelSplitter, 3);
        Put(main, channelSplitter, 0, 1);
        Put(main, DeskUi.FrameOf(instrumentTabs), 0, 2);
        Put(main, DeskUi.Splitter(horizontal: true), 1, 2);
        Put(main, DeskUi.FrameOf(plot), 2, 2);

        var session = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Put(session, sessionText, 0, 0);
        Put(session, modeText, 0, 1);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto") };
        Put(layout, DeskUi.Strip(menu, DeskUi.Surface), 0);
        Put(layout, DeskUi.Strip(toolbar), 1);
        Put(layout, DeskUi.Strip(hardwareRow), 2);
        Put(layout, DeskUi.Strip(session, DeskUi.Header), 3);
        Put(layout, main, 4);
        Put(layout, DeskUi.Strip(status), 5);
        Content = layout;

        timer.Tick += (_, _) => Tick();
        timer.Start();
        chart.Seek = Seek;
        chart.ZoomAt = Zoom;
        slider.PropertyChanged += (_, args) =>
        {
            if (args.Property == Slider.ValueProperty && !adjustingSlider && log is not null && !live)
            {
                playback.Stop();
                Seek(log.Start + (long)Math.Round(slider.Value));
            }
        };
        filter.TextChanged += (_, _) => PopulateChannels();
        KeyDown += async (_, args) =>
        {
            if (args.Key == Key.O && args.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                args.Handled = true;
                try { await PickLog(); }
                catch (Exception ex) { SetStatus(ex.Message, true); }
            }
        };
        Closing += (_, args) =>
        {
            if (pcmToolsOpen)
            {
                args.Cancel = true;
                SetStatus("Close PCM & flashing before closing the suite.");
                return;
            }
            if (hardware is null)
                return;
            args.Cancel = true;
            closeAfterHardware = true;
            hardware.Cancel();
            SetStatus("Stopping the active adapter session…");
        };
        Opened += async (_, _) =>
        {
            if (Program.StartupFile is { } path)
                await OpenLog(path);
        };
        RefreshButtons();
    }

    private async Task PickLog()
    {
        if (busy || hardware is not null)
            return;
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a scanner log",
            FileTypeFilter = [new FilePickerFileType("Scanner logs") { Patterns = ["*.hpl", "*.HPL", "*.csv", "*.CSV"] }]
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path)
            await OpenLog(path);
    }

    private async Task OpenLog(string path)
    {
        busy = true;
        RefreshButtons();
        SetStatus("Opening " + Path.GetFileName(path) + "…");
        try
        {
            SetLog(await Task.Run(() => ScanLog.Load(path)));
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
        finally
        {
            busy = false;
            RefreshButtons();
        }
    }

    private async Task Export()
    {
        if (log is null || busy || hardware is not null)
            return;
        string? path = await PickCsvPath(Path.GetFileNameWithoutExtension(log.Name) + "-export");
        if (path is null)
            return;
        var current = log;
        busy = true;
        RefreshButtons();
        try
        {
            await Task.Run(() => CsvLog.Export(current, path));
            SetStatus("Exported " + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
        finally
        {
            busy = false;
            RefreshButtons();
        }
    }

    private async Task<string?> PickCsvPath(string name)
    {
        var picked = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save scanner CSV", SuggestedFileName = name, DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV log") { Patterns = ["*.csv"] }]
        });
        return picked?.TryGetLocalPath();
    }

    private void SetLog(ScanLog next)
    {
        playback.Reset();
        log = next;
        sessionText.Text = next.ScannerVersion is { } version
            ? $"{next.Name}   ·   VCM Scanner {version}"
            : next.Name;
        active.Clear();
        foreach (var (pid, name) in new[] { (12, "RPM"), (11, "Manifold"), (17, "Throttle"), (2630, "Knock Retard") })
        {
            var item = next.Find(pid, name);
            if (pid == 12)
                item ??= next.Find(pid, "Engine Speed");
            if (pid == 11)
                item ??= next.Find(pid, "MAP");
            if (item is not null && item.Readings.Any(r => r.Number is not null))
                active.Add(item);
        }
        if (active.Count == 0)
            active.AddRange(next.Channels.Where(c => c.Readings.Any(r => r.Number is not null)).Take(4));
        chart.Origin = next.Start;
        Seek(next.Start);
        FitView();
        PopulateChannels();
        if (instrumentTabs.SelectedIndex == 1)
            RefreshHistogram();
        string summary = $"{next.Frames:N0} frames   ·   {next.Channels.Count} channels   ·   {next.Duration / 1000d:0.0} s";
        if (next.Warnings.Count > 0)
            summary += $"   ·   {next.Warnings[0]}";
        SetStatus(summary, next.Warnings.Count != 0);
        RefreshButtons();
    }

    private void PopulateChannels()
    {
        channelList.Children.Clear();
        displayedValues.Clear();
        channelCount.Text = $"{log?.Channels.Count ?? 0} channels";
        if (log is null)
            return;
        string search = filter.Text?.Trim() ?? "";
        foreach (var channel in log.Channels)
        {
            if (!channel.Label.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !(channel.Pid?.ToString(CultureInfo.InvariantCulture).Contains(search) ?? false))
                continue;
            var value = Text("—", 11, Muted);
            value.TextTrimming = TextTrimming.CharacterEllipsis;
            value.MaxWidth = 90;
            value.Margin = new Thickness(3, 0, 6, 0);
            DockPanel.SetDock(value, Dock.Right);
            var name = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = channel.Name, TextTrimming = TextTrimming.CharacterEllipsis,
                    FontSize = 12
                },
                IsChecked = active.Contains(channel),
                IsEnabled = channel.Readings.Any(r => r.Number is not null),
                MinHeight = 24, Padding = new Thickness(4, 0, 0, 0)
            };
            ToolTip.SetTip(name, channel.Label + (channel.Pid is { } pid ? $" · PID {pid}" : ""));
            name.IsCheckedChanged += (_, _) =>
            {
                if (name.IsChecked == true)
                {
                    if (active.Count >= 6)
                    {
                        name.IsChecked = false;
                        SetStatus("Select up to six channels for the chart.");
                        return;
                    }
                    if (!active.Contains(channel))
                        active.Add(channel);
                }
                else
                    active.Remove(channel);
                RefreshChart();
            };
            var row = new DockPanel { Children = { value, name } };
            channelList.Children.Add(new Border
            {
                Background = displayedValues.Count % 2 == 0 ? DeskUi.Surface : DeskUi.Frame,
                Child = row
            });
            displayedValues[channel] = value;
        }
        RefreshChart();
    }

    private void Seek(long time)
    {
        if (log is null || live)
            return;
        cursor = Math.Clamp(time, log.Start, log.End);
        if (playback.IsRunning)
            playOrigin = cursor - playback.ElapsedMilliseconds;
        RefreshChart();
    }

    private void FitView()
    {
        if (log is null)
            return;
        chart.ViewStart = log.Start;
        chart.ViewEnd = Math.Max(log.Start + 1, log.End);
        RefreshChart();
    }

    private void Zoom(double position, double factor)
    {
        if (log is null || live || log.Duration < 100)
            return;
        long oldSpan = chart.ViewEnd - chart.ViewStart;
        long span = Math.Clamp((long)(oldSpan * factor), 100, log.Duration);
        long start = (long)(chart.ViewStart + position * (oldSpan - span));
        chart.ViewStart = Math.Clamp(start, log.Start, log.End - span);
        chart.ViewEnd = chart.ViewStart + span;
        RefreshChart();
    }

    private void TogglePlayback()
    {
        if (log is null || live)
            return;
        if (playback.IsRunning)
            playback.Stop();
        else
        {
            if (cursor >= log.End)
                cursor = log.Start;
            playOrigin = cursor;
            playback.Restart();
        }
        RefreshChart();
    }

    private void Tick()
    {
        if (log is null)
            return;
        if (live)
        {
            cursor = log.End;
            chart.ViewStart = Math.Max(log.Start, log.End - 15_000);
            chart.ViewEnd = Math.Max(chart.ViewStart + 1000, log.End);
            RefreshChart();
            return;
        }
        if (!playback.IsRunning)
            return;
        cursor = Math.Min(log.End, playOrigin + playback.ElapsedMilliseconds);
        if (cursor > chart.ViewEnd || cursor < chart.ViewStart)
        {
            long span = chart.ViewEnd - chart.ViewStart;
            chart.ViewStart = Math.Max(log.Start, Math.Min(cursor, log.End - span));
            chart.ViewEnd = chart.ViewStart + span;
        }
        if (cursor == log.End)
            playback.Stop();
        RefreshChart();
    }

    private void RefreshChart()
    {
        chart.Channels = active;
        chart.CursorTime = cursor;
        chart.InvalidateVisual();
        if (log is null)
            return;
        foreach (var (channel, element) in displayedValues)
        {
            var reading = channel.AsOf(cursor);
            element.Text = reading?.Number is { } number
                ? $"{number:0.##} {channel.Unit}".TrimEnd() : reading?.Text ?? "—";
            int index = active.IndexOf(channel);
            element.Foreground = index < 0 ? Muted : ChannelInks[index];
        }
        UpdateGauge(rpmGauge, EngineSpeed(log));
        UpdateGauge(mapGauge, ManifoldPressure(log));
        UpdateGauge(throttleGauge, log.Find(17, "Throttle"));
        clock.Text = $"{(cursor - log.Start) / 1000d:0.000} s";
        playButton.Content = playback.IsRunning ? "Pause" : "Play";
        adjustingSlider = true;
        slider.Maximum = Math.Max(1, log.Duration);
        slider.Value = Math.Max(0, cursor - log.Start);
        adjustingSlider = false;
    }

    private static Channel? EngineSpeed(ScanLog log) => log.Find(12, "RPM") ?? log.Find(12, "Engine Speed");
    private static Channel? ManifoldPressure(ScanLog log) =>
        log.Find(11, "Manifold Absolute Pressure") ?? log.Find(11, "Manifold Pressure") ?? log.Find(11, "MAP");

    private void UpdateGauge(ScanGauge gauge, Channel? channel) =>
        gauge.Update(channel?.AsOf(cursor)?.Number, channel?.Unit ?? "");

    private async Task FindPort()
    {
        string[] available = LiveScanner.Ports();
        if (available.Length == 0)
        {
            SetStatus("No USB or Bluetooth serial port found. Connect the GT or bind its RFCOMM port.", true);
            return;
        }
        if (available.Length == 1)
        {
            port.Text = available[0];
            SetStatus("Selected " + available[0]);
            return;
        }

        var choices = new ListBox { ItemsSource = available, SelectedIndex = 0 };
        var dialog = new Window
        {
            Title = "Select the OBDX Pro GT serial port", Width = 525, Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = BackgroundBrush
        };
        var select = Button("Select port", () =>
        {
            if (choices.SelectedItem is not string path)
                return;
            port.Text = path;
            SetStatus("Selected " + path);
            dialog.Close();
        });
        var layout = new DockPanel { Margin = new Thickness(14) };
        var actions = Row(select, Button("Cancel", dialog.Close));
        actions.Margin = new Thickness(0, 10, 0, 0);
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);
        layout.Children.Add(choices);
        dialog.Content = layout;
        await dialog.ShowDialog(this);
    }

    private async Task ShowAdapter(bool includePcm)
    {
        if (port.Text?.Trim() is not { Length: > 0 } device)
        {
            SetStatus("Select the OBDX Pro GT USB or Bluetooth serial port first.", true);
            return;
        }

        GtInspection? inspection = null;
        bool success = await RunHardware(async token =>
        {
            inspection = await HammerdroidProbe.Inspect(device, includePcm, PostStatus, token);
        }, recording: false, completion: "GT inspection complete. Serial port released.");
        if (!success || inspection is null || closeAfterHardware)
            return;

        var details = new StackPanel { Spacing = 8 };
        details.Children.Add(Text(inspection.Adapter.Name, 16, Accent));
        details.Children.Add(Text($"Firmware {inspection.Adapter.Firmware}   ·   Hardware {inspection.Adapter.Hardware}", 13, DeskUi.Ink));
        details.Children.Add(Text("Adapter serial   " + inspection.Adapter.Serial, 13, Muted));
        details.Children.Add(Text($"OBD port voltage   {inspection.Adapter.Voltage:0.00} V", 13, DeskUi.Ink));
        if (inspection.Pcm is { } pcm)
            details.Children.Add(Text("PCM OS ID   " + pcm.OsId, 13, DeskUi.Ink));
        if (inspection.Pcm?.Vin is { } vin)
        {
            details.Children.Add(Text("VIN stored in PCM", 13, DeskUi.Ink));
            details.Children.Add(new SelectableTextBlock
            {
                Text = vin, FontFamily = new FontFamily("DejaVu Sans Mono"),
                FontSize = 17, Foreground = DeskUi.Ink
            });
        }
        else if (inspection.PcmError is { } error)
        {
            details.Children.Add(new TextBlock
            {
                Text = (inspection.Pcm is null ? "PCM identification incomplete. " : "PCM VIN unavailable. ") + error,
                Foreground = Warning, TextWrapping = TextWrapping.Wrap
            });
            SetStatus(inspection.Pcm is null ? "GT responded. PCM identification was incomplete."
                : "PCM OS ID read. VIN unavailable.", true);
        }
        await Modal("Hammerdroid · GT and PCM", details, 520, 300);
    }

    private async Task Record()
    {
        if (port.Text?.Trim() is not { Length: > 0 } device)
        {
            SetStatus("Enter the OBDX Pro GT USB serial path first.", true);
            return;
        }
        string? path = await PickCsvPath("obdx-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        if (path is null)
            return;
        var next = new ScanLog(Path.GetFileName(path));
        string selection = (string?)profile.SelectedItem ?? LiveScanner.Profiles[0];
        await RunHardware(async token =>
        {
            await LiveScanner.Record(device, selection, path, PostStatus,
                reading => Dispatcher.UIThread.Post(() => AppendLive(next, reading)), token);
        }, recording: true);
    }

    private void AppendLive(ScanLog next, LiveReading reading)
    {
        bool first = next.Channels.Count == 0;
        if (first)
        {
            for (int i = 0; i < reading.Columns.Length; i++)
            {
                var column = reading.Columns[i];
                next.Channels.Add(new Channel(i, null, column.Name, column.Unit));
            }
        }
        next.NoteFrame(reading.Time);
        for (int i = 0; i < reading.Values.Length; i++)
            next.Channels[i].Readings.Add(reading.Values[i]);
        if (first)
        {
            cursor = reading.Time;
            live = true;
            SetLog(next);
            SetStatus("Recording · " + next.Name);
        }
    }

    private async Task ReadCodes()
    {
        if (port.Text?.Trim() is not { Length: > 0 } device)
        {
            SetStatus("Enter the OBDX Pro GT USB serial path first.", true);
            return;
        }
        string[] codes = [];
        bool success = await RunHardware(async token =>
        {
            codes = await LiveScanner.ReadCodes(device, PostStatus, token);
        }, recording: false);
        if (success && !closeAfterHardware)
            await Modal("Stored diagnostic trouble codes",
                new TextBlock { Text = codes.Length == 0 ? "The PCM reported no stored codes." : string.Join("   ", codes) });
    }

    private async Task<bool> RunHardware(Func<CancellationToken, Task> operation,
        bool recording, string? completion = null)
    {
        if (hardware is not null || busy || pcmToolsOpen)
            return false;
        using var cancellation = new CancellationTokenSource();
        hardware = cancellation;
        playback.Stop();
        RefreshButtons();
        try
        {
            await Task.Run(() => operation(cancellation.Token));
            SetStatus(completion ?? (recording
                ? "Recording stopped. Received rows are saved to CSV."
                : "Stored-code request complete."));
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Adapter operation stopped.");
            return false;
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
            return false;
        }
        finally
        {
            hardware = null;
            live = false;
            RefreshButtons();
            if (instrumentTabs.SelectedIndex == 1)
                RefreshHistogram();
            if (closeAfterHardware)
                Close();
        }
    }

    private void PostStatus(string text) => Dispatcher.UIThread.Post(() => SetStatus(text));

    private async Task OpenPcmTools()
    {
        if (hardware is not null || busy || pcmToolsOpen)
            return;
        pcmToolsOpen = true;
        RefreshButtons();
        SetStatus("PCM & flashing is open. Close it to resume using the adapter here.");
        try
        {
            var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardError = true };
            start.ArgumentList.Add(PcmLauncher);
            start.ArgumentList.Add("--pcm-only");
            if (port.Text?.Trim() is { Length: > 0 } device)
            {
                start.ArgumentList.Add("--port");
                start.ArgumentList.Add(device);
            }
            using var process = Process.Start(start) ?? throw new IOException("Could not open PCM & flashing.");
            Task<string> errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string error = (await errors).Trim();
            if (process.ExitCode != 0)
                throw new IOException(error.Length > 0 ? error : "PCM & flashing closed with an error.");
            SetStatus("PCM & flashing closed. The scanner can use the adapter.");
        }
        finally
        {
            pcmToolsOpen = false;
            RefreshButtons();
        }
    }

    private async Task ShowWideband()
    {
        if (log is not { } current)
            return;
        var volts = current.Channels.Where(c => c.Unit is "V" or "Volts" &&
            c.Readings.Any(r => r.Number is not null)).ToArray();
        if (volts.Length == 0)
        {
            SetStatus("This log has no voltage channel for wideband AFR.", true);
            return;
        }
        var source = new ComboBox
        {
            ItemsSource = volts.Select(c => c.Label).ToArray(),
            SelectedIndex = Math.Max(0, Array.FindIndex(volts, c => c.Pid == 7101 ||
                c.Name.Contains("Pressure Sensor", StringComparison.OrdinalIgnoreCase))),
            MinWidth = 280
        };
        var slope = new TextBox { Text = "2.0", Width = 74 };
        var offset = new TextBox { Text = "9.57", Width = 74 };
        var message = Text("Match these values to your wideband’s voltage calibration.", 12, Muted);
        message.TextWrapping = TextWrapping.Wrap;
        var dialog = new Window
        {
            Title = "Wideband AFR math channel", Width = 490, Height = 238,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = BackgroundBrush
        };
        var apply = Button("Add AFR channel", () =>
        {
            if (!double.TryParse(slope.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale)
                || !double.TryParse(offset.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double intercept)
                || !double.IsFinite(scale) || !double.IsFinite(intercept) || scale == 0)
            {
                message.Text = "Enter a finite, nonzero slope and a finite offset.";
                message.Foreground = Warning;
                return;
            }
            if (source.SelectedIndex < 0)
                return;
            string name = "Wideband AFR";
            for (int suffix = 2; current.Channels.Any(c => c.Name == name); suffix++)
                name = $"Wideband AFR {suffix}";
            var calculated = new Channel(current.Channels.Max(c => c.Key) + 1, null, name, "AFR");
            foreach (var reading in volts[source.SelectedIndex].Readings)
            {
                if (reading.Number is not double voltage)
                    continue;
                double afr = voltage * scale + intercept;
                if (double.IsFinite(afr))
                    calculated.Readings.Add(new Reading(reading.Time, afr));
            }
            if (calculated.Readings.Count == 0)
            {
                message.Text = "That formula did not produce finite values.";
                message.Foreground = Warning;
                return;
            }
            current.Channels.Add(calculated);
            if (active.Count < 6)
                active.Add(calculated);
            filter.Text = "";
            PopulateChannels();
            SetStatus($"Added {name} using slope {scale:G} and offset {intercept:G}.");
            dialog.Close();
        });
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(15), Spacing = 12,
            Children = { Text("VOLTAGE INPUT", 12, Accent), source,
                Row(Text("AFR = volts ×", 13, Muted), slope, Text("+", 14, Muted), offset),
                message, Row(apply, Button("Cancel", dialog.Close)) }
        };
        await dialog.ShowDialog(this);
    }

    private void ShowHistogram()
    {
        if (instrumentTabs.SelectedIndex == 1)
            RefreshHistogram();
        else
            instrumentTabs.SelectedIndex = 1;
    }

    private void RefreshHistogram()
    {
        void Empty(string message)
        {
            var label = DeskUi.Label(message, 12, Muted);
            label.Margin = new Thickness(8);
            graph.Content = label;
        }
        if (log is null || live)
        {
            Empty(live ? "Stop recording to build the RPM × MAP graph." : "Open a log to build the RPM × MAP graph.");
            return;
        }
        var rpm = EngineSpeed(log);
        var map = ManifoldPressure(log);
        if (rpm is null || map is null)
        {
            Empty("This graph needs Engine RPM and MAP channels.");
            return;
        }

        var measures = log.Channels.Where(c => c != rpm && c != map && c.Readings.Any(r => r.Number is not null)).ToArray();
        if (measures.Length == 0)
        {
            Empty("Open a log with a numeric channel to summarize.");
            return;
        }
        var picker = new ComboBox
        {
            ItemsSource = measures.Select(c => c.Label).ToArray(),
            SelectedIndex = 0, MinWidth = 190
        };
        var table = new ContentControl();
        void Update() => table.Content = Histogram(rpm, map, measures[Math.Max(0, picker.SelectedIndex)]);
        picker.SelectionChanged += (_, _) => Update();
        Update();
        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(5) };
        Put(panel, Row(DeskUi.Section("RPM × MAP"), picker, DeskUi.Label("Mean / count", 11, Muted)), 0);
        Put(panel, new ScrollViewer
        {
            Content = table, Margin = new Thickness(0, 5, 0, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        }, 1);
        graph.Content = panel;
    }

    private static Control Histogram(Channel rpm, Channel map, Channel measure)
    {
        var cells = new Dictionary<(int Rpm, int Map), (double Total, int Count)>();
        foreach (var reading in measure.Readings)
        {
            var r = rpm.AsOf(reading.Time);
            var m = map.AsOf(reading.Time);
            if (reading.Number is not double value || r?.Number is not double speed ||
                m?.Number is not double pressure || reading.Time - r.Time > 500 ||
                reading.Time - m.Time > 500 || speed < 0 || speed >= 12000 || pressure < 0 || pressure >= 400)
                continue;
            var key = ((int)speed / 500 * 500, (int)pressure / 10 * 10);
            var current = cells.GetValueOrDefault(key);
            cells[key] = (current.Total + value, current.Count + 1);
        }
        if (cells.Count == 0)
            return Text("No samples align with RPM and MAP within 500 ms.", 14, Muted);
        var rpms = cells.Keys.Select(key => key.Rpm).Distinct().Order().ToArray();
        var maps = cells.Keys.Select(key => key.Map).Distinct().Order().ToArray();
        var grid = new Grid { ColumnSpacing = 3, RowSpacing = 3 };
        for (int i = 0; i <= rpms.Length; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(76, GridUnitType.Pixel));
        for (int i = 0; i <= maps.Length; i++)
            grid.RowDefinitions.Add(new RowDefinition(38, GridUnitType.Pixel));
        Put(grid, Text("kPa / RPM", 11, Muted), 0, 0);
        for (int col = 0; col < rpms.Length; col++)
            Put(grid, Text(rpms[col].ToString(), 11, Accent), 0, col + 1);
        for (int row = 0; row < maps.Length; row++)
        {
            Put(grid, Text(maps[row].ToString(), 11, Accent), row + 1);
            for (int col = 0; col < rpms.Length; col++)
            {
                if (!cells.TryGetValue((rpms[col], maps[row]), out var cell))
                    continue;
                var contents = Text($"{cell.Total / cell.Count:0.##}\nn={cell.Count}", 11, DeskUi.Ink);
                Put(grid, new Border
                {
                    Background = cell.Count >= 10 ? DeskUi.Edge : DeskUi.Header,
                    CornerRadius = new CornerRadius(1), Padding = new Thickness(5, 2), Child = contents
                }, row + 1, col + 1);
            }
        }
        return grid;
    }

    private async Task Modal(string name, Control content, double width = 480, double height = 170)
    {
        var dialog = new Window
        {
            Title = name, Width = width, Height = height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = BackgroundBrush, ShowInTaskbar = false
        };
        var layout = new DockPanel { Margin = new Thickness(14) };
        var close = Button("Close", dialog.Close);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(close, Dock.Bottom);
        layout.Children.Add(close);
        layout.Children.Add(content);
        dialog.Content = layout;
        await dialog.ShowDialog(this);
    }

    private void SetStatus(string message, bool warning = false)
    {
        status.Text = message;
        status.Foreground = warning ? Warning : Muted;
        ToolTip.SetTip(status, message);
    }

    private void RefreshButtons()
    {
        bool connected = hardware is not null;
        modeText.Text = pcmToolsOpen ? "PCM TOOLS OPEN" : live ? "RECORDING" : connected ? "ADAPTER ACTIVE" : log is null ? "OFFLINE" : "LOG REVIEW";
        modeText.Foreground = live ? Warning : Accent;
        openButton.IsEnabled = !connected && !busy;
        exportButton.IsEnabled = log is not null && !connected && !busy;
        histogramButton.IsEnabled = log is not null && !connected && !busy;
        widebandButton.IsEnabled = log is not null && !connected && !busy;
        bool adapterAvailable = !connected && !busy && !pcmToolsOpen;
        recordButton.IsEnabled = codesButton.IsEnabled = gtButton.IsEnabled = refreshPorts.IsEnabled =
            pcmToolsButton.IsEnabled = adapterAvailable;
        port.IsEnabled = profile.IsEnabled = !connected && !pcmToolsOpen;
        stopButton.IsEnabled = connected;
        slider.IsEnabled = log is not null && !live;
        playButton.IsEnabled = slider.IsEnabled;
    }

    private static Button Button(string label, Action action, string? glyph = null)
    {
        var button = DeskUi.Tool(label, glyph);
        button.Click += (_, _) => action();
        return button;
    }

    private Button Button(string label, Func<Task> action, string? glyph = null)
    {
        var button = DeskUi.Tool(label, glyph);
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) { SetStatus(ex.Message, true); }
        };
        return button;
    }

    private static TextBlock Text(string label, double size, IBrush brush) =>
        new() { Text = label, FontSize = size, Foreground = brush, VerticalAlignment = VerticalAlignment.Center };

    private static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var child in children)
            row.Children.Add(child);
        return row;
    }

    private static void Put(Grid grid, Control child, int row, int column = 0)
    {
        Grid.SetRow(child, row);
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }
}
