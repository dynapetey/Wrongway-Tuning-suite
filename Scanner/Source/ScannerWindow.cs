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

sealed partial class ScannerWindow : Window
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
    private readonly CheckBox recordedOnly = new() { Content = "Recorded only", MinHeight = 22 };
    private readonly CheckBox ecmOnly = new() { Content = "ECM-supported", MinHeight = 22, IsEnabled = false };
    private readonly TextBox port = new() { Width = 170, Watermark = "/dev/ttyUSB0" };
    private readonly ComboBox profile = new() { ItemsSource = LiveScanner.Profiles.Append(LiveScanner.Custom).ToArray(), SelectedIndex = 0, Width = 152 };
    private readonly StackPanel channelList = new() { Spacing = 3 };
    private readonly Slider slider = new() { Minimum = 0, Maximum = 1, IsEnabled = false };
    private readonly TraceChart chart = new() { MinHeight = 170 };
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
    private readonly Button mathsButton;
    private readonly Button telemetryHubButton;
    private readonly Button recordButton;
    private readonly Button codesButton;
    private readonly Button discoverPidsButton;
    private readonly Button gtButton;
    private readonly Button pcmToolsButton;
    private readonly Button refreshPorts;
    private readonly Button stopButton;
    private readonly Button playButton;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Stopwatch playback = new();
    private ScanLog? log;
    private string? logPath;
    private CancellationTokenSource? hardware;
    private bool closeAfterHardware;
    private bool adjustingSlider;
    private bool live;
    private bool busy;
    private bool pcmToolsOpen;
    private uint? lastIdentifiedOs;
    private GtPidDiscovery? pidDiscovery;
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
        status.Text = "Ready · open a compact HP Tuners HPL log, CSV recording, or VCM XML config.";
        status.Foreground = Muted;
        clock.Text = "0.000 s";
        clock.Foreground = Accent;
        clock.FontWeight = FontWeight.SemiBold;
        clock.MinWidth = 96;

        openButton = Button("Open log", PickLog, DeskUi.Open);
        exportButton = Button("Export CSV", Export, DeskUi.Save);
        histogramButton = Button("Graph", ShowHistogram, DeskUi.Chart);
        mathsButton = Button("Maths", EditMaths);
        telemetryHubButton = Button("Telemetry Hub", OpenTelemetryHub);
        var vcmControls = Button("VCM Controls", OpenVcmControls);
        widebandButton = Button("Wideband AFR", async () => await ShowWideband());
        recordButton = Button("Record", Record, DeskUi.Record);
        codesButton = Button("Read DTCs", async () => await ReadCodes());
        discoverPidsButton = Button("Read ECM PIDs", DiscoverPids);
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
            var editor = new TuneWindow(CurrentHistogramSnapshot()) { WindowStartupLocation = WindowStartupLocation.CenterOwner };
            await editor.ShowDialog(this);
        }, DeskUi.Table);
        var gaugeMenu = new MenuItem { Header = "Gauges" };
        gaugeMenu.Click += (_, _) => instrumentTabs.SelectedIndex = 0;
        var editGauges = Button("Edit gauges", EditGauges);
        var editGraph = Button("Edit graph", EditGraph);
        var editTraces = Button("Edit traces", EditTraces);
        var editChannels = Button("Edit display channels", EditDisplayChannels);
        var editLive = Button("Edit live channels", EditLiveChannels);
        var menu = new Menu
        {
            ItemsSource = new[]
            {
                DeskUi.Menu("_File", DeskUi.Action("Open log", openButton), DeskUi.Action("Export CSV", exportButton),
                    DeskUi.Action("Tune BIN", tuneButton)),
                DeskUi.Menu("_Scan", DeskUi.Action("Record", recordButton), DeskUi.Action("Stop", stopButton),
                    DeskUi.Action("Read DTCs", codesButton), DeskUi.Action("Read ECM PIDs", discoverPidsButton),
                    DeskUi.Action("Find ports", refreshPorts)),
                DeskUi.Menu("_View", gaugeMenu, DeskUi.Action("Histogram graph", histogramButton),
                    DeskUi.Action("Fit time chart", zoomOut)),
                DeskUi.Menu("_Layout", DeskUi.Action("Gauges…", editGauges), DeskUi.Action("Histogram graph…", editGraph),
                    DeskUi.Action("Time chart…", editTraces), DeskUi.Action("Display channels…", editChannels),
                    DeskUi.Action("Live channels…", editLive),
                    DeskUi.Action("Import VCM XML…", Button("Import VCM XML", ImportVcmXml)),
                    DeskUi.Action("Save layout as…", Button("Save layout as", SaveLayout)),
                    DeskUi.Action("Load layout…", Button("Load layout", LoadLayout)),
                    DeskUi.Action("Reset to defaults…", Button("Reset layout", ResetLayout))),
                DeskUi.Menu("_Tools", DeskUi.Action("Maths", mathsButton), DeskUi.Action("Wideband AFR", widebandButton),
                    DeskUi.Action("VCM Controls & Special Functions…", vcmControls),
                    DeskUi.Action("Telemetry Hub…", telemetryHubButton),
                    pcmToolsMenu, DeskUi.Action("LM7 to ’95 F-150 wiring guide…",
                        Button("LM7 to ’95 F-150 wiring guide", () => new WiringGuideWindow().ShowDialog(this))))
            }
        };
        var toolbar = Row(openButton, exportButton, recordButton, stopButton, codesButton,
            histogramButton, mathsButton, widebandButton, telemetryHubButton, tuneButton);
        var hardwareRow = Row(DeskUi.Section("OBDX PRO GT"), port, refreshPorts, gtButton, discoverPidsButton,
            DeskUi.Label("Profile", 12, Muted), profile, editLive);

        var sidebar = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        var channelEdit = Button("Edit", EditDisplayChannels);
        Put(sidebar, DeskUi.Strip(Header("Channels", channelEdit), DeskUi.Header), 0);
        sidebar.ContextMenu = new ContextMenu { Items = { DeskUi.Action("Edit display channels…", editChannels), DeskUi.Action("Edit live channels…", editLive) } };
        filter.Margin = new Thickness(5);
        Put(sidebar, filter, 1);
        Put(sidebar, DeskUi.Strip(Row(recordedOnly, ecmOnly)), 2);
        Put(sidebar, new ScrollViewer { Content = channelList,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, 3);
        Put(sidebar, DeskUi.Strip(channelCount), 4);

        var gaugePane = PanelWithEditor(gaugeGrid, editGauges, "Edit gauges…");
        gaugeGrid.Background = DeskUi.ChartBackground;
        var graphPane = PanelWithEditor(graph, editGraph, "Edit graph…");
        instrumentTabs.ItemsSource = new[]
        {
            new TabItem { Header = DeskUi.Section("Gauges"), MinHeight = 30, Padding = new Thickness(9, 3), Content = gaugePane },
            new TabItem { Header = DeskUi.Section("Graph"), MinHeight = 30, Padding = new Thickness(9, 3), Content = graphPane }
        };
        instrumentTabs.SelectedIndex = 0;
        instrumentTabs.SelectionChanged += (_, args) =>
        {
            if (args.Source == instrumentTabs && instrumentTabs.SelectedIndex == 1)
                RefreshHistogram();
        };

        var chartHeader = Header("Chart vs. Time", Row(editTraces, zoomOut));
        chart.ContextMenu = new ContextMenu { Items = { DeskUi.Action("Edit traces…", editTraces) } };
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
        if (File.Exists(ScannerLayout.DefaultPath))
        {
            try { scannerLayout = ScannerLayout.Load(ScannerLayout.DefaultPath); }
            catch (Exception ex) { SetStatus("Could not load saved scanner layout. " + ex.Message, true); }
        }
        if (scannerLayout.LiveChannels is not null)
            profile.SelectedItem = LiveScanner.Custom;
        RebuildGauges();

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
        recordedOnly.IsCheckedChanged += (_, _) => PopulateChannels();
        ecmOnly.IsCheckedChanged += (_, _) => PopulateChannels();
        port.TextChanged += (_, _) => ClearPidDiscovery();
        if (OperatingSystem.IsLinux())
        {
            string[] gtPorts = LiveScanner.Ports().Where(path =>
                Path.GetFileName(path).Contains("OBDX_Pro_GT", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (gtPorts.Length == 1)
                port.Text = gtPorts[0];
        }
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
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var readout = new ScannerReadout(async () => await Dispatcher.UIThread.InvokeAsync(ScannerStatus));
                Closed += (_, _) => readout.Dispose();
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException or ArgumentException)
            {
                SetStatus("Local scanner readout unavailable. " + ex.Message, true);
            }
        }
        PopulateChannels();
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
            var loaded = await Task.Run(() => ScanLog.Load(path));
            logPath = path;
            SetLog(loaded);
            try { VcmTelemetryControls.RegisterSession(loaded, path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus("Log opened. Telemetry session could not be indexed: " + ex.Message, true);
            }
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
        RefreshMaths();
        sessionText.Text = next.ScannerVersion is { } version
            ? $"{next.Name}   ·   VCM Scanner {version}"
            : next.Name;
        ApplyChannelDisplay();
        SelectTraces();
        RebuildGauges();
        chart.Origin = next.Start;
        Seek(next.Start);
        FitView();
        PopulateChannels();
        histogram = null;
        if (instrumentTabs.SelectedIndex == 1)
            RefreshHistogram();
        string summary = $"{next.Frames:N0} frames   ·   {next.Channels.Count} channels   ·   {next.Duration / 1000d:0.0} s";
        if (next.Warnings.Count > 0)
            summary += $"   ·   {next.Warnings[0]}";
        SetStatus(summary, next.Warnings.Count != 0);
        RefreshButtons();
    }

    private void SelectTraces()
    {
        active.Clear();
        traceStyles.Clear();
        if (log is not { } next)
            return;
        if (scannerLayout.Traces is { } traces)
        {
            foreach (var trace in traces)
            {
                if (trace.Source.Resolve(next) is { } channel && traceStyles.TryAdd(channel, trace))
                    active.Add(channel);
            }
            return;
        }
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
    }

    private void PopulateChannels()
    {
        channelList.Children.Clear();
        displayedValues.Clear();
        string search = filter.Text?.Trim() ?? "";
        if (log is { } current)
            AddRecordedChannels(current, search);
        if (recordedOnly.IsChecked == true)
            channelCount.Text = $"{displayedValues.Count} recorded channels";
        else
            AddCatalogChannels(search);
        RefreshChart();
    }

    private void AddRecordedChannels(ScanLog current, string search)
    {
        foreach (var channel in current.Channels)
        {
            if (channel.Display?.Hidden == true ||
                !channel.DisplayLabel.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !channel.Label.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !(channel.Pid?.ToString(CultureInfo.InvariantCulture).Contains(search) ?? false) &&
                !(channel.Pid is { } searchPid && $"0x{searchPid:X4}".Contains(search, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (displayedValues.Count == 0)
                channelList.Children.Add(DeskUi.Strip(DeskUi.Section("Recorded channels")));
            var value = Text("—", 11, Muted);
            value.TextTrimming = TextTrimming.CharacterEllipsis;
            value.MaxWidth = 90;
            value.Margin = new Thickness(3, 0, 6, 0);
            DockPanel.SetDock(value, Dock.Right);
            var name = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = channel.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis,
                    FontSize = 12
                },
                IsChecked = active.Contains(channel),
                IsEnabled = channel.Readings.Any(r => r.Number is not null),
                MinHeight = 24, Padding = new Thickness(4, 0, 0, 0)
            };
            ToolTip.SetTip(name, channel.DisplayLabel + (channel.Pid is { } pid ? $" · PID {pid}" : ""));
            name.IsCheckedChanged += (_, _) =>
            {
                var source = ChannelSource.From(current, channel);
                var traces = CurrentTraces().Where(trace => trace.Source.Resolve(current) != channel).ToList();
                if (name.IsChecked == true)
                {
                    if (traces.Count >= 6)
                    {
                        SetStatus("Select up to six channels for the chart.");
                        Dispatcher.UIThread.Post(PopulateChannels);
                        return;
                    }
                    traces.Add(new TraceSetting(source, NextTraceColor(traces)));
                }
                if (!ApplyLayout(scannerLayout with { Traces = traces.ToArray() }, "Time chart updated."))
                    Dispatcher.UIThread.Post(PopulateChannels);
            };
            var row = new DockPanel { Children = { value, name } };
            channelList.Children.Add(new Border
            {
                Background = displayedValues.Count % 2 == 0 ? DeskUi.Surface : DeskUi.Frame,
                Child = row
            });
            displayedValues[channel] = value;
        }
    }

    private void AddCatalogChannels(string search)
    {
        bool onlySupported = ecmOnly.IsChecked == true;
        var discovery = pidDiscovery;
        channelList.Children.Add(DeskUi.Strip(DeskUi.Section(onlySupported ? "ECM-supported PIDs" : $"PCM OS {liveOs} catalog")));
        var rows = catalog.AsEnumerable();
        if (discovery is not null)
        {
            var known = catalog.Where(channel => channel.Pid.HasValue).Select(channel => channel.Pid!.Value).ToHashSet();
            var unknown = discovery.SaePids.Concat(discovery.GmPids).Where(pid => !known.Contains(pid))
                .Select(pid => new LiveChannelChoice("", "Unmapped ECM PID", [], 0, pid, null));
            rows = rows.Concat(unknown);
            if (onlySupported)
                rows = rows.Where(channel => channel.Pid is uint pid && discovery.Supports(pid));
        }
        var available = rows.ToArray();
        int count = 0;
        foreach (var channel in available.Where(item => item.Matches(search)))
        {
            bool mapped = channel.Bytes > 0;
            var add = Button("+", () => EditLiveChannels(channel));
            add.IsEnabled = mapped;
            Avalonia.Automation.AutomationProperties.SetName(add, $"Add {channel.Name} to recording profile");
            ToolTip.SetTip(add, mapped ? "Add to live recording profile" : "Add a PID definition to record this channel");
            DockPanel.SetDock(add, Dock.Right);
            bool supported = channel.Pid is uint pid && discovery?.Supports(pid) == true;
            var address = Text(channel.AddressLabel, 10, supported ? Accent : Muted);
            address.Margin = new Thickness(3, 0, 6, 0);
            DockPanel.SetDock(address, Dock.Right);
            var name = Text(channel.Name, 12, DeskUi.Ink);
            name.Margin = new Thickness(5, 0, 0, 0);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            string details = supported ? "Confirmed by the ECM in the last scan" : "Catalog definition";
            ToolTip.SetTip(name, mapped ? $"{channel} · {string.Join(", ", channel.Units)} · {details}"
                : "The ECM reported this PID. A decoding definition is required for recording.");
            channelList.Children.Add(new Border
            {
                Background = count++ % 2 == 0 ? DeskUi.Surface : DeskUi.Frame,
                Child = new DockPanel { Children = { add, address, name } }
            });
        }
        channelCount.Text = onlySupported ? $"{count} channels · {discovery?.Count ?? 0} confirmed PIDs"
            : $"{count} of {available.Length} PCM channels · {displayedValues.Count} recorded";
    }

    private void ClearPidDiscovery()
    {
        if (pidDiscovery is null)
            return;
        pidDiscovery = null;
        ecmOnly.IsEnabled = false;
        if (ecmOnly.IsChecked == true)
            ecmOnly.IsChecked = false;
        else
            PopulateChannels();
    }

    private async Task DiscoverPids()
    {
        if (hardware is not null || busy || pcmToolsOpen)
            return;
        if (port.Text?.Trim() is not { Length: > 0 } device)
        {
            SetStatus("Select the OBDX Pro GT USB or Bluetooth serial port first.", true);
            return;
        }

        ClearPidDiscovery();
        GtPidDiscovery? result = null;
        bool success = await RunHardware(async token =>
        {
            result = await HammerdroidProbe.DiscoverPids(device, PostStatus, token);
        }, recording: false, completion: "ECM PID scan complete.");
        if (!success || result is null || closeAfterHardware)
            return;
        if (result.OsId is uint os)
            IdentifyOs(os);
        pidDiscovery = result;
        ecmOnly.IsEnabled = true;
        recordedOnly.IsChecked = false;
        ecmOnly.IsChecked = true;
        string summary = $"ECM scan · {result.SaePids.Length} SAE PIDs · {result.GmPids.Length} of {result.GmProbed} GM PIDs replied.";
        SetStatus(result.Warning is null ? summary : summary + " " + result.Warning, result.Warning is not null);
    }

    private void IdentifyOs(uint os)
    {
        lastIdentifiedOs = os;
        if (liveOs == os)
            return;
        catalog = LiveScanner.Catalog(os);
        liveOs = os;
        ClearPidDiscovery();
        PopulateChannels();
    }

    private object ScannerStatus() => new
    {
        SchemaVersion = 2,
        AppVersion = typeof(Program).Assembly.GetName().Version?.ToString(3),
        CapturedUtc = DateTimeOffset.UtcNow,
        Mode = modeText.Text,
        CatalogOs = liveOs,
        CatalogChannelCount = catalog.Length,
        LastIdentifiedOs = lastIdentifiedOs,
        PidDiscovery = pidDiscovery,
        Profile = profile.SelectedItem as string,
        CustomProfileChannels = profile.SelectedItem as string == LiveScanner.Custom ? scannerLayout.LiveChannels : null,
        Frames = log?.Frames ?? 0,
        CursorMilliseconds = log is null ? (long?)null : cursor - log.Start,
        Channels = log?.Channels.Where(channel => channel.Display?.Hidden != true).Select(channel =>
        {
            var reading = channel.AsOf(cursor);
            return new { channel.Key, channel.Pid, channel.IsMath, Name = channel.DisplayName, Unit = channel.DisplayUnit,
                Value = channel.DisplayNumber(reading),
                SampleMilliseconds = reading is null ? (long?)null : reading.Time - log.Start };
        }).ToArray() ?? [],
        ActiveHistogram = histogram is { TooManyBins: false, Cells.Count: > 0 } current ? new
        {
            XLabel = current.X.DisplayLabel,
            YLabel = current.Y.DisplayLabel,
            ValueLabel = current.Value.DisplayLabel,
            Statistic = current.Settings.Statistic.ToString(),
            current.Settings.MinimumSamples,
            BinCount = current.Cells.Count
        } : null
    };

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
            if (instrumentTabs.SelectedIndex == 1)
                UpdateHistogram();
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
        chart.TraceStyles = traceStyles;
        chart.CursorTime = cursor;
        chart.InvalidateVisual();
        if (log is null)
            return;
        foreach (var (channel, element) in displayedValues)
        {
            var reading = channel.AsOf(cursor);
            element.Text = channel.DisplayNumber(reading) is { } number
                ? $"{channel.FormatDisplay(number)} {channel.DisplayUnit}".TrimEnd() : reading?.Text ?? "—";
            int index = active.IndexOf(channel);
            element.Foreground = index < 0 ? Muted : ChannelInks[traceStyles.GetValueOrDefault(channel)?.Color ?? index];
        }
        foreach (var (gauge, channel) in gauges)
            gauge.Update(channel, cursor);
        clock.Text = $"{(cursor - log.Start) / 1000d:0.000} s";
        playButton.Content = playback.IsRunning ? "Pause" : "Play";
        adjustingSlider = true;
        slider.Maximum = Math.Max(1, log.Duration);
        slider.Value = Math.Max(0, cursor - log.Start);
        adjustingSlider = false;
    }

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
        {
            IdentifyOs(pcm.OsId);
            details.Children.Add(Text("PCM OS ID   " + pcm.OsId, 13, DeskUi.Ink));
        }
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
        var custom = selection == LiveScanner.Custom ? scannerLayout.LiveChannels : null;
        if (selection == LiveScanner.Custom && custom is null)
        {
            SetStatus("Set up a custom profile in Edit live channels first.", true);
            return;
        }
        uint? recordedOs = null;
        await RunHardware(async token =>
        {
            await LiveScanner.Record(device, selection, custom, path, PostStatus,
                os =>
                {
                    recordedOs = os;
                    Dispatcher.UIThread.Post(() => IdentifyOs(os));
                },
                reading => Dispatcher.UIThread.Post(() => AppendLive(next, reading)), token);
        }, recording: true);
        if (next.Frames > 0)
        {
            logPath = path;
            SetLog(next);
            try { VcmTelemetryControls.RegisterSession(next, path, recordedOs); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus("Log recorded. Telemetry session could not be indexed: " + ex.Message, true);
            }
        }
    }

    private void AppendLive(ScanLog next, LiveReading reading)
    {
        bool first = next.Channels.Count == 0;
        if (first)
        {
            for (int i = 0; i < reading.Columns.Length; i++)
            {
                var column = reading.Columns[i];
                next.Channels.Add(new Channel(i, column.Pid, column.Name, column.Unit));
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
        else
        {
            bool addedReading = false;
            foreach (var channel in mathChannels)
            {
                bool empty = channel.Output.Readings.Count == 0;
                channel.Append(reading.Time);
                addedReading |= empty && channel.Output.Readings.Count != 0;
            }
            if (addedReading)
                PopulateChannels();
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
            {
                if (scannerLayout.Traces is null)
                    active.Add(calculated);
                else if (!active.Contains(calculated) && !scannerLayout.Traces.Any(trace => trace.Source.Resolve(current) == calculated)
                    && scannerLayout.Traces.Length < 6)
                {
                    var traces = scannerLayout.Traces.Append(new TraceSetting(ChannelSource.From(current, calculated),
                        NextTraceColor(scannerLayout.Traces))).ToArray();
                    ApplyLayout(scannerLayout with { Traces = traces }, "Added wideband trace.");
                }
            }
            ApplyChannelDisplay();
            RebuildGauges();
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
        histogramButton.IsEnabled = !busy;
        mathsButton.IsEnabled = !connected && !busy;
        widebandButton.IsEnabled = log is not null && !connected && !busy;
        telemetryHubButton.IsEnabled = !busy;
        bool adapterAvailable = !connected && !busy && !pcmToolsOpen;
        recordButton.IsEnabled = codesButton.IsEnabled = discoverPidsButton.IsEnabled = gtButton.IsEnabled = refreshPorts.IsEnabled =
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
