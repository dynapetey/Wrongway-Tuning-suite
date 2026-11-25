using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace WrongWay;

sealed class ScanGauge(GaugeSetting settings, IBrush accent) : Control
{
    private static readonly Pen Ring = new(Brush.Parse("#30404E"), 3);
    private static readonly Pen Tick = new(Brush.Parse("#687786"), 1);
    private readonly Pen needle = new(accent, 2.5);
    private double? value;
    private string sourceUnit = "";
    private string valueLabel = "—";
    private double minimum = ImperialUnits.Range(settings.Minimum, settings.Source, settings.RangeUnit);
    private double maximum = ImperialUnits.Range(settings.Maximum, settings.Source, settings.RangeUnit);

    public void Update(Channel? channel, long cursor)
    {
        double? reading = channel?.DisplayNumber(channel.AsOf(cursor));
        string units = channel?.DisplayUnit ?? "";
        string label = reading is double number ? channel!.FormatDisplay(number) : "—";
        if (value == reading && sourceUnit == units && valueLabel == label)
            return;
        value = reading;
        sourceUnit = units;
        valueLabel = label;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 50 || Bounds.Height < 46)
            return;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double rangeValue = value.GetValueOrDefault();
        double position = Math.Clamp((rangeValue - minimum) / (maximum - minimum), 0, 1);
        if (settings.Kind != GaugeKind.Dial)
        {
            Write(settings.Label, 12, DeskUi.ChartMuted, center.Y - 31);
            Write((valueLabel + " " + sourceUnit).TrimEnd(), 21, accent, center.Y - 14);
            if (settings.Kind == GaugeKind.Bar)
            {
                double width = Math.Min(188, Bounds.Width - 24);
                var bar = new Rect(center.X - width / 2, center.Y + 18, width, 7);
                context.FillRectangle(DeskUi.Edge, bar, 2);
                if (value.HasValue && position > 0)
                    context.FillRectangle(accent, new Rect(bar.X, bar.Y, width * position, bar.Height), 2);
            }
            return;
        }
        double radius = Math.Min((Bounds.Width - 16) / 2, (Bounds.Height - 14) / 2);
        if (radius < 20)
            return;
        Point At(double fraction, double distance)
        {
            double radians = (135 + 270 * fraction) * Math.PI / 180;
            return new Point(center.X + Math.Cos(radians) * distance, center.Y + Math.Sin(radians) * distance);
        }
        for (int index = 0; index < 48; index++)
            context.DrawLine(Ring, At(index / 48d, radius), At((index + 1) / 48d, radius));
        for (int index = 0; index <= 10; index++)
            context.DrawLine(Tick, At(index / 10d, radius - 4), At(index / 10d, radius - (index % 5 == 0 ? 12 : 8)));

        if (value.HasValue)
        {
            context.DrawLine(needle, center, At(position, radius - 16));
            context.DrawEllipse(accent, null, center, 3, 3);
        }

        Write(settings.Label, 12, DeskUi.ChartMuted, center.Y - radius * 0.52);
        Write(valueLabel, 19, accent, center.Y + radius * 0.18);
        Write(sourceUnit, 10, DeskUi.ChartMuted, center.Y + radius * 0.54);

        void Write(string text, double size, IBrush brush, double y)
        {
            var layout = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, size, brush);
            using var clip = context.PushClip(new Rect(3, 0, Bounds.Width - 6, Bounds.Height));
            context.DrawText(layout, new Point(Math.Max(3, center.X - layout.Width / 2), y));
        }
    }
}
