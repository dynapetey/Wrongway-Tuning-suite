using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace WrongWay;

sealed class TraceChart : Control
{
    public static readonly string[] ColorNames = ["Mint", "Amber", "Blue", "Pink", "Lime", "Violet", "Cyan", "Coral", "Gold", "Lavender", "Teal", "Rose", "Sky", "Orange", "Green", "Lilac", "Ice", "Peach", "Periwinkle", "Blush", "Chartreuse", "Orchid", "Aqua", "Salmon", "Steel blue", "Sand", "Seafoam", "Plum", "Pale cyan", "Apricot", "Apple", "Silver"];
    public static readonly IBrush[] SeriesBrushes = new[] {
        "#64E0CD", "#F1AD62", "#8EAAFF", "#EB86BA", "#B5DA77", "#BBA6FD", "#42DDF5", "#FF8066", "#E9CD4A", "#D7B8FF", "#39BDAD", "#F768A8", "#4CAFFF", "#F38A35", "#68C96F", "#AA84E8", "#A3EEED", "#FFD1A3", "#BBCBFF", "#FFC1D9", "#D7ED76", "#E3A3EF", "#78F2C3", "#FFAD9B", "#8AB8D8", "#D5BD87", "#9ACFAB", "#C78FAF", "#B6E9FA", "#F4B875", "#A0DB56", "#D6DEE8"
    }.Select(Brush.Parse).ToArray();
    public const int ScrollThreshold = 6;
    public static double MinimumPlotHeight(int channels) => Math.Max(170, channels * 54 + 42);
    private static readonly IBrush Grid = Brush.Parse("#30404E");
    private static readonly IBrush Dim = DeskUi.ChartMuted;
    private static readonly IBrush Panel = DeskUi.ChartBackground;
    private static readonly IBrush Legend = Brush.Parse("#18232E");
    private const double Left = 154;
    private const double Right = 58;
    private const double Top = 12;
    private const double Bottom = 30;

    public IReadOnlyList<Channel> Channels { get; set; } = [];
    public IReadOnlyDictionary<Channel, TraceSetting> TraceStyles { get; set; } = new Dictionary<Channel, TraceSetting>();
    public long ViewStart { get; set; }
    public long ViewEnd { get; set; } = 1;
    public long Origin { get; set; }
    public long CursorTime { get; set; }
    public Action<long>? Seek { get; set; }
    public Action<double, double>? ZoomAt { get; set; }

    public TraceChart()
    {
        PointerPressed += (_, args) =>
        {
            if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                Seek?.Invoke(ViewStart + (long)Math.Round(Fraction(args) * (ViewEnd - ViewStart)));
        };
        PointerWheelChanged += (_, args) =>
        {
            if (Channels.Count > ScrollThreshold && !args.KeyModifiers.HasFlag(KeyModifiers.Control))
                return;
            ZoomAt?.Invoke(Fraction(args), args.Delta.Y > 0 ? 0.7 : 1.4);
            args.Handled = true;
        };
    }

    private double Fraction(PointerEventArgs args) =>
        Math.Clamp((args.GetPosition(this).X - Left) / Math.Max(1, Bounds.Width - Left - Right), 0, 1);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Panel, new Rect(Bounds.Size));
        double width = Bounds.Width - Left - Right;
        double height = Bounds.Height - Top - Bottom;
        if (width <= 0 || height <= 0)
            return;
        if (Channels.Count == 0)
        {
            var prompt = Text("Open a log or start recording to plot channels.", 14, Dim);
            context.DrawText(prompt, new Point(Math.Max(12, (Bounds.Width - prompt.Width) / 2), height * 0.48));
            return;
        }

        context.FillRectangle(Legend, new Rect(0, 0, Left, Top + height));
        double span = Math.Max(1, ViewEnd - ViewStart);
        double lane = height / Channels.Count;
        for (int tick = 0; tick <= 5; tick++)
        {
            double x = Left + width * tick / 5;
            context.DrawLine(new Pen(Grid), new Point(x, Top), new Point(x, Top + height));
            string label = ((ViewStart - Origin + span * tick / 5) / 1000).ToString("0.0", CultureInfo.InvariantCulture);
            var formatted = Text(label + "s", 11, Dim);
            context.DrawText(formatted, new Point(x - formatted.Width / 2, Top + height + 8));
        }

        for (int i = 0; i < Channels.Count; i++)
        {
            var channel = Channels[i];
            var style = TraceStyles.GetValueOrDefault(channel);
            var brush = SeriesBrushes[(style?.Color ?? i) % SeriesBrushes.Length];
            double y = Top + i * lane;
            context.DrawLine(new Pen(Grid), new Point(0, y + lane), new Point(Bounds.Width, y + lane));
            DrawSeries(context, channel, style, brush, new Rect(Left, y, width, lane), span);
        }
        if (CursorTime >= ViewStart && CursorTime <= ViewEnd)
        {
            double x = Left + width * (CursorTime - ViewStart) / span;
            context.DrawLine(new Pen(Brushes.White, 1.1), new Point(x, Top), new Point(x, Top + height));
        }
    }

    private void DrawSeries(DrawingContext context, Channel channel, TraceSetting? style, IBrush brush, Rect area, double span)
    {
        using (context.PushClip(new Rect(7, area.Y, Left - 13, area.Height)))
        {
            string name = channel.DisplayName;
            var label = Text(name, 11, brush);
            while (label.Width > Left - 15 && name.Length > 1)
            {
                name = name[..^1];
                label = Text(name + "…", 11, brush);
            }
            context.DrawText(label, new Point(8, area.Y + 5));
            if (area.Height > 32 && channel.DisplayNumber(channel.AsOf(CursorTime)) is double value)
            {
                var current = Text($"{channel.FormatDisplay(value)} {channel.DisplayUnit}".TrimEnd(), 12, Dim);
                context.DrawText(current, new Point(8, area.Y + 20));
            }
        }

        int start = Math.Max(0, channel.UpperBound(ViewStart) - 1);
        int end = Math.Min(channel.Readings.Count, channel.UpperBound(ViewEnd) + 1);
        var points = new List<(long Time, double Value)>(Math.Max(0, end - start));
        for (int i = start; i < end; i++)
        {
            var reading = channel.Readings[i];
            if (channel.DisplayNumber(reading) is double value)
                points.Add((reading.Time, value));
        }
        if (points.Count == 0)
        {
            context.DrawText(Text("No samples in view", 11, Dim), new Point(area.X + 8, area.Y + 6));
            return;
        }
        double min = style?.Minimum ?? points.Min(p => p.Value);
        double max = style?.Maximum ?? points.Max(p => p.Value);
        double range = max - min;
        if (range == 0)
        {
            double padding = Math.Max(1, Math.Abs(min) * 0.05);
            min -= padding;
            max += padding;
            range = max - min;
        }
        if (!double.IsFinite(range) || range <= 0)
            return;

        context.DrawText(Text(max.ToString("G4", CultureInfo.InvariantCulture), 10, Dim), new Point(area.Right + 5, area.Y + 3));
        if (area.Height > 34)
            context.DrawText(Text(min.ToString("G4", CultureInfo.InvariantCulture), 10, Dim),
                new Point(area.Right + 5, area.Bottom - 17));
        using var clip = context.PushClip(area);
        context.DrawLine(new Pen(Grid), new Point(area.X, area.Center.Y), new Point(area.Right, area.Center.Y));

        var geometry = new StreamGeometry();
        double height = Math.Max(1, area.Height - 12);
        Point Locate((long Time, double Value) point) => new(
            area.X + (point.Time - ViewStart) / span * area.Width,
            area.Bottom - 5 - (point.Value - min) / range * height);
        using (var figure = geometry.Open())
        {
            figure.BeginFigure(Locate(points[0]), false);
            for (int i = 1; i < points.Count; i++)
                figure.LineTo(Locate(points[i]));
            figure.EndFigure(false);
        }
        var pen = new Pen(brush, 1.5);
        context.DrawGeometry(null, pen, geometry);
        if (points.Count == 1)
        {
            Point point = Locate(points[0]);
            context.DrawEllipse(brush, null, point, 2, 2);
        }
    }

    private static FormattedText Text(string value, double size, IBrush brush) =>
        new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
}
