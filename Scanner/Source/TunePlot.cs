using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace WrongWay;

sealed class TunePlot : Control
{
    private static readonly Color[] Stops =
        new[] { "#2364D4", "#25B8BD", "#77C641", "#F2CD48", "#EC633D" }.Select(Color.Parse).ToArray();
    private static readonly IBrush[] Palette = Enumerable.Range(0, 65).Select(i =>
    {
        int segment = Math.Min(i / 16, 3);
        double fraction = (i - segment * 16) / 16d;
        Color from = Stops[segment], to = Stops[segment + 1];
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * fraction);
        return (IBrush)new SolidColorBrush(Color.FromRgb(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B)));
    }).ToArray();
    private static readonly Pen GridLine = new(DeskUi.Edge);
    private static readonly Pen AxisLine = new(DeskUi.Muted);
    private static readonly Pen MeshLine = new(new SolidColorBrush(Color.FromArgb(165, 26, 48, 73)), .7);
    private static readonly Pen Selection = new(Brushes.White, 1.5);
    private readonly bool surface;
    private readonly double homeAngle;
    private readonly List<(Point Point, int Row, int Column)> points = [];
    private TuneParameter? parameter;
    private double[,]? values;
    private double low, high;
    private double yaw, tilt = .58, zoom = 1;
    private (int Row, int Column) selected;
    private Point start, last;
    private bool tracking, dragged;
    private (Point Point, int Row, int Column)? editPoint;
    private Point editCursor;
    private double editValue;

    public Action<int, int>? PickCell { get; set; }
    public Action<int, int, double>? EditCell { get; set; }

    public TunePlot(bool surface, double angle = .68)
    {
        this.surface = surface;
        homeAngle = yaw = angle;
        ClipToBounds = true;
        PointerPressed += (_, e) =>
        {
            if (values is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;
            start = last = e.GetPosition(this);
            if (EditCell is not null && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                editPoint = Closest(start);
                if (editPoint is not { } point)
                    return;
                editValue = values[point.Row, point.Column];
                editCursor = point.Point;
            }
            tracking = true;
            dragged = false;
            e.Pointer.Capture(this);
        };
        PointerMoved += (_, e) =>
        {
            if (!tracking)
                return;
            Point next = e.GetPosition(this);
            if (!dragged && DistanceSquared(next, start) < 16)
                return;
            if (editPoint is { } point)
            {
                double range = high - low;
                double before = values![point.Row, point.Column];
                if (!(range > 0))
                    range = Math.Max(1, Math.Abs(before) * .1);
                double span = surface ? .62 * Math.Cos(tilt) * SurfaceScale : Bounds.Height - 41;
                editValue = before + (start.Y - next.Y) * range / span;
                editCursor = new(point.Point.X, point.Point.Y + next.Y - start.Y);
                dragged = true;
                InvalidateVisual();
                return;
            }
            if (!surface)
                return;
            dragged = true;
            yaw += (next.X - last.X) * .013;
            tilt = Math.Clamp(tilt + (next.Y - last.Y) * .009, .16, 1.22);
            last = next;
            InvalidateVisual();
        };
        PointerReleased += (_, e) =>
        {
            if (!tracking)
                return;
            var point = editPoint;
            editPoint = null;
            tracking = false;
            e.Pointer.Capture(null);
            if (dragged && point is { } edited)
                EditCell?.Invoke(edited.Row, edited.Column, editValue);
            else if (!dragged && Closest(e.GetPosition(this)) is { } closest)
                PickCell?.Invoke(closest.Row, closest.Column);
            InvalidateVisual();
        };
        PointerCaptureLost += (_, _) => { tracking = false; editPoint = null; InvalidateVisual(); };
        PointerWheelChanged += (_, e) =>
        {
            if (!surface || values is null)
                return;
            zoom = Math.Clamp(zoom * (e.Delta.Y > 0 ? 1.13 : 1 / 1.13), .65, 2.6);
            InvalidateVisual();
            e.Handled = true;
        };
    }

    private (Point Point, int Row, int Column)? Closest(Point cursor)
    {
        if (points.Count == 0)
            return null;
        var closest = points.MinBy(point => DistanceSquared(point.Point, cursor));
        return DistanceSquared(closest.Point, cursor) <= 225 ? closest : null;
    }

    private double SurfaceScale => Math.Min(Bounds.Width * .64, Bounds.Height * 1.08) * zoom;

    private static double DistanceSquared(Point a, Point b)
    {
        double x = a.X - b.X, y = a.Y - b.Y;
        return x * x + y * y;
    }

    public void ResetView()
    {
        yaw = homeAngle;
        tilt = .58;
        zoom = 1;
        InvalidateVisual();
    }

    public void SetData(TuneParameter? map, double[,]? data, double minimum, double maximum, int row, int column)
    {
        if (parameter != map)
            ResetView();
        parameter = map;
        values = data;
        low = minimum;
        high = maximum;
        selected = (row, column);
        InvalidateVisual();
    }

    public void SetSelection(int row, int column)
    {
        if (selected == (row, column))
            return;
        selected = (row, column);
        InvalidateVisual();
    }

    private double Unit(double value) => high > low ? Math.Clamp((value - low) / (high - low), 0, 1) : .5;
    private IBrush ColorAt(double value) => Palette[(int)Math.Round(Unit(value) * (Palette.Length - 1))];

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(DeskUi.ChartBackground, new Rect(Bounds.Size));
        points.Clear();
        if (values is null || parameter is null || Bounds.Width < 100 || Bounds.Height < 70 ||
            !double.IsFinite(low) || !double.IsFinite(high - low))
            return;
        if (surface)
            DrawSurface(context, values, parameter);
        else
            DrawProfile(context, values, parameter);
        if (editPoint is not null && dragged)
        {
            context.DrawEllipse(DeskUi.Blue, Selection, editCursor, 4, 4);
            Write(context, editValue.ToString("G6", CultureInfo.InvariantCulture),
                Math.Clamp(editCursor.X + 7, 3, Bounds.Width - 65), Math.Clamp(editCursor.Y - 14, 3, Bounds.Height - 18), DeskUi.Ink);
        }
    }

    private void DrawProfile(DrawingContext context, double[,] data, TuneParameter map)
    {
        var area = new Rect(49, 14, Bounds.Width - 65, Bounds.Height - 41);
        for (int i = 0; i < 5; i++)
        {
            double y = area.Bottom - area.Height * i / 4;
            double value = low + (high - low) * i / 4;
            context.DrawLine(GridLine, new Point(area.Left, y), new Point(area.Right, y));
            Write(context, value.ToString("G4", CultureInfo.InvariantCulture), 6, y - 7, ColorAt(value));
        }
        context.DrawLine(AxisLine, area.TopLeft, area.BottomLeft);
        context.DrawLine(AxisLine, area.BottomLeft, area.BottomRight);
        Write(context, map.Units.Length > 0 ? $"Decoded value ({map.Units})" : "Decoded value",
            area.Left, area.Bottom + 6, DeskUi.Muted);
        int step = Math.Max(1, data.Length / Math.Max(1, (int)area.Width));
        (Point Point, int Row)? previous = null;
        using var clip = context.PushClip(area.Inflate(3));
        for (int index = 0; index < data.Length; index++)
        {
            int row = index / map.Columns, col = index % map.Columns;
            if (index % step != 0 && (row, col) != selected && index + 1 != data.Length)
                continue;
            double value = data[row, col];
            if (!double.IsFinite(value))
            {
                previous = null;
                continue;
            }
            var point = new Point(area.Left + index * area.Width / (data.Length - 1), area.Bottom - Unit(value) * area.Height);
            IBrush ink = ColorAt(value);
            if (previous is { } prior)
                context.DrawLine(new Pen(ink, prior.Row == row ? 1.2 : .8), prior.Point, point);
            context.DrawEllipse(ink, (row, col) == selected ? Selection : null, point, 2.6, 2.6);
            points.Add((point, row, col));
            previous = (point, row);
        }
    }

    private void DrawSurface(DrawingContext context, double[,] data, TuneParameter map)
    {
        double sin = Math.Sin(yaw), cos = Math.Cos(yaw);
        double pitch = Math.Sin(tilt), rise = Math.Cos(tilt);
        double scale = SurfaceScale;
        Point Project(double x, double y, double z) => new(
            Bounds.Width * .5 + (x * cos - y * sin) * scale,
            Bounds.Height * .61 + ((x * sin + y * cos) * pitch - z * rise) * scale);
        double X(int col) => map.Columns > 1 ? (double)col / (map.Columns - 1) - .5 : 0;
        double Y(int row) => map.Rows > 1 ? (double)row / (map.Rows - 1) - .5 : 0;
        double Z(double value) => .05 + Unit(value) * .62;
        for (int i = 0; i <= 8; i++)
        {
            double tick = i / 8d - .5;
            context.DrawLine(GridLine, Project(-.5, tick, 0), Project(.5, tick, 0));
            context.DrawLine(GridLine, Project(tick, -.5, 0), Project(tick, .5, 0));
        }
        int[] rows = Sample(map.Rows, selected.Row);
        int[] columns = Sample(map.Columns, selected.Column);
        var faces = new List<(double Depth, int A, int B)>();
        for (int i = 0; i + 1 < rows.Length; i++)
        for (int j = 0; j + 1 < columns.Length; j++)
            faces.Add((X(columns[j]) * sin + Y(rows[i]) * cos, i, j));

        foreach (var (_, i, j) in faces.OrderBy(face => face.Depth))
        {
            int r0 = rows[i], r1 = rows[i + 1], c0 = columns[j], c1 = columns[j + 1];
            double a = data[r0, c0], b = data[r0, c1], c = data[r1, c1], d = data[r1, c0];
            if (!double.IsFinite(a) || !double.IsFinite(b) || !double.IsFinite(c) || !double.IsFinite(d))
                continue;
            var face = new StreamGeometry();
            using (var path = face.Open())
            {
                path.BeginFigure(Project(X(c0), Y(r0), Z(a)), true);
                path.LineTo(Project(X(c1), Y(r0), Z(b)));
                path.LineTo(Project(X(c1), Y(r1), Z(c)));
                path.LineTo(Project(X(c0), Y(r1), Z(d)));
                path.EndFigure(true);
            }
            context.DrawGeometry(ColorAt(a / 4 + b / 4 + c / 4 + d / 4), MeshLine, face);
        }
        foreach (int row in rows)
        foreach (int col in columns)
        {
            double value = data[row, col];
            if (!double.IsFinite(value))
                continue;
            Point point = Project(X(col), Y(row), Z(value));
            bool active = (row, col) == selected;
            context.DrawEllipse(active ? ColorAt(value) : DeskUi.Blue, active ? Selection : null,
                point, active ? 4 : 1.65, active ? 4 : 1.65);
            points.Add((point, row, col));
        }
        Write(context, "Drag to orbit · Scroll to zoom", 10, 6, DeskUi.Muted);
        string axes = string.Join("  ×  ", new[] { map.ColumnUnits, map.RowUnits }.Where(text => text.Length > 0));
        Write(context, axes, 10, Bounds.Height - 17, DeskUi.Muted);
    }

    private static int[] Sample(int count, int selected)
    {
        int step = Math.Max(1, (count + 27) / 28);
        return Enumerable.Range(0, count).Where(index => index % step == 0 || index == count - 1 || index == selected).ToArray();
    }

    private static void Write(DrawingContext context, string text, double x, double y, IBrush ink) =>
        context.DrawText(new FormattedText(text, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 10, ink), new Point(x, y));
}
