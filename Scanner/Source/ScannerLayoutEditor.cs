using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using static WrongWay.DeskUi;

namespace WrongWay;

sealed partial class ScannerWindow
{
    private static readonly string[] Colors = TraceChart.ColorNames;
    private static readonly FilePickerFileType LayoutFile = new("WrongWay / VCM Scanner layout")
    {
        Patterns = ["*.json", "*.Layout.xml", "*.Graphs.xml", "*.Channels.xml", "*.MathParameter.xml", "*.xml"]
    };
    private static readonly FilePickerFileType VcmXmlFile = new("HP Tuners VCM Scanner XML")
    {
        Patterns = ["*.Layout.xml", "*.Graphs.xml", "*.Channels.xml", "*.MathParameter.xml", "*.xml"]
    };
    private readonly UniformGrid gaugeGrid = new() { MinHeight = 96, Columns = 3 };
    private readonly List<(ScanGauge Gauge, Channel? Channel)> gauges = [];
    private readonly Dictionary<Channel, TraceSetting> traceStyles = [];
    private ScannerLayout scannerLayout = ScannerLayout.Default;
    private uint liveOs = LiveScanner.DefaultOs;
    private LiveChannelChoice[] catalog = LiveScanner.Catalog(LiveScanner.DefaultOs);

    private static DockPanel Header(string name, Control action)
    {
        DockPanel.SetDock(action, Dock.Right);
        return new DockPanel { Children = { action, DeskUi.Section(name) } };
    }

    private static Control PanelWithEditor(Control content, Button editor, string label)
    {
        var dock = new DockPanel
        {
            Background = DeskUi.ChartBackground,
            ContextMenu = new ContextMenu { Items = { DeskUi.Action(label, editor) } }
        };
        editor.HorizontalAlignment = HorizontalAlignment.Right;
        var header = DeskUi.Strip(editor);
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(content is UniformGrid
            ? new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
            : content);
        return dock;
    }

    private void RebuildGauges()
    {
        gauges.Clear();
        gaugeGrid.Children.Clear();
        gaugeGrid.Columns = Math.Clamp(scannerLayout.Gauges.Length, 1, 4);
        foreach (var settings in scannerLayout.Gauges)
        {
            var gauge = new ScanGauge(settings, ChannelInks[settings.Color]) { MinHeight = 96 };
            gauges.Add((gauge, settings.Source.Resolve(log)));
            gaugeGrid.Children.Add(gauge);
        }
        if (gauges.Count == 0)
            gaugeGrid.Children.Add(DeskUi.Label("Use Edit gauges to add an instrument.", 12, Muted));
    }

    private void ApplyChannelDisplay()
    {
        if (log is null)
            return;
        foreach (var channel in log.Channels)
            channel.Display = null;
        foreach (var rule in scannerLayout.Channels)
        {
            if (rule.Source.Resolve(log) is { } channel)
                channel.Display = rule;
        }
    }

    private TraceSetting[] CurrentTraces() => scannerLayout.Traces ??
        (log is { } current ? active.Select((channel, index) =>
            new TraceSetting(ChannelSource.From(current, channel), index)).ToArray() : []);

    private static int NextTraceColor(IEnumerable<TraceSetting> traces)
    {
        var used = traces.Select(trace => trace.Color).ToHashSet();
        return Enumerable.Range(0, Colors.Length).FirstOrDefault(color => !used.Contains(color));
    }

    private bool ApplyLayout(ScannerLayout next, string message, bool selectProfile = false)
    {
        try { next.Save(ScannerLayout.DefaultPath); }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
            return false;
        }
        bool mathsChanged = scannerLayout.Maths != next.Maths;
        scannerLayout = next;
        if (mathsChanged)
            RefreshMaths();
        if (selectProfile)
            profile.SelectedItem = scannerLayout.LiveChannels is null ? LiveScanner.Profiles[0] : LiveScanner.Custom;
        ApplyChannelDisplay();
        SelectTraces();
        RebuildGauges();
        PopulateChannels();
        histogram = null;
        if (instrumentTabs.SelectedIndex == 1)
            RefreshHistogram();
        SetStatus(message);
        return true;
    }

    private async Task SaveLayout()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save scanner layout", SuggestedFileName = "wrongway-scanner-layout",
            DefaultExtension = "json", FileTypeChoices = [new FilePickerFileType("WrongWay scanner layout") { Patterns = ["*.json"] }]
        });
        if (file?.TryGetLocalPath() is { } path)
        {
            // Capture automatically selected traces too when saving a layout for sharing.
            (scannerLayout with { Traces = scannerLayout.Traces ?? (log is null ? null : CurrentTraces()) }).Save(path);
            SetStatus("Saved scanner layout to " + Path.GetFileName(path));
        }
    }

    private async Task LoadLayout()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load scanner layout or HP Tuners VCM XML", FileTypeFilter = [LayoutFile]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            if (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                var updated = VcmConfigImporter.ImportLayoutXml(path, scannerLayout, log, liveOs);
                ApplyLayout(updated, "Imported VCM Scanner XML " + Path.GetFileName(path), selectProfile: updated.LiveChannels != scannerLayout.LiveChannels);
            }
            else
            {
                ApplyLayout(ScannerLayout.Load(path), "Loaded scanner layout " + Path.GetFileName(path), selectProfile: true);
            }
        }
    }

    private async Task ImportVcmXml()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import HP Tuners VCM Scanner XML (Layout, Graphs, Channels, or MathParameter)",
            AllowMultiple = true,
            FileTypeFilter = [VcmXmlFile]
        });
        if (files.Count == 0)
            return;
        var next = scannerLayout;
        bool changedLive = false;
        int importedCount = 0;
        foreach (var item in files)
        {
            if (item.TryGetLocalPath() is not { } path)
                continue;
            var beforeLive = next.LiveChannels;
            next = VcmConfigImporter.ImportLayoutXml(path, next, log, liveOs);
            changedLive |= next.LiveChannels != beforeLive;
            importedCount++;
        }
        if (importedCount > 0)
            ApplyLayout(next, $"Imported {importedCount} VCM Scanner XML file(s).", selectProfile: changedLive);
    }

    private async Task ResetLayout()
    {
        var result = await Form<string>(this, "Reset scanner layout", new StackPanel { Children =
            { DeskUi.Label("Restore the default gauges, graph, traces, and channel settings.") } }, () => "reset");
        if (result is not null)
        {
            selectedGraphIndex = 0;
            ApplyLayout(ScannerLayout.Default, "Scanner layout reset.", selectProfile: true);
        }
    }

    private async Task EditGauges()
    {
        var result = await EditList("Gauges", "Choose up to 12 instruments and put them in display order.",
            scannerLayout.Gauges, 12, (owner, gauge) =>
            {
                var source = SourcePicker(gauge?.Source, ChannelSource.Rpm);
                var label = Field(gauge?.Label ?? "");
                var kind = Choice(Enum.GetValues<GaugeKind>(), (int)(gauge?.Kind ?? GaugeKind.Dial));
                var min = Field(gauge is null ? 0 : ImperialUnits.Range(gauge.Minimum, gauge.Source, gauge.RangeUnit));
                var max = Field(gauge is null ? 100 : ImperialUnits.Range(gauge.Maximum, gauge.Source, gauge.RangeUnit));
                var color = Choice(Colors, gauge?.Color ?? 0);
                return Form(owner, "Gauge settings", Fields(("Channel", source), ("Label (blank uses channel name)", label),
                    ("Style", kind), ("Range minimum", min), ("Range maximum", max), ("Color", color)), () =>
                {
                    var input = SelectedSource(source);
                    var range = ReadRange(min, max);
                    return new GaugeSetting(input, OptionalLabel(label, SourceLabel(input)), (GaugeKind)kind.SelectedIndex,
                        range.Low, range.High, color.SelectedIndex, DisplayUnit(input));
                });
            });
        if (result is not null)
            ApplyLayout(scannerLayout with { Gauges = result }, "Gauge layout saved.");
    }

    private async Task EditTraces()
    {
        var result = await EditList("Time chart traces", $"Add up to {ScannerLayout.MaximumTraces} traces. Scroll to see all lanes; Ctrl+wheel zooms time. Leave both range fields blank for automatic scaling.",
            CurrentTraces(), ScannerLayout.MaximumTraces, (owner, trace) =>
            {
                var source = SourcePicker(trace?.Source);
                var color = Choice(Colors, trace?.Color ?? 0);
                var min = Field(trace?.Minimum is double low ? ImperialUnits.Range(low, trace.Source, trace.RangeUnit) : (double?)null);
                var max = Field(trace?.Maximum is double high ? ImperialUnits.Range(high, trace.Source, trace.RangeUnit) : (double?)null);
                return Form(owner, "Trace settings", Fields(("Channel", source), ("Color", color),
                    ("Range minimum", min), ("Range maximum", max)), () =>
                {
                    var input = SelectedSource(source);
                    if (string.IsNullOrWhiteSpace(min.Text) && string.IsNullOrWhiteSpace(max.Text))
                        return new TraceSetting(input, color.SelectedIndex);
                    var range = ReadRange(min, max);
                    return new TraceSetting(input, color.SelectedIndex, range.Low, range.High, DisplayUnit(input));
                });
            }, items => items.Select(item => item.Source).Distinct().Count() == items.Count ? null : "Each trace needs a different channel.");
        if (result is not null)
            ApplyLayout(scannerLayout with { Traces = result }, "Time chart layout saved.");
    }

    private async Task EditDisplayChannels()
    {
        var result = await EditList("Display channels", "Rename, rescale, change units, or hide a channel in the list. CSV export uses imperial display values.",
            scannerLayout.Channels, 128, (owner, display) =>
            {
                var source = SourcePicker(display?.Source);
                var name = Field(display?.Name ?? "");
                var unit = Field(display?.Unit ?? "");
                var scale = Field(display?.Scale ?? 1);
                var offset = Field(display?.Offset ?? 0);
                var decimals = Field(display?.Decimals ?? 2);
                var hidden = new CheckBox { Content = "Hide in Channels list", IsChecked = display?.Hidden ?? false };
                return Form(owner, "Channel display settings", Fields(("Channel", source), ("Label (blank uses channel name)", name),
                    ("Display units (blank uses source units)", unit), ("Multiply by", scale), ("Then add", offset),
                    ("Decimal places (0–6)", decimals), ("Visibility", hidden)), () =>
                {
                    var input = SelectedSource(source);
                    var factor = Number(scale, "Multiplier");
                    if (factor == 0)
                        throw new FormatException("The multiplier must be nonzero.");
                    return new ChannelDisplay(input, OptionalLabel(name, SourceLabel(input)),
                        OptionalLabel(unit, input.Resolve(log)?.Unit ?? input.Unit),
                        factor, Number(offset, "Offset"), Whole(decimals, "Decimal places", 0, 6), hidden.IsChecked == true);
                });
            }, items => items.Select(item => item.Source.Resolve(log) ?? (object)item.Source).Distinct().Count() == items.Count
                ? null : "Use one display rule per channel.");
        if (result is not null)
            ApplyLayout(scannerLayout with { Channels = result }, "Channel display settings saved.");
    }

    private async Task EditGraph()
    {
        var previous = ActiveGraphSetting();
        var labelBox = Field(previous.Label ?? "Primary Histogram");
        var x = SourcePicker(previous.X.Source);
        var y = SourcePicker(previous.Y.Source);
        var measure = SourcePicker(previous.Value, automatic: true);
        var xWidth = Field(previous.X.Width * previous.X.Conversion.Scale);
        var yWidth = Field(previous.Y.Width * previous.Y.Conversion.Scale);
        var xBins = Field(string.Join(", ", previous.X.Breakpoints.Select(value => previous.X.Conversion.Convert(value).ToString("G", CultureInfo.InvariantCulture))));
        var yBins = Field(string.Join(", ", previous.Y.Breakpoints.Select(value => previous.Y.Conversion.Convert(value).ToString("G", CultureInfo.InvariantCulture))));
        var statistic = Choice(Enum.GetValues<GraphStatistic>(), (int)previous.Statistic);
        var minimum = Field(previous.MinimumSamples);
        var filterBox = Field(previous.FilterExpression ?? "");
        filterBox.Watermark = "Optional VCM filter, e.g. [50090.156]>22 or [CL]=1";
        var result = await Form(this, "Histogram graph settings", Fields(
            ("Table label", labelBox),
            ("X axis (columns)", x), ("X bin width", xWidth),
            ("X breakpoints (optional, comma-separated)", xBins), ("Y axis (rows)", y), ("Y bin width", yWidth),
            ("Y breakpoints (optional, comma-separated)", yBins), ("Value channel", measure),
            ("Cell statistic", statistic), ("Minimum samples per cell", minimum),
            ("Cell filter formula (optional)", filterBox)), () =>
        {
            var xAxis = ReadAxis(x, xWidth, xBins);
            var yAxis = ReadAxis(y, yWidth, yBins);
            if (xAxis.Source == yAxis.Source || xAxis.Source.Resolve(log) is { } xChannel && xChannel == yAxis.Source.Resolve(log))
                throw new FormatException("Choose different channels for the two axes.");
            string? filterText = filterBox.Text?.Trim();
            if (!string.IsNullOrEmpty(filterText))
                _ = new MathFormula(filterText);
            return new GraphSetting(xAxis, yAxis, (measure.SelectedItem as SourceChoice)?.Source,
                (GraphStatistic)statistic.SelectedIndex, Whole(minimum, "Minimum samples", 1, 1_000_000),
                OptionalLabel(labelBox, "Histogram"),
                string.IsNullOrEmpty(filterText) ? null : filterText);
        }, height: 730);
        if (result is not null)
        {
            var graphs = AvailableGraphs().ToArray();
            graphs[Math.Clamp(selectedGraphIndex, 0, graphs.Length - 1)] = result;
            if (ApplyLayout(scannerLayout with { Graph = graphs[0], Graphs = graphs }, "Histogram layout saved."))
                ShowHistogram();
        }
    }

    private Task EditLiveChannels() => EditLiveChannels(null);

    private async Task EditLiveChannels(LiveChannelChoice? add)
    {
        if (hardware is not null || pcmToolsOpen)
        {
            SetStatus("Close the active adapter session to edit recording channels.", true);
            return;
        }

        uint os = liveOs;
        var catalog = this.catalog;
        if (catalog.Length == 0)
            throw new InvalidDataException($"No live channels are available for PCM OS {os}.");
        string selected = (string?)profile.SelectedItem ?? LiveScanner.Profiles[0];
        var seed = selected == LiveScanner.Custom ? scannerLayout.LiveChannels ?? [] : LiveScanner.ProfileSettings(selected, os);
        if (add is not null && !seed.Any(item => item.Id == add.Id))
            seed = [.. seed, new LiveChannelSetting(add.Id, add.Units[0], add.Name)];
        var catalogById = catalog.ToDictionary(item => item.Id);
        var result = await EditList("Live recording channels",
            $"PCM OS {os} · Select PIDs and RAM channels. Above 18 bytes uses slower polled acquisition. Each uses 1 or 2 bytes of the {LiveScanner.MaximumLiveBytes}-byte scanner capacity.",
            seed, LiveScanner.MaximumLiveBytes, (owner, channel) =>
            {
                var search = new TextBox { Watermark = "Find a name, PID or RAM address" };
                int selectedChannel = Array.FindIndex(catalog, item => item.Id == channel?.Id);
                var parameter = Choice(catalog, selectedChannel);
                var units = Choice(Array.Empty<string>(), -1);
                var label = Field(channel?.Label ?? "");
                void UpdateUnits()
                {
                    var offered = (parameter.SelectedItem as LiveChannelChoice)?.Units ?? [];
                    units.ItemsSource = offered.Select(u => new RecordingUnit(u)).ToArray();
                    int index = Array.IndexOf(offered, channel?.Unit);
                    units.SelectedIndex = index >= 0 ? index : offered.Length > 0 ? 0 : -1;
                }
                search.TextChanged += (_, _) =>
                {
                    var last = parameter.SelectedItem as LiveChannelChoice;
                    string query = search.Text?.Trim() ?? "";
                    var filtered = catalog.Where(item => item.Matches(query)).ToArray();
                    parameter.ItemsSource = filtered;
                    int index = Array.IndexOf(filtered, last);
                    parameter.SelectedIndex = index >= 0 ? index : filtered.Length > 0 ? 0 : -1;
                };
                parameter.SelectionChanged += (_, _) => UpdateUnits();
                UpdateUnits();
                return Form(owner, "Live channel settings", Fields(("Search", search), ("PCM channel", parameter),
                    ("Units", units), ("Label (blank uses catalog name)", label)), () =>
                {
                    if (parameter.SelectedItem is not LiveChannelChoice choice || units.SelectedItem is not RecordingUnit unit)
                        throw new FormatException("Choose a PCM channel and its recording units.");
                    return new LiveChannelSetting(choice.Id, unit.Source, OptionalLabel(label, choice.Name));
                });
            }, items => items.Count == 0 ? "Add at least one live channel." :
                items.Select(item => item.Id).Distinct().Count() != items.Count ? "Choose each PCM channel once." :
                items.Any(item => !catalogById.TryGetValue(item.Id, out var choice) || !choice.Units.Contains(item.Unit))
                    ? $"Edit or remove channels unavailable for OS {os}." :
                items.Sum(item => catalogById[item.Id].Bytes) > LiveScanner.MaximumLiveBytes
                    ? $"Remove channels to fit the {LiveScanner.MaximumLiveBytes}-byte scanner capacity." : null);
        if (result is not null)
            ApplyLayout(scannerLayout with { LiveChannels = result }, "Custom live recording profile saved.", selectProfile: true);
    }

    private sealed record SourceChoice(ChannelSource? Source, string Label)
    {
        public override string ToString() => Label;
    }

    private ComboBox SourcePicker(ChannelSource? selected, ChannelSource? fallback = null, bool automatic = false)
    {
        var sources = new List<SourceChoice>();
        if (automatic)
            sources.Add(new(null, "Automatic value channel"));
        sources.AddRange(new[] { ChannelSource.Rpm, ChannelSource.Map, ChannelSource.Throttle }
            .Select(source => new SourceChoice(source, source.ToString())));
        if (log is { } current)
            sources.AddRange(current.Channels.Select(channel =>
            {
                var source = ChannelSource.From(current, channel);
                return new SourceChoice(source, channel.DisplayLabel + (source.Occurrence == 0 ? "" : $" #{source.Occurrence + 1}"));
            }));
        if (scannerLayout.LiveChannels is { } liveChannels)
        {
            foreach (var item in liveChannels)
            {
                var source = new ChannelSource(item.Label, item.Unit);
                if (sources.All(choice => choice.Source != source))
                    sources.Add(new SourceChoice(source, source.ToString()));
            }
        }
        foreach (var math in (scannerLayout.Maths ?? MathSetting.Defaults).Where(math => math.Enabled))
        {
            var source = new ChannelSource(math.Name, math.Unit, MathId: math.Id);
            if (sources.All(choice => choice.Source != source))
                sources.Add(new SourceChoice(source, source.ToString()));
        }
        bool Equivalent(ChannelSource? actual, ChannelSource? expected) =>
            actual == expected || actual?.MathId is { } id && expected?.MathId == id;
        if (selected is not null && sources.All(choice => !Equivalent(choice.Source, selected)))
            sources.Add(new SourceChoice(selected, $"{selected}  ·  unavailable in this log"));
        int index = sources.FindIndex(choice => Equivalent(choice.Source, selected ?? fallback));
        return Choice(sources, index >= 0 ? index : sources.Count > 3 ? 3 : 0);
    }

    private string DisplayUnit(ChannelSource source) => source.Resolve(log)?.DisplayUnit ?? ImperialUnits.For(ImperialUnits.SourceUnit(source)).Unit;

    private string SourceLabel(ChannelSource source) => source.Resolve(log)?.Name ?? source.ToString();
    private static ChannelSource SelectedSource(ComboBox box) => (box.SelectedItem as SourceChoice)?.Source
        ?? throw new FormatException("Choose a channel.");

    private static ComboBox Choice<T>(IEnumerable<T> choices, int selected) =>
        new() { ItemsSource = choices, SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 300 };

    private static TextBox Field(object? value) =>
        new() { Text = Convert.ToString(value, CultureInfo.InvariantCulture), MaxLength = 800 };

    private static string OptionalLabel(TextBox box, string fallback)
    {
        string value = box.Text?.Trim() ?? "";
        return value.Length == 0 ? fallback : value;
    }

    private static double Number(TextBox box, string label)
    {
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            throw new FormatException($"{label} needs a finite number.");
        return number;
    }

    private static int Whole(TextBox box, string label, int min, int max)
    {
        if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new FormatException($"{label} needs a whole number from {min} to {max}.");
        return value;
    }

    private static (double Low, double High) ReadRange(TextBox minimum, TextBox maximum)
    {
        double low = Number(minimum, "Minimum");
        double high = Number(maximum, "Maximum");
        if (high <= low || !double.IsFinite(high - low))
            throw new FormatException("Maximum must be greater than minimum.");
        return (low, high);
    }

    private GraphAxis ReadAxis(ComboBox source, TextBox width, TextBox breaks)
    {
        string[] tokens = (breaks.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length > 120)
            throw new FormatException("Use at most 120 breakpoints per axis.");
        var points = tokens.Select(token =>
        {
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                throw new FormatException("Enter breakpoints as comma-separated numbers.");
            return value;
        }).Order().ToArray();
        if (points.Distinct().Count() != points.Length)
            throw new FormatException("Each breakpoint must be unique.");
        double step = points.Length == 0 ? Number(width, "Bin width") : 1;
        if (step <= 0)
            throw new FormatException("Bin width must be positive.");
        var input = SelectedSource(source);
        return new GraphAxis(input, step, points, DisplayUnit(input));
    }

    private Task<T[]?> EditList<T>(string title, string description, T[] initial, int maximum,
        Func<Window, T?, Task<T?>> edit, Func<List<T>, string?>? validate = null) where T : class =>
        DeskUi.EditList(this, title, description, initial, maximum, edit, validate);
}
