using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace WrongWay;

sealed record HistogramSnapshot(string XLabel, string YLabel, string ValueLabel,
    GraphStatistic Statistic, int MinimumSamples, IReadOnlyDictionary<(double X, double Y), HistogramSamples.Cell> Cells)
{
    private readonly double[] xs = Cells.Keys.Select(key => key.X).Distinct().Order().ToArray();
    private readonly double[] ys = Cells.Keys.Select(key => key.Y).Distinct().Order().ToArray();

    private double? CellValue(double x, double y, int minSamples)
    {
        if (!Cells.TryGetValue((x, y), out var cell) || cell.Count < minSamples)
            return null;
        return Statistic switch
        {
            GraphStatistic.Count => cell.Count,
            GraphStatistic.Minimum => cell.Minimum,
            GraphStatistic.Maximum => cell.Maximum,
            _ => cell.Mean
        };
    }

    public double? Sample(double targetX, double targetY, int minSamples, bool bilinear)
    {
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
        if (snapshot.Sample(1500, 50, 1, true) != 15 ||
            snapshot.Sample(1000, 40, 1, true) != 0 ||
            snapshot.Sample(3000, 50, 1, false) is not null ||
            snapshot.Sample(1500, 50, 2, true) is not null)
            throw new InvalidDataException("Histogram correction sampling failed.");
    }
}

sealed partial class ScannerWindow
{
    private ContentControl histogramTable = new();
    private TextBlock histogramStatus = DeskUi.Label("", 12, Muted);
    private HistogramSamples? histogram;
    private long histogramRefresh;

    internal HistogramSnapshot? CurrentHistogramSnapshot()
    {
        if (histogram is not { TooManyBins: false, Cells.Count: > 0 } current)
            return null;
        return new HistogramSnapshot(
            current.X.DisplayLabel, current.Y.DisplayLabel, current.Value.DisplayLabel,
            current.Settings.Statistic, current.Settings.MinimumSamples,
            new Dictionary<(double X, double Y), HistogramSamples.Cell>(current.Cells));
    }

    private void RefreshHistogram()
    {
        histogram = null;
        if (log is null)
        {
            graph.Content = DeskUi.Label("Open a log or record live data to build the histogram.");
            return;
        }
        var settings = scannerLayout.Graph;
        var x = settings.X.Source.Resolve(log);
        var y = settings.Y.Source.Resolve(log);
        if (x is null || y is null || x == y)
        {
            graph.Content = DeskUi.Label("Choose two available axis channels in Edit graph.");
            return;
        }
        var value = settings.Value is { } input ? input.Resolve(log) : log.Channels.FirstOrDefault(channel =>
            channel != x && channel != y && channel.Readings.Any(reading => reading.Number.HasValue));
        if (settings.Statistic != GraphStatistic.Count && value is null)
        {
            graph.Content = DeskUi.Label("Choose an available numeric value channel in Edit graph.");
            return;
        }
        var source = settings.Statistic == GraphStatistic.Count ? x : value!;
        histogram = new HistogramSamples(settings, x, y, source);
        var summary = settings.Statistic == GraphStatistic.Count ? "Sample count" : $"{settings.Statistic} of {source.DisplayLabel}";
        histogramTable = new ContentControl();
        histogramStatus = DeskUi.Label("", 12, Muted);
        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(5), RowSpacing = 5 };
        Put(panel, DeskUi.Section($"{x.DisplayLabel} × {y.DisplayLabel}   ·   {summary}"), 0);
        Put(panel, histogramStatus, 1);
        Put(panel, new ScrollViewer { Content = histogramTable, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto }, 2);
        graph.Content = panel;
        UpdateHistogram(force: true);
    }

    private void UpdateHistogram(bool force = false)
    {
        if (histogram is null || !force && Environment.TickCount64 - histogramRefresh < 1000)
            return;
        histogramRefresh = Environment.TickCount64;
        if (!histogram.AddSamples() && !force)
            return;
        histogramStatus.Text = histogram.TooManyBins
            ? "An axis exceeds 80 bins. Increase its bin width or enter breakpoints."
            : histogram.Cells.Count == 0
                ? "No numeric samples align with both axes within 500 ms."
                : $"Columns: {histogram.X.DisplayLabel}   ·   Rows: {histogram.Y.DisplayLabel}";
        histogramTable.Content = histogram.TooManyBins || histogram.Cells.Count == 0 ? null : DrawHistogram(histogram);
    }

    private static Control DrawHistogram(HistogramSamples data)
    {
        var xs = data.Cells.Keys.Select(key => key.X).Distinct().Order().ToArray();
        var ys = data.Cells.Keys.Select(key => key.Y).Distinct().Order().ToArray();
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
                string label = data.Settings.Statistic switch
                {
                    GraphStatistic.Count => $"{cell.Count:N0}",
                    GraphStatistic.Minimum => data.Value.FormatDisplay(cell.Minimum),
                    GraphStatistic.Maximum => data.Value.FormatDisplay(cell.Maximum),
                    _ => data.Value.FormatDisplay(cell.Mean)
                };
                if (data.Settings.Statistic != GraphStatistic.Count)
                    label += $"\nn={cell.Count:N0}";
                Put(grid, new Border
                {
                    Background = cell.Count >= 10 ? DeskUi.Edge : DeskUi.Header,
                    Padding = new Thickness(5, 2), Child = Text(label, 11, DeskUi.Ink)
                }, row + 1, col + 1);
            }
        }
        return grid;
    }
}

sealed class HistogramSamples(GraphSetting settings, Channel x, Channel y, Channel value)
{
    public readonly record struct Cell(double Mean, double Minimum, double Maximum, int Count)
    {
        public Cell Add(double sample) => Count == 0 ? new(sample, sample, sample, 1)
            : new(Mean * (Count / (Count + 1d)) + sample / (Count + 1d),
                Math.Min(Minimum, sample), Math.Max(Maximum, sample), Count + 1);
    }

    public GraphSetting Settings => settings;
    public Channel X => x;
    public Channel Y => y;
    public Channel Value => value;
    public Dictionary<(double X, double Y), Cell> Cells { get; } = [];
    public bool TooManyBins { get; private set; }
    private readonly HashSet<double> xBins = [];
    private readonly HashSet<double> yBins = [];
    private int next;

    public bool AddSamples()
    {
        int first = next;
        while (!TooManyBins && next < value.Readings.Count)
        {
            var sample = value.Readings[next++];
            var rx = x.AsOf(sample.Time);
            var ry = y.AsOf(sample.Time);
            if (value.DisplayNumber(sample) is not double amount || x.DisplayNumber(rx) is not double dx ||
                y.DisplayNumber(ry) is not double dy || sample.Time - rx!.Time > 500 || sample.Time - ry!.Time > 500)
                continue;
            var key = (settings.X.Bin(dx), settings.Y.Bin(dy));
            if (!double.IsFinite(key.Item1) || !double.IsFinite(key.Item2))
                continue;
            xBins.Add(key.Item1);
            yBins.Add(key.Item2);
            if (xBins.Count > 80 || yBins.Count > 80)
                TooManyBins = true;
            else
                Cells[key] = Cells.GetValueOrDefault(key).Add(amount);
        }
        return next != first;
    }
}
