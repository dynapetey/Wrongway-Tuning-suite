using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace WrongWay;

sealed class ScanGauge(string name, string unit, double maximum, IBrush accent) : Control
{
    private static readonly Pen Ring = new(Brush.Parse("#30404E"), 3);
    private static readonly Pen Tick = new(Brush.Parse("#687786"), 1);
    private readonly Pen needle = new(accent, 2.5);
    private double? value;
    private string? sourceUnit;

    public void Update(double? reading, string units)
    {
        string? nextUnit = units.Length == 0 ? null : units;
        if (value == reading && sourceUnit == nextUnit)
            return;
        value = reading;
        sourceUnit = nextUnit;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double radius = Math.Max(0, Math.Min((Bounds.Width - 16) / 2, (Bounds.Height - 14) / 2));
        if (radius < 20)
            return;
        string displayUnit = sourceUnit ?? unit;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        Point At(double fraction, double distance)
        {
            double radians = (135 + 270 * fraction) * Math.PI / 180;
            return new Point(center.X + Math.Cos(radians) * distance, center.Y + Math.Sin(radians) * distance);
        }
        for (int index = 0; index < 48; index++)
            context.DrawLine(Ring, At(index / 48d, radius), At((index + 1) / 48d, radius));
        for (int index = 0; index <= 10; index++)
            context.DrawLine(Tick, At(index / 10d, radius - 4), At(index / 10d, radius - (index % 5 == 0 ? 12 : 8)));

        if (value is double number && double.IsFinite(number))
        {
            // Scale the dial in the source unit, including imperial MAP logs.
            double scale = unit == "kPa" && displayUnit.Equals("psi", StringComparison.OrdinalIgnoreCase)
                ? maximum / 6.894757 : maximum;
            double position = Math.Clamp(number / scale, 0, 1);
            context.DrawLine(needle, center, At(position, radius - 16));
            context.DrawEllipse(accent, null, center, 3, 3);
        }

        Write(name, 12, DeskUi.ChartMuted, center.Y - radius * 0.52);
        Write(value?.ToString("0.#", CultureInfo.InvariantCulture) ?? "—", 19, accent, center.Y + radius * 0.18);
        Write(displayUnit, 10, DeskUi.ChartMuted, center.Y + radius * 0.54);

        void Write(string text, double size, IBrush brush, double y)
        {
            var layout = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, size, brush);
            context.DrawText(layout, new Point(center.X - layout.Width / 2, y));
        }
    }
}
