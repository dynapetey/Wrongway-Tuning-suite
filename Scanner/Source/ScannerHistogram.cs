using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace WrongWay;

sealed record HistogramSnapshot(string XLabel, string YLabel, string ValueLabel,
    GraphStatistic Statistic, int MinimumSamples, IReadOnlyDictionary<(double X, double Y), HistogramSamples.Cell> Cells,
    double[]? XBins = null, double[]? YBins = null, bool SingleRow = false, string XUnit = "", string YUnit = "",
    IReadOnlySet<(double X, double Y)>? Selection = null, bool Smooth = false)
{
    private readonly double[] xs = XBins ?? Cells.Keys.Select(key => key.X).Distinct().Order().ToArray();
    private readonly double[] ys = YBins ?? Cells.Keys.Select(key => key.Y).Distinct().Order().ToArray();

    internal static double[] ParseAxis(IEnumerable<string> labels) => labels.Select(label =>
        double.TryParse(label, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) &&
        double.IsFinite(value) ? value : throw new FormatException($"Axis label '{label}' is not numeric.")).ToArray();

    internal static ImperialConversion MatchUnits(string tableUnit, string histogramUnit)
    {
        var conversion = ImperialUnits.For(tableUnit);
        if (tableUnit.Length > 0 && histogramUnit.Length > 0 &&
            !conversion.Unit.Equals(histogramUnit, StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"The table axis uses {tableUnit} but the histogram uses {histogramUnit}.");
        return conversion;
    }

    internal string CopyValues() => string.Join(Environment.NewLine, ys.Select(y => string.Join("\t",
        xs.Select(x => CellValue(x, y, MinimumSamples)?.ToString("G17", System.Globalization.CultureInfo.InvariantCulture) ?? ""))));

    private double? CellValue(double x, double y, int minSamples)
    {
        double? Lookup(double column, double row) =>
            (Selection is null || Selection.Contains((column, row))) &&
            Cells.TryGetValue((column, row), out var sample) && sample.Count >= minSamples
                ? sample.Statistic(Statistic) : null;
        double? value = Lookup(x, y);
        if (!Smooth || value is null)
            return value;

        int column = Array.BinarySearch(xs, x);
        int row = Array.BinarySearch(ys, y);
        double sum = 0;
        int count = 0;
        for (int yi = Math.Max(0, row - 1); yi <= Math.Min(ys.Length - 1, row + 1); yi++)
        for (int xi = Math.Max(0, column - 1); xi <= Math.Min(xs.Length - 1, column + 1); xi++)
            if (Lookup(xs[xi], ys[yi]) is double neighbor)
            {
                count++;
                sum += (neighbor - sum) / count;
            }
        return sum;
    }

    public double? Sample(double targetX, double targetY, int minSamples, bool bilinear)
    {
        if (SingleRow)
            targetY = 0;
        if (xs.Length == 0 || ys.Length == 0 || !double.IsFinite(targetX) || !double.IsFinite(targetY) ||
            targetX < xs[0] || targetX > xs[^1] || targetY < ys[0] || targetY > ys[^1])
            return null;

        if (!bilinear)
            return CellValue(xs.MinBy(x => Math.Abs(x - targetX)), ys.MinBy(y => Math.Abs(y - targetY)), minSamples);

        (int x0, int x1) = Neighbors(xs, targetX);
        (int y0, int y1) = Neighbors(ys, targetY);

        double? v00 = CellValue(xs[x0], ys[y0], minSamples);
        double? v10 = CellValue(xs[x1], ys[y0], minSamples);
        double? v01 = CellValue(xs[x0], ys[y1], minSamples);
        double? v11 = CellValue(xs[x1], ys[y1], minSamples);
        if (v00 is null || v10 is null || v01 is null || v11 is null)
            return CellValue(xs.MinBy(x => Math.Abs(x - targetX)), ys.MinBy(y => Math.Abs(y - targetY)), minSamples);

        double tx = xs[x1] == xs[x0] ? 0 : (targetX - xs[x0]) / (xs[x1] - xs[x0]);
        double ty = ys[y1] == ys[y0] ? 0 : (targetY - ys[y0]) / (ys[y1] - ys[y0]);
        double a = v00.Value * (1 - tx) + v10.Value * tx;
        double b = v01.Value * (1 - tx) + v11.Value * tx;
        return a * (1 - ty) + b * ty;
    }

    private static (int Lower, int Upper) Neighbors(double[] values, double target)
    {
        int index = Array.BinarySearch(values, target);
        return index >= 0 ? (index, index) : (~index - 1, ~index);
    }

    public static void SelfTest()
    {
        var cells = new Dictionary<(double X, double Y), HistogramSamples.Cell>
        {
            [(1000, 40)] = new HistogramSamples.Cell().Add(0),
            [(1000, 60)] = new HistogramSamples.Cell().Add(10),
            [(2000, 40)] = new HistogramSamples.Cell().Add(20),
            [(2000, 60)] = new HistogramSamples.Cell().Add(30)
        };
        var snapshot = new HistogramSnapshot("RPM", "kPa", "Correction", GraphStatistic.Mean, 1, cells);
        var stats = new HistogramSamples.Cell().Add(2).Add(4, false, 125).Add(6);
        if (stats.Count != 3 || stats.Visits != 2 || stats.DurationMs != 125 || stats.Last != 6 ||
            Math.Abs(stats.Statistic(GraphStatistic.StandardDeviation) - Math.Sqrt(8d / 3)) > 1e-12)
            throw new InvalidDataException("Histogram cell statistics failed.");
        var selection = new HashSet<(double X, double Y)> { (1000, 40), (2000, 40) };
        var smoothed = snapshot with { Selection = selection, Smooth = true };
        if (smoothed.Sample(1000, 40, 1, false) != 10 || smoothed.Sample(1000, 60, 1, false) is not null)
            throw new InvalidDataException("Histogram selection or selective smoothing failed.");
        if (snapshot.Sample(1500, 50, 1, true) != 15 ||
            snapshot.Sample(1000, 40, 1, true) != 0 ||
            snapshot.Sample(3000, 50, 1, false) is not null ||
            snapshot.Sample(1500, 50, 2, true) is not null)
            throw new InvalidDataException("Histogram correction sampling failed.");
    }
}

sealed partial class ScannerWindow
{
    private HistogramSamples? histogram;
    private int selectedGraphIndex;
    private long histogramRefresh;
    private bool replayHistogram, smoothHistogram;
    private ComboBox? histogramPicker;
    private long histogramThrough = long.MinValue;
    private readonly Dictionary<int, HashSet<(double X, double Y)>> histogramSelections = [];
    private readonly HashSet<int> histogramSurfaces = [];

    private HashSet<(double X, double Y)> HistogramSelection(int index)
    {
        if (!histogramSelections.TryGetValue(index, out var selection))
            histogramSelections[index] = selection = [];
        return selection;
    }

    internal HistogramSnapshot? CurrentHistogramSnapshot()
    {
        if (histogram is not { TooManyBins: false, Cells.Count: > 0 } current)
            return null;
        return new HistogramSnapshot(
            current.X.DisplayLabel, current.YLabel, current.Value.DisplayLabel,
            current.Settings.Statistic, current.Settings.MinimumSamples,
            new Dictionary<(double X, double Y), HistogramSamples.Cell>(current.Cells),
            current.AxisBins(true), current.AxisBins(false), current.Settings.Y.Source == ChannelSource.SingleRow,
            current.X.DisplayUnit, current.Y.DisplayUnit,
            HistogramSelection(selectedGraphIndex) is { Count: > 0 } selection ? selection.ToHashSet() : null, smoothHistogram);
    }

    internal void ImportHistogramAxes(TuneParameter table, bool swap)
    {
        var graph = ActiveGraphSetting();
        var current = histogram;
        string xUnit = current?.X.DisplayUnit ?? graph.X.Conversion.Unit;
        string yUnit = current?.Y.DisplayUnit ?? graph.Y.Conversion.Unit;
        bool single = graph.Y.Source == ChannelSource.SingleRow;
        if (single && table.Rows > 1 && table.Columns > 1)
            throw new FormatException("Choose a one-dimensional calibration table for this histogram.");
        if (single && (swap ? table.Rows : table.Columns) < 2)
            throw new FormatException("Match the histogram to the table axis that contains the breakpoints.");
        GraphAxis Read(GraphAxis axis, string[] labels, string tableUnit, string histogramUnit)
        {
            var conversion = HistogramSnapshot.MatchUnits(tableUnit, histogramUnit);
            var points = HistogramSnapshot.ParseAxis(labels).Order().ToArray();
            if (points.Length is < 1 or > 120 || points.Distinct().Count() != points.Length)
                throw new FormatException("Calibration axes need 1 to 120 unique breakpoints.");
            return axis with { Breakpoints = points, BinUnit = tableUnit.Length == 0 ? histogramUnit : tableUnit };
        }

        var x = Read(graph.X, swap ? table.RowLabels : table.ColumnLabels,
            swap ? table.RowUnits : table.ColumnUnits, xUnit);
        var y = single ? graph.Y : Read(graph.Y, swap ? table.ColumnLabels : table.RowLabels,
            swap ? table.ColumnUnits : table.RowUnits, yUnit);
        var graphs = AvailableGraphs().ToArray();
        graphs[selectedGraphIndex] = graph with { X = x, Y = y };
        if (!ApplyLayout(scannerLayout with { Graph = graphs[0], Graphs = graphs }, $"Imported {table.Title} breakpoints."))
            throw new FormatException("The scanner could not save the calibration breakpoints.");
    }

    private GraphSetting[] AvailableGraphs() =>
        scannerLayout.Graphs is { Length: > 0 } graphs ? graphs : [scannerLayout.Graph];

    private GraphSetting ActiveGraphSetting()
    {
        var graphs = AvailableGraphs();
        selectedGraphIndex = Math.Clamp(selectedGraphIndex, 0, graphs.Length - 1);
        return graphs[selectedGraphIndex];
    }

    private readonly List<(HistogramSamples Samples, ContentControl Table, TextBlock Status, int Index)> histogramPanels = new();

    private void RefreshHistogram()
    {
        histogram = null;
        histogramPanels.Clear();
        if (log is null)
        {
            graph.Content = DeskUi.Label("Open a log or record live data to build histograms.");
            return;
        }
        var graphs = AvailableGraphs();
        selectedGraphIndex = Math.Clamp(selectedGraphIndex, 0, graphs.Length - 1);
        var panels = new StackPanel { Margin = new Thickness(5), Spacing = 12 };
        var picker = new ComboBox
        {
            ItemsSource = graphs.Select((g, i) => string.IsNullOrWhiteSpace(g.Label) ? $"Table {i + 1}" : g.Label).ToArray(),
            SelectedIndex = selectedGraphIndex,
            MinWidth = 180
        };
        histogramPicker = picker;
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedIndex < 0) return;
            selectedGraphIndex = picker.SelectedIndex;
            histogram = histogramPanels.FirstOrDefault(p => p.Index == selectedGraphIndex).Samples;
        };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolbar.Children.Add(DeskUi.Label("Active table:", 11, Muted));
        toolbar.Children.Add(picker);
        var replay = new CheckBox { Content = "Follow replay cursor", IsChecked = replayHistogram, IsEnabled = !live };
        replay.IsCheckedChanged += (_, _) =>
        {
            replayHistogram = replay.IsChecked == true;
            ResetHistogramSamples();
            UpdateHistogram(force: true);
        };
        var smooth = new CheckBox { Content = "Smooth calibration transfer", IsChecked = smoothHistogram };
        smooth.IsCheckedChanged += (_, _) => smoothHistogram = smooth.IsChecked == true;
        toolbar.Children.Add(replay);
        toolbar.Children.Add(smooth);
        toolbar.Children.Add(Button("Copy values", async () =>
        {
            if (CurrentHistogramSnapshot() is { } snapshot && Clipboard is { } clipboard)
            {
                await clipboard.SetValueAsync(DataFormat.Text, snapshot.CopyValues());
                SetStatus("Copied the active histogram values.");
            }
        }));
        toolbar.Children.Add(Button("Clear selection", () =>
        {
            HistogramSelection(selectedGraphIndex).Clear();
            UpdateHistogram(force: true);
        }));
        panels.Children.Add(toolbar);
        for (int i = 0; i < graphs.Length; i++)
        {
            var settings = graphs[i];
            var x = settings.X.Source.Resolve(log);
            var y = settings.Y.Source == ChannelSource.SingleRow ? x : settings.Y.Source.Resolve(log);
            var title = string.IsNullOrWhiteSpace(settings.Label) ? $"Table {i + 1}" : settings.Label;
            if (x is null || y is null || (x == y && settings.Y.Source != ChannelSource.SingleRow))
            {
                string problem = x is null ? $"Missing X channel ({settings.X.Source})"
                    : y is null ? $"Missing Y channel ({settings.Y.Source})" : "The two axes resolve to the same channel";
                panels.Children.Add(DeskUi.Label($"{title}: {problem}.", 12, Muted));
                continue;
            }
            var value = settings.Value is { } input ? input.Resolve(log) : log.Channels.FirstOrDefault(channel =>
                channel != x && channel != y && channel.Readings.Any(reading => reading.Number.HasValue));
            if (settings.Statistic != GraphStatistic.Count && value is null)
            {
                panels.Children.Add(DeskUi.Label($"{title}: Missing value channel ({settings.Value?.ToString() ?? "automatic"}).", 12, Muted));
                continue;
            }
            var source = settings.Statistic == GraphStatistic.Count ? x : value!;
            var missing = MathChannel.MissingFilterChannels(log, settings.FilterExpression);
            if (missing.Length > 0)
            {
                panels.Children.Add(DeskUi.Label($"{title}: Missing filter channels: {string.Join(", ", missing)}.", 12, Muted));
                continue;
            }
            var delayed = settings.ValueDelayMs > 0 && ScannerTuningPresets.Maths.Any(preset =>
                preset.Name == source.Name && (scannerLayout.Maths ?? MathSetting.Defaults).Contains(preset))
                ? ScannerTuningPresets.DelayedWideband(log, source) : null;
            var samples = new HistogramSamples(settings, x, y, source,
                MathChannel.BuildFilter(log, settings.FilterExpression), delayed?.Clock, delayed?.Calculate);
            var table = new ContentControl();
            var status = DeskUi.Label("", 12, Muted);
            var section = new StackPanel { Spacing = 4 };
            section.Children.Add(DeskUi.Section($"{title} · {x.DisplayLabel} × {samples.YLabel} · {settings.Statistic}"));
            int index = i;
            var threeD = new CheckBox { Content = "3D surface", IsChecked = histogramSurfaces.Contains(index) };
            threeD.IsCheckedChanged += (_, _) =>
            {
                if (threeD.IsChecked == true) histogramSurfaces.Add(index);
                else histogramSurfaces.Remove(index);
                UpdateHistogram(force: true);
            };
            section.Children.Add(threeD);
            section.Children.Add(status);
            section.Children.Add(table);
            panels.Children.Add(section);
            histogramPanels.Add((samples, table, status, i));
            if (i == selectedGraphIndex)
            {
                histogram = samples;
            }
        }
        graph.Content = new ScrollViewer
        {
            Content = panels,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        UpdateHistogram(force: true);
    }

    private void UpdateHistogram(bool force = false)
    {
        if (histogramPanels.Count == 0 || (!force && Environment.TickCount64 - histogramRefresh < 1000))
            return;
        histogramRefresh = Environment.TickCount64;
        long through = replayHistogram && !live ? cursor : long.MaxValue;
        if (through < histogramThrough)
        {
            ResetHistogramSamples();
            force = true;
        }
        histogramThrough = through;
        foreach (var panel in histogramPanels)
        {
            var samples = panel.Samples;
            if (!samples.AddSamples(through) && !force) continue;
            panel.Status.Text = samples.TooManyBins
                ? "An axis exceeds 120 bins. Increase bin width or enter breakpoints."
                : samples.Cells.Count == 0
                    ? "No aligned numeric samples available."
                    : $"Columns: {samples.X.DisplayLabel} · Rows: {samples.YLabel}";
            if (samples.FilteredOut > 0)
                panel.Status.Text += $" · Filtered out: {samples.FilteredOut:N0}";
            panel.Table.Content = samples.TooManyBins || samples.Cells.Count == 0 ? null : histogramSurfaces.Contains(panel.Index)
                ? DrawHistogramSurface(samples, panel.Table.Content as TunePlot) : DrawHistogram(samples, panel.Index);
        }
    }

    private void ResetHistogramSamples()
    {
        histogramThrough = long.MinValue;
        foreach (var panel in histogramPanels)
            panel.Samples.Clear();
    }

    private static Control DrawHistogramSurface(HistogramSamples data, TunePlot? existing)
    {
        var xs = data.AxisBins(true);
        var ys = data.AxisBins(false);
        var values = new double[ys.Length, xs.Length];
        var range = data.CellRange();
        for (int row = 0; row < ys.Length; row++)
        for (int column = 0; column < xs.Length; column++)
            values[row, column] = data.Cells.TryGetValue((xs[column], ys[row]), out var cell) &&
                cell.Count >= data.Settings.MinimumSamples ? cell.Statistic(data.Settings.Statistic) : double.NaN;
        var plot = existing ?? new TunePlot(surface: true) { Height = 325, MinWidth = 400 };
        plot.SetSeries(values, range.Low, range.High, -1, -1, data.Value.DisplayUnit, data.X.DisplayLabel, data.YLabel);
        return plot;
    }

    private Control DrawHistogram(HistogramSamples data, int index)
    {
        var xs = data.AxisBins(true);
        var ys = data.AxisBins(false);
        var selection = HistogramSelection(index);
        var (low, high) = data.CellRange();
        var lowColor = Color.FromUInt32(data.Settings.LowColor);
        var highColor = Color.FromUInt32(data.Settings.HighColor);
        var grid = new Grid { ColumnSpacing = 3, RowSpacing = 3 };
        for (int i = 0; i <= xs.Length; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(90, GridUnitType.Pixel));
        for (int i = 0; i <= ys.Length; i++)
            grid.RowDefinitions.Add(new RowDefinition(38, GridUnitType.Pixel));
        Put(grid, Text("Y / X", 11, Muted), 0);
        for (int col = 0; col < xs.Length; col++)
            Put(grid, Text(xs[col].ToString("G6"), 11, Accent), 0, col + 1);
        for (int row = 0; row < ys.Length; row++)
        {
            Put(grid, Text(ys[row].ToString("G6"), 11, Accent), row + 1);
            for (int col = 0; col < xs.Length; col++)
            {
                if (!data.Cells.TryGetValue((xs[col], ys[row]), out var cell) || cell.Count < data.Settings.MinimumSamples)
                    continue;
                double amount = cell.Statistic(data.Settings.Statistic);
                string label = data.Settings.Statistic is GraphStatistic.Count or GraphStatistic.Visits
                    ? amount.ToString("N0") : data.Value.FormatDisplay(amount);
                if (data.Settings.Statistic != GraphStatistic.Count)
                    label += $"\nn={cell.Count:N0}";
                double t = high > low ? Math.Clamp((amount - low) / (high - low), 0, 1) : 0.5;
                byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * t);
                var key = (xs[col], ys[row]);
                var cellButton = new Button
                {
                    Background = new SolidColorBrush(Color.FromArgb(Mix(lowColor.A, highColor.A), Mix(lowColor.R, highColor.R),
                        Mix(lowColor.G, highColor.G), Mix(lowColor.B, highColor.B))),
                    BorderBrush = selection.Contains(key) ? Brushes.White : Brushes.Transparent,
                    BorderThickness = new Thickness(2), Padding = new Thickness(5, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
                    Content = Text(label, 11, DeskUi.Ink)
                };
                ToolTip.SetTip(cellButton, "Select cell · Ctrl+click to toggle multiple cells");
                cellButton.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
                {
                    if (!e.GetCurrentPoint(cellButton).Properties.IsLeftButtonPressed)
                        return;
                    selectedGraphIndex = index;
                    histogram = data;
                    bool wasSelected = selection.Contains(key);
                    bool multi = e.KeyModifiers.HasFlag(KeyModifiers.Control);
                    if (!multi)
                        selection.Clear();
                    if (!wasSelected || !multi)
                        selection.Add(key);
                    else
                        selection.Remove(key);
                    if (histogramPicker is not null)
                        histogramPicker.SelectedIndex = index;
                    UpdateHistogram(force: true);
                    e.Handled = true;
                }, RoutingStrategies.Tunnel);
                Put(grid, cellButton, row + 1, col + 1);
            }
        }
        return grid;
    }
}

sealed class HistogramSamples(GraphSetting settings, Channel x, Channel y, Channel value, Func<long, bool>? filter = null,
    Channel? sampleClock = null, Func<Reading, long, double?>? calculate = null)
{
    public readonly record struct Cell(double Mean, double Minimum, double Maximum, int Count,
        double Last = 0, double SumSquaredDeviation = 0, int Visits = 0, long DurationMs = 0)
    {
        public Cell Add(double sample, bool newVisit = true, long elapsedMs = 0)
        {
            if (Count == 0)
                return new(sample, sample, sample, 1, sample, 0, 1);
            double mean = Mean * (Count / (Count + 1d)) + sample / (Count + 1d);
            return new(mean, Math.Min(Minimum, sample), Math.Max(Maximum, sample), Count + 1, sample,
                SumSquaredDeviation + (sample - Mean) * (sample - mean),
                Visits + (newVisit ? 1 : 0), DurationMs + elapsedMs);
        }

        public double Statistic(GraphStatistic statistic) => statistic switch
        {
            GraphStatistic.Count => Count,
            GraphStatistic.Minimum => Minimum,
            GraphStatistic.Maximum => Maximum,
            GraphStatistic.Last => Last,
            GraphStatistic.StandardDeviation => Math.Sqrt(Math.Max(0, SumSquaredDeviation / Count)),
            GraphStatistic.Visits => Visits,
            GraphStatistic.Duration => DurationMs / 1000d,
            _ => Mean
        };
    }

    public GraphSetting Settings => settings;
    public Channel X => x;
    public Channel Y => y;
    public string YLabel => settings.Y.Source == ChannelSource.SingleRow ? "Single row" : y.DisplayLabel;
    public Channel Value => value;
    public Dictionary<(double X, double Y), Cell> Cells { get; } = [];
    public bool TooManyBins { get; private set; }
    public int FilteredOut { get; private set; }
    private readonly HashSet<double> xBins = [];
    private readonly HashSet<double> yBins = [];
    private int next;
    private (double X, double Y)? previousKey;
    private long previousTime;

    public (double Low, double High) CellRange()
    {
        var values = Cells.Values.Where(cell => cell.Count >= settings.MinimumSamples)
            .Select(cell => cell.Statistic(settings.Statistic)).ToArray();
        return (settings.HeatmapMin ?? values.DefaultIfEmpty(0).Min(),
            settings.HeatmapMax ?? values.DefaultIfEmpty(0).Max());
    }

    public double[] AxisBins(bool horizontal)
    {
        var axis = horizontal ? settings.X : settings.Y;
        return axis.Breakpoints.Length > 0
            ? axis.Breakpoints.Select(axis.Conversion.Convert).ToArray()
            : (horizontal ? xBins : yBins).Order().ToArray();
    }

    public void Clear()
    {
        Cells.Clear();
        xBins.Clear();
        yBins.Clear();
        next = 0;
        previousKey = null;
        TooManyBins = false;
        FilteredOut = 0;
    }

    public bool AddSamples(long through = long.MaxValue)
    {
        int first = next;
        var readings = (sampleClock ?? value).Readings;
        while (!TooManyBins && next < readings.Count && readings[next].Time <= through)
        {
            var sample = readings[next++];
            long time = sample.Time - (settings.Statistic == GraphStatistic.Count ? 0 : settings.ValueDelayMs);
            var rx = x.AsOf(time);
            bool singleRow = settings.Y.Source == ChannelSource.SingleRow;
            var ry = singleRow ? rx : y.AsOf(time);
            double? reading = calculate is null ? value.DisplayNumber(sample) : calculate(sample, time);
            if (reading is not double amount || x.DisplayNumber(rx) is not double dx ||
                y.DisplayNumber(ry) is not double dy || time - rx!.Time > 500 || time - ry!.Time > 500)
            {
                previousKey = null;
                continue;
            }
            if (filter is not null && !filter(time))
            {
                previousKey = null;
                FilteredOut++;
                continue;
            }
            var key = (settings.X.Bin(dx), singleRow ? 0 : settings.Y.Bin(dy));
            if (!double.IsFinite(key.Item1) || !double.IsFinite(key.Item2))
            {
                previousKey = null;
                continue;
            }
            xBins.Add(key.Item1);
            yBins.Add(key.Item2);
            if (xBins.Count > 120 || yBins.Count > 120)
                TooManyBins = true;
            else
            {
                long elapsed = time - previousTime;
                bool sameVisit = previousKey == key && elapsed is >= 0 and <= 500;
                Cells[key] = Cells.GetValueOrDefault(key).Add(amount, !sameVisit, sameVisit ? elapsed : 0);
                previousKey = key;
                previousTime = time;
            }
        }
        return next != first;
    }
}
