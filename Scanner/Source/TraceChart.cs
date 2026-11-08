using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace WrongWay;

sealed class TraceChart : Control
{
    public static readonly IBrush[] SeriesBrushes = new[] {
        "#64E0CD", "#F1AD62", "#8EAAFF", "#EB86BA", "#B5DA77", "#BBA6FD"
    }.Select(Brush.Parse).ToArray();
    private static readonly IBrush Grid = Brush.Parse("#30404E");
    private static readonly IBrush Dim = DeskUi.ChartMuted;
    private static readonly IBrush Panel = DeskUi.ChartBackground;
    private static readonly IBrush Legend = Brush.Parse("#18232E");
    private const double Left = 154;
    private const double Right = 58;
    private const double Top = 12;
    private const double Bottom = 30;

    public IReadOnlyList<Channel> Channels { get; set; } = [];
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
            var brush = SeriesBrushes[i];
            double y = Top + i * lane;
            context.DrawLine(new Pen(Grid), new Point(0, y + lane), new Point(Bounds.Width, y + lane));
            DrawSeries(context, channel, brush, new Rect(Left, y, width, lane), span);
        }
        if (CursorTime >= ViewStart && CursorTime <= ViewEnd)
        {
            double x = Left + width * (CursorTime - ViewStart) / span;
            context.DrawLine(new Pen(Brushes.White, 1.1), new Point(x, Top), new Point(x, Top + height));
        }
    }

    private void DrawSeries(DrawingContext context, Channel channel, IBrush brush, Rect area, double span)
    {
        using (context.PushClip(new Rect(7, area.Y, Left - 13, area.Height)))
        {
            string name = channel.Name;
            var label = Text(name, 11, brush);
            while (label.Width > Left - 15 && name.Length > 1)
            {
                name = name[..^1];
                label = Text(name + "…", 11, brush);
            }
            context.DrawText(label, new Point(8, area.Y + 5));
            if (area.Height > 32 && channel.AsOf(CursorTime)?.Number is double value)
            {
                var current = Text($"{value:0.##} {channel.Unit}".TrimEnd(), 12, Dim);
                context.DrawText(current, new Point(8, area.Y + 20));
            }
        }

        int start = Math.Max(0, channel.UpperBound(ViewStart) - 1);
        int end = Math.Min(channel.Readings.Count, channel.UpperBound(ViewEnd) + 1);
        var points = channel.Readings.GetRange(start, Math.Max(0, end - start))
            .Where(value => value.Number is not null).ToList();
        if (points.Count == 0)
        {
            context.DrawText(Text("No samples in view", 11, Dim), new Point(area.X + 8, area.Y + 6));
            return;
        }
        double min = points.Min(p => p.Number!.Value);
        double max = points.Max(p => p.Number!.Value);
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
        Point Locate(Reading reading) => new(
            area.X + (reading.Time - ViewStart) / span * area.Width,
            area.Bottom - 5 - (reading.Number!.Value - min) / range * height);
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
