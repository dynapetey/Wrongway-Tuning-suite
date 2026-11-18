using System.Globalization;
using Cell = (int Row, int Column);

namespace WrongWay;

enum TuneDirection { Both, Horizontal, Vertical }
enum TunePaste { Replace, Add, Subtract, MultiplyPercent, MultiplyHalfPercent, Average }

readonly record struct TuneArea(int Top, int Left, int Bottom, int Right)
{
    public static TuneArea Between(Cell first, Cell last) => new(
        Math.Min(first.Row, last.Row), Math.Min(first.Column, last.Column),
        Math.Max(first.Row, last.Row), Math.Max(first.Column, last.Column));

    public static TuneArea Around(IReadOnlySet<Cell> cells) => new(
        cells.Min(cell => cell.Row), cells.Min(cell => cell.Column),
        cells.Max(cell => cell.Row), cells.Max(cell => cell.Column));

    public IEnumerable<Cell> Positions()
    {
        for (int row = Top; row <= Bottom; row++)
        for (int col = Left; col <= Right; col++)
            yield return (row, col);
    }
}

static class TuneGridTools
{
    public static IEnumerable<(int Row, int Column, double Value)> Smooth(
        IReadOnlySet<Cell> selection, Func<Cell, double> read, TuneDirection direction)
    {
        var source = selection.ToDictionary(cell => cell, read);
        int height = direction == TuneDirection.Horizontal ? 0 : 1;
        int width = direction == TuneDirection.Vertical ? 0 : 1;
        foreach (var (row, col) in selection)
        {
            double sum = 0;
            int weight = 0;
            for (int dy = -height; dy <= height; dy++)
            for (int dx = -width; dx <= width; dx++)
            {
                if (!source.TryGetValue((row + dy, col + dx), out double value))
                    continue;
                int factor = (dy == 0 ? 2 : 1) * (dx == 0 ? 2 : 1);
                sum += value * factor;
                weight += factor;
            }
            yield return (row, col, sum / weight);
        }
    }

    public static IEnumerable<(int Row, int Column, double Value)> Interpolate(
        IReadOnlySet<Cell> selection, Func<Cell, double> read, string[] rows, string[] columns, TuneDirection direction)
    {
        var area = TuneArea.Around(selection);
        double[] x = AxisCoordinates(columns);
        double[] y = AxisCoordinates(rows);
        if (direction == TuneDirection.Both && area.Top != area.Bottom && area.Left != area.Right)
        {
            if (selection.Count != (area.Bottom - area.Top + 1) * (area.Right - area.Left + 1))
                throw new FormatException("Select one complete rectangle for surface interpolation.");
            double tl = read((area.Top, area.Left)), tr = read((area.Top, area.Right));
            double bl = read((area.Bottom, area.Left)), br = read((area.Bottom, area.Right));
            foreach (var (row, col) in area.Positions())
            {
                if ((row == area.Top || row == area.Bottom) && (col == area.Left || col == area.Right))
                    continue;
                double horizontal = Fraction(x, col, area.Left, area.Right);
                double vertical = Fraction(y, row, area.Top, area.Bottom);
                yield return (row, col, Blend(Blend(tl, tr, horizontal), Blend(bl, br, horizontal), vertical));
            }
            yield break;
        }

        bool alongRows = direction == TuneDirection.Horizontal ||
            (direction == TuneDirection.Both && area.Top == area.Bottom);
        double[] axis = alongRows ? x : y;
        foreach (var line in selection.GroupBy(cell => alongRows ? cell.Row : cell.Column))
        {
            int[] indices = line.Select(cell => alongRows ? cell.Column : cell.Row).Order().ToArray();
            Cell Position(int i) => alongRows ? (line.Key, i) : (i, line.Key);
            for (int first = 0; first < indices.Length;)
            {
                int last = first;
                while (last + 1 < indices.Length && indices[last + 1] == indices[last] + 1)
                    last++;
                if (last - first > 1)
                {
                    double start = read(Position(indices[first])), end = read(Position(indices[last]));
                    for (int i = first + 1; i < last; i++)
                    {
                        var (row, col) = Position(indices[i]);
                        yield return (row, col, Blend(start, end, Fraction(axis, indices[i], indices[first], indices[last])));
                    }
                }
                first = last + 1;
            }
        }
    }

    private static double[] AxisCoordinates(string[] labels)
    {
        var axis = new double[labels.Length];
        int order = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            if (!Number(labels[i], out axis[i]))
                return Indices();
            if (i == 0)
                continue;
            double gap = axis[i] - axis[i - 1];
            if (gap == 0 || !double.IsFinite(gap) || (order != 0 && order != Math.Sign(gap)))
                return Indices();
            order = Math.Sign(gap);
        }
        return axis;

        double[] Indices() => Enumerable.Range(0, labels.Length).Select(i => (double)i).ToArray();
    }

    private static double Fraction(double[] axis, int i, int start, int end) =>
        (axis[i] - axis[start]) / (axis[end] - axis[start]);

    private static double Blend(double first, double last, double fraction) =>
        (1 - fraction) * first + fraction * last;

    public static string Copy(TuneParameter map, IReadOnlySet<Cell> selection, Func<Cell, double> read, bool axes)
    {
        var area = TuneArea.Around(selection);
        var lines = new List<string>();
        if (axes)
            lines.Add("#Axes\t" + string.Join('\t', map.ColumnLabels[area.Left..(area.Right + 1)]));
        for (int row = area.Top; row <= area.Bottom; row++)
        {
            var fields = new List<string>();
            if (axes)
                fields.Add(map.RowLabels[row]);
            for (int col = area.Left; col <= area.Right; col++)
                fields.Add(selection.Contains((row, col)) ? map.Format(read((row, col)), forEdit: true) : "");
            lines.Add(string.Join('\t', fields));
        }
        return string.Join('\n', lines);
    }

    public static IEnumerable<(int Row, int Column, double Value)> Paste(TuneParameter map, string text,
        IReadOnlySet<Cell> selection, Func<Cell, double> read, TunePaste mode)
    {
        if (text.Length > 1_000_000)
            throw new FormatException("The pasted table is too large.");
        string[][] data = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n')
            .Select(line => line.Split('\t')).ToArray();
        int width = data[0].Length;
        if (data.Any(line => line.Length != width))
            throw new FormatException("Paste a rectangular block of tab-separated values.");

        bool axes = data[0][0] == "#Axes";
        if (!axes && data.Length == 1 && width == 1 && !string.IsNullOrWhiteSpace(data[0][0]))
        {
            foreach (Cell cell in selection)
                yield return Edit(cell, data[0][0]);
            yield break;
        }

        var area = TuneArea.Around(selection);
        int offset = axes ? 1 : 0;
        if (width <= offset || data.Length <= offset || width - offset > map.Columns || data.Length - offset > map.Rows ||
            (!axes && (area.Left + width > map.Columns || area.Top + data.Length > map.Rows)))
            throw new FormatException("The copied block does not fit in this table.");

        int[] rows = axes ? AxisMapping(data.Skip(1).Select(line => line[0]), map.RowLabels, "row") :
            Enumerable.Range(area.Top, data.Length).ToArray();
        int[] columns = axes ? AxisMapping(data[0].Skip(1), map.ColumnLabels, "column") :
            Enumerable.Range(area.Left, width).ToArray();
        for (int row = 0; row < rows.Length; row++)
        for (int col = 0; col < columns.Length; col++)
        {
            string value = data[row + offset][col + offset];
            if (!string.IsNullOrWhiteSpace(value))
                yield return Edit((rows[row], columns[col]), value);
        }

        (int Row, int Column, double Value) Edit(Cell cell, string value)
        {
            double operand = map.ParseEdit(value, setValue: mode == TunePaste.Replace);
            double before = mode == TunePaste.Replace ? 0 : read(cell);
            return (cell.Row, cell.Column, mode switch
            {
                TunePaste.Add => before + operand,
                TunePaste.Subtract => before - operand,
                TunePaste.MultiplyPercent => before * operand / 100,
                TunePaste.MultiplyHalfPercent => before * (1 + (operand - 100) / 200),
                TunePaste.Average => Blend(before, operand, .5),
                _ => operand
            });
        }
    }

    private static int[] AxisMapping(IEnumerable<string> source, string[] destination, string name)
    {
        var used = new HashSet<int>();
        return source.Select(label =>
        {
            bool numeric = Number(label, out double value);
            int[] matches = Enumerable.Range(0, destination.Length).Where(i => label == destination[i] ||
                (numeric && Number(destination[i], out double axis) && value == axis)).ToArray();
            if (matches.Length != 1 || !used.Add(matches[0]))
                throw new FormatException($"The {name} label '{label}' does not match a unique destination cell.");
            return matches[0];
        }).ToArray();
    }

    private static bool Number(string label, out double value) =>
        double.TryParse(label, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}
