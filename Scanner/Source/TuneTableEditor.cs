using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Cell = (int Row, int Column);

namespace WrongWay;

sealed partial class TuneWindow
{
    private HashSet<Cell> selection = [(0, 0)];
    private readonly Dictionary<Cell, IBrush> cellFills = [];
    private Cell anchor;
    private Cell dragAnchor;
    private HashSet<Cell>? dragBasis;
    private IPointer? dragPointer;
    private bool subtractDrag;
    private int? precision;
    private bool tableOnly;

    private MenuItem MakeTableMenu() => new() { Header = "_Table", ItemsSource = new Control[]
    {
        TableAction("Select all", SelectAllCells),
        TableAction("Copy", () => CopyCells(false)), TableAction("Copy with axes", () => CopyCells(true)),
        TableAction("Paste", () => PasteCells(TunePaste.Replace)), PasteSpecialMenu(), new Separator(),
        TransformMenu(false), TransformMenu(true), TableAction("Maths formula…", ApplyMaths), new Separator(),
        TableAction("Increase decimal precision", () => SetPrecision(1)),
        TableAction("Decrease decimal precision", () => SetPrecision(-1)),
        TableAction("Use XDF precision", () => { precision = null; Paint(resetEntry: false); }),
        TableAction("Save table as TSV…", SaveTable), TableAction("Load table from TSV…", LoadTable),
        TableAction("Restore table from opened BIN", RestoreTable)
    }};

    private ContextMenu MakeTableContext() => new() { ItemsSource = new Control[]
    {
        TableAction("Copy", () => CopyCells(false)), TableAction("Copy with axes", () => CopyCells(true)),
        DeskUi.Menu("Copy axis labels", TableAction("Row axis", () => CopyAxis(true)),
            TableAction("Column axis", () => CopyAxis(false))),
        TableAction("Paste", () => PasteCells(TunePaste.Replace)), PasteSpecialMenu(), new Separator(),
        TransformMenu(false), TransformMenu(true), TableAction("Maths formula…", ApplyMaths), new Separator(),
        TableAction("Undo", () => ChangeHistory(false)), TableAction("Redo", () => ChangeHistory(true)),
        TableAction("Restore table from opened BIN", RestoreTable)
    }};

    private MenuItem PasteSpecialMenu() => DeskUi.Menu("Paste special",
        TableAction("Add", () => PasteCells(TunePaste.Add)), TableAction("Subtract", () => PasteCells(TunePaste.Subtract)),
        TableAction("Multiply by %", () => PasteCells(TunePaste.MultiplyPercent)),
        TableAction("Apply half of % change", () => PasteCells(TunePaste.MultiplyHalfPercent)),
        TableAction("Average", () => PasteCells(TunePaste.Average)));

    private MenuItem TransformMenu(bool interpolate) => DeskUi.Menu(interpolate ? "_Interpolate" : "_Smooth",
        TableAction(interpolate ? "Surface / entire selection" : "Entire selection", () => Transform(interpolate, TuneDirection.Both)),
        TableAction("Horizontal", () => Transform(interpolate, TuneDirection.Horizontal)),
        TableAction("Vertical", () => Transform(interpolate, TuneDirection.Vertical)));

    private MenuItem TableAction(string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => Guard(action);
        return item;
    }

    private MenuItem TableAction(string label, Func<Task> action)
    {
        var item = new MenuItem { Header = label };
        item.Click += async (_, _) => await TableTask(action);
        return item;
    }

    private async Task TableTask(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Report(ex.Message, error: true); }
    }

    private void WireTable(Grid grid)
    {
        grid.ContextMenu = MakeTableContext();
        grid.AddHandler(PointerPressedEvent, (_, e) => Guard(() => StartTableSelection(grid, e)), RoutingStrategies.Tunnel);
        grid.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (e.Pointer == dragPointer && CellAt(grid, e.GetPosition(grid)) is Cell end)
                Guard(() => DragSelection(end));
        }, RoutingStrategies.Tunnel);
        grid.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.Pointer != dragPointer)
                return;
            dragPointer = null;
            dragBasis = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        grid.PointerCaptureLost += (_, _) => { dragPointer = null; dragBasis = null; };
        grid.AddHandler(KeyDownEvent, TableKey, RoutingStrategies.Tunnel);
        grid.AddHandler(TextInputEvent, (_, e) =>
        {
            if (current?.Editable != true || current.IsText || e.Text is not { Length: > 0 } input ||
                !input.All(c => char.IsAsciiDigit(c) || c is '.' or '-'))
                return;
            operation.SelectedIndex = 0;
            entry.Focus();
            entry.Text = input;
            entry.CaretIndex = input.Length;
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    private static Cell? CellAt(Grid grid, Point point)
    {
        // Once captured, the event source is the grid itself. Hit-test the actual pointer point.
        for (Visual? item = grid.InputHitTest(point) as Visual; item is not null && item != grid;
            item = item.GetVisualParent())
            if (item is Control { Tag: Cell cell })
                return cell;
        return null;
    }

    private void StartTableSelection(Grid grid, PointerPressedEventArgs e)
    {
        if (current is null || CellAt(grid, e.GetPosition(grid)) is not Cell cell)
            return;
        var buttons = e.GetCurrentPoint(grid).Properties;
        Cell active = (Math.Max(0, cell.Row), Math.Max(0, cell.Column));
        if (buttons.IsRightButtonPressed)
        {
            if (cell.Row < 0 || cell.Column < 0 || !selection.Contains(cell))
                SetSelection(new HashSet<Cell>(AreaTo(cell, cell).Positions()), active, moveAnchor: true);
            return;
        }
        if (!buttons.IsLeftButtonPressed)
            return;

        scope.SelectedIndex = 0;
        if (e.ClickCount == 2 && (e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control)) == 0 &&
            current.Editable && !current.IsText && cell.Row >= 0 && cell.Column >= 0)
        {
            SetSelection([active], active, moveAnchor: true);
            entry.Focus();
            entry.SelectAll();
        }
        else
        {
            bool extend = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool toggle = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            dragAnchor = extend && cell.Row >= 0 && cell.Column >= 0 ? anchor : cell;
            if (!extend)
                anchor = active;
            dragBasis = toggle ? new HashSet<Cell>(selection) : [];
            subtractDrag = toggle && AreaTo(cell, cell).Positions().All(selection.Contains);
            DragSelection(cell);
            cells[active].Focus();
            dragPointer = e.Pointer;
            e.Pointer.Capture(grid);
        }
        e.Handled = true;
    }

    private TuneArea AreaTo(Cell from, Cell to)
    {
        if (from == (-1, -1))
            return new(0, 0, current!.Rows - 1, current.Columns - 1);
        if (from.Row < 0)
        {
            int col = to.Column < 0 ? from.Column : to.Column;
            return new(0, Math.Min(from.Column, col), current!.Rows - 1, Math.Max(from.Column, col));
        }
        if (from.Column < 0)
        {
            int row = to.Row < 0 ? from.Row : to.Row;
            return new(Math.Min(from.Row, row), 0, Math.Max(from.Row, row), current!.Columns - 1);
        }
        return TuneArea.Between(from, (Math.Max(0, to.Row), Math.Max(0, to.Column)));
    }

    private void DragSelection(Cell end)
    {
        var area = AreaTo(dragAnchor, end);
        var next = new HashSet<Cell>(dragBasis!);
        if (subtractDrag)
            next.ExceptWith(area.Positions());
        else
            next.UnionWith(area.Positions());
        SetSelection(next, (Math.Max(0, end.Row), Math.Max(0, end.Column)));
    }

    private void SetSelection(HashSet<Cell> next, Cell active, bool moveAnchor = false)
    {
        if (moveAnchor)
            anchor = active;
        if (next.Count != 0 && !next.Contains(active))
            active = next.Min();
        if ((selectedRow, selectedColumn) == active && selection.SetEquals(next))
            return;
        var repaint = new HashSet<Cell>(selection);
        repaint.SymmetricExceptWith(next);
        repaint.Add((selectedRow, selectedColumn));
        repaint.Add(active);
        selection = next;
        (selectedRow, selectedColumn) = active;
        foreach (Cell cell in repaint)
            PaintCellSelection(cell);
        profile.SetSelection(active.Row, active.Column);
        surface.SetSelection(active.Row, active.Column);
        baselineSurface.SetSelection(active.Row, active.Column);
        if (operation.SelectedIndex == 0 && !current!.IsText)
            RefreshEntry();
        RefreshCellLocation();
    }

    private void PaintCellSelection(Cell cell)
    {
        Button button = cells[cell];
        bool selected = selection.Contains(cell);
        button.Background = selected ? Selected : cellFills.GetValueOrDefault(cell, DeskUi.Frame);
        button.BorderBrush = selected ? (cell == (selectedRow, selectedColumn) ? Accent : Selected) : Brushes.Transparent;
    }

    private void RefreshCellLocation()
    {
        if (current is null)
            return;
        string target = selection.Count switch
        {
            0 => "No cells selected",
            1 => $"Cell {selectedRow + 1}, {selectedColumn + 1}   ·   0x{current.AddressOf(selectedRow, selectedColumn):X}",
            _ => $"{selection.Count} cells selected"
        };
        cellLocation.Text = target + (current.Units.Length > 0 ? $"   ·   {current.Units}" : "") +
            (reference is null ? "   ·   Red values differ from the original" : "   ·   Red values differ from the comparison BIN");
    }

    private void SelectAllCells()
    {
        if (current is null)
            return;
        scope.SelectedIndex = 0;
        SetSelection(new(AreaTo((-1, -1), default).Positions()), (selectedRow, selectedColumn));
    }

    private void SelectScope()
    {
        if (current is null || cells.Count == 0 || scope.SelectedIndex <= 0)
            return;
        Cell axis = scope.SelectedIndex switch
        {
            1 => (selectedRow, -1),
            2 => (-1, selectedColumn),
            _ => (-1, -1)
        };
        SetSelection(new(AreaTo(axis, axis).Positions()), (selectedRow, selectedColumn));
    }

    private async void TableKey(object? sender, KeyEventArgs e)
    {
        if (current is null)
            return;
        bool extend = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        Cell? next = e.Key switch
        {
            Key.Up => (selectedRow - 1, selectedColumn),
            Key.Down => (selectedRow + 1, selectedColumn),
            Key.Left => (selectedRow, selectedColumn - 1),
            Key.Right => (selectedRow, selectedColumn + 1),
            _ => null
        };
        if (next is Cell point && !ctrl)
        {
            e.Handled = true;
            point = (Math.Clamp(point.Row, 0, current.Rows - 1), Math.Clamp(point.Column, 0, current.Columns - 1));
            scope.SelectedIndex = 0;
            SetSelection(extend ? new(TuneArea.Between(anchor, point).Positions()) : [point], point, !extend);
            cells[point].Focus();
            cells[point].BringIntoView();
            return;
        }
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.A: e.Handled = true; SelectAllCells(); break;
                case Key.C: e.Handled = true; await TableTask(() => CopyCells(extend)); break;
                case Key.V: e.Handled = true; await TableTask(() => PasteCells(TunePaste.Replace)); break;
                case Key.Z: e.Handled = true; Guard(() => ChangeHistory(extend)); break;
                case Key.Y: e.Handled = true; Guard(() => ChangeHistory(true)); break;
            }
        }
        else if (extend && e.Key is Key.Add or Key.Subtract or Key.OemPlus or Key.OemMinus)
        {
            e.Handled = true;
            Guard(() => Nudge(e.Key is Key.Add or Key.OemPlus ? 1 : -1));
        }
    }

    private (TuneImage Image, TuneParameter Map) NeedTable(bool editable = false, bool numeric = false)
    {
        if (image is null || current is null || current.IsText || selection.Count == 0)
            throw new FormatException("Select at least one non-text cell first.");
        if (editable && !current.Editable)
            throw new FormatException("This table is read-only.");
        if (numeric && (current.Mask != 0 || current.Display != TuneFormat.Number))
            throw new FormatException("This tool requires a numeric calibration table.");
        return (image, current);
    }

    private void ApplyTableEdits(IEnumerable<(int Row, int Column, double Value)> edits, string message)
    {
        var (active, map) = NeedTable(editable: true);
        var batch = edits.ToArray();
        if (batch.Length == 0)
            throw new FormatException("There are no editable values inside this selection for that operation.");
        active.Apply(map, batch, AllParameters);
        Paint();
        RefreshFiles();
        Report($"{message} · {batch.Length} cell(s) processed.");
    }

    private void Transform(bool interpolate, TuneDirection direction)
    {
        var (active, map) = NeedTable(editable: true, numeric: true);
        if (selection.Count < 2)
            throw new FormatException("Select two or more cells first.");
        double Read(Cell cell) => active.Read(map, cell.Row, cell.Column);
        ApplyTableEdits(interpolate
            ? TuneGridTools.Interpolate(selection, Read, map.RowLabels, map.ColumnLabels, direction)
            : TuneGridTools.Smooth(selection, Read, direction), interpolate ? "Interpolated" : "Smoothed");
    }

    private void Nudge(int direction)
    {
        var (active, map) = NeedTable(editable: true, numeric: true);
        double step = direction * Math.Pow(10, -(precision ?? map.Decimals));
        ApplyTableEdits(selection.Select(cell => (cell.Row, cell.Column, active.Read(map, cell.Row, cell.Column) + step)),
            "Adjusted by " + step.ToString("G", CultureInfo.InvariantCulture));
    }

    private void SetPrecision(int amount)
    {
        var (_, map) = NeedTable(numeric: true);
        precision = Math.Clamp((precision ?? map.Decimals) + amount, 0, 12);
        Paint(resetEntry: false);
        Report($"Display precision set to {precision} decimal place(s).");
    }

    private async Task CopyCells(bool axes)
    {
        var (active, map) = NeedTable();
        string text = TuneGridTools.Copy(map, selection, cell => active.Read(map, cell.Row, cell.Column), axes);
        await TableClipboard.SetValueAsync(DataFormat.Text, text);
        Report(axes ? "Copied selection with axes." : "Copied selection.");
    }

    private async Task CopyAxis(bool rows)
    {
        var (_, map) = NeedTable();
        await TableClipboard.SetValueAsync(DataFormat.Text, rows
            ? string.Join('\n', map.RowLabels) : string.Join('\t', map.ColumnLabels));
        Report(rows ? "Copied row axis labels." : "Copied column axis labels.");
    }

    private async Task PasteCells(TunePaste mode)
    {
        var (active, map) = NeedTable(editable: true, numeric: mode != TunePaste.Replace);
        HashSet<Cell> destination = selection;
        var clipboard = TableClipboard;
        var ownData = await clipboard.TryGetInProcessDataAsync();
        string? text = ownData is null ? await clipboard.TryGetTextAsync() : await ownData.TryGetTextAsync();
        if (text is null || text.Length == 0)
            throw new FormatException("The clipboard does not contain table values.");
        if (current != map || image != active || selection != destination)
            throw new FormatException("The destination selection changed while reading the clipboard.");
        PasteText(active, map, text, mode);
    }

    private IClipboard TableClipboard => Clipboard ?? throw new InvalidOperationException("Clipboard unavailable.");

    private void PasteText(TuneImage active, TuneParameter map, string text, TunePaste mode) =>
        ApplyTableEdits(TuneGridTools.Paste(map, text, selection, cell => active.Read(map, cell.Row, cell.Column), mode),
            mode == TunePaste.Replace ? "Pasted values" : "Applied paste special");

    private async Task SaveTable()
    {
        var (active, map) = NeedTable();
        string text = TuneGridTools.Copy(map, new HashSet<Cell>(AreaTo((-1, -1), default).Positions()),
            cell => active.Read(map, cell.Row, cell.Column), axes: true);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save table with axes", SuggestedFileName = $"Map-{map.Id:X}", DefaultExtension = "tsv",
            FileTypeChoices = [new FilePickerFileType("Tab-separated table") { Patterns = ["*.tsv"] }]
        });
        if (file?.TryGetLocalPath() is not string path)
            return;
        await File.WriteAllTextAsync(path, text + "\n");
        Report($"Saved {Path.GetFileName(path)}.");
    }

    private async Task LoadTable()
    {
        var (active, map) = NeedTable(editable: true);
        HashSet<Cell> destination = selection;
        string? path = await Pick("Load a tab-separated table", "Table values", "*.tsv");
        if (path is null)
            return;
        if (new FileInfo(path).Length > 1_000_000)
            throw new FormatException("The table file is too large.");
        string text = await File.ReadAllTextAsync(path);
        if (current != map || image != active || selection != destination)
            throw new FormatException("The destination selection changed while reading the table.");
        PasteText(active, map, text, TunePaste.Replace);
    }

    private void RestoreTable()
    {
        var (active, map) = NeedTable(editable: true);
        var baseline = original ?? throw new InvalidOperationException("The original BIN is unavailable.");
        ApplyTableEdits(AreaTo((-1, -1), default).Positions().Select(cell =>
            (cell.Row, cell.Column, baseline.Read(map, cell.Row, cell.Column))), "Restored opened BIN values");
    }

    private void ChangePlottedCell(int row, int column, double value)
    {
        ChooseCell(row, column, focus: false);
        ApplyTableEdits([(row, column, value)], "Adjusted plotted value");
    }
}
