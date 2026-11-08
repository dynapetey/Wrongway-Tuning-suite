using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Styling;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace WrongWay;

sealed class TuneWindow : Window
{
    private static readonly IBrush Accent = DeskUi.Blue;
    private static readonly IBrush Muted = DeskUi.Muted;
    private static readonly IBrush Warning = DeskUi.Alert;
    private static readonly IBrush Changed = Brush.Parse("#422B2B");
    private static readonly IBrush Selected = Brush.Parse("#233D65");
    private static readonly IBrush[] Heat = Enumerable.Range(0, 16).Select(index =>
        (IBrush)new SolidColorBrush(Color.FromRgb((byte)(20 + index * 5), (byte)(41 + index * 2), (byte)(77 - index * 3)))).ToArray();
    private static readonly string[] Categories =
        ["All", "OS", "Engine", "Engine Diag", "Trans", "Trans Diag", "Fuel Sys", "System", "Speedo"];
    private readonly TabStrip sections = CategoryTabs();
    private readonly TabStrip groups = CategoryTabs();
    private readonly TextBlock files = DeskUi.Label("No BIN loaded");
    private readonly TextBlock title = DeskUi.Label("Calibration editor", 13, Accent);
    private readonly TextBlock details = DeskUi.Label("", 11, Muted);
    private readonly TextBlock description = new() { TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 12 };
    private readonly ScrollViewer notes;
    private readonly TextBlock status = DeskUi.Label("", 12, Muted);
    private readonly TextBlock itemCount = DeskUi.Label("", 11, Muted);
    private readonly TextBlock cellLocation = DeskUi.Label("", 11, Muted);
    private readonly CheckBox heatMap = new() { Content = "Heat map", FontSize = 11, MinHeight = 25 };
    private readonly TextBox filter = new() { Watermark = "Find a parameter or ID" };
    private readonly TextBox entry = new() { Watermark = "New value", Width = 90 };
    private readonly TextBox decodedText = new()
    {
        Width = 222, FontFamily = new FontFamily("DejaVu Sans Mono"),
        FontSize = 15, Padding = new Thickness(8, 4)
    };
    private readonly TextBlock textHint = DeskUi.Label("· marks a blank or nonprintable byte", 11, Muted);
    private readonly ListBox list = new();
    private readonly ScrollViewer table = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly ComboBox scope = new()
    {
        ItemsSource = new[] { "Cell", "Row", "Column", "Whole table" }, SelectedIndex = 0, Width = 96
    };
    private readonly ComboBox operation = new()
    {
        ItemsSource = new[] { "Set to", "Add", "Multiply", "Change by %" }, SelectedIndex = 0, Width = 107
    };
    private readonly Button pickXdf, compare, save, undo, redo, apply, applyText, warnings;
    private readonly Border editStrip, textStrip;
    private readonly Grid mapRows, mapTop;
    private readonly Border editorPane, referencePane;
    private readonly GridSplitter mapDivider;
    private readonly TunePlot profile = new(surface: false);
    private readonly TunePlot surface = new(surface: true);
    private readonly TunePlot baselineSurface = new(surface: true, angle: -.65);
    private readonly TextBlock profileInfo = DeskUi.Label("", 11, Muted);
    private readonly TextBlock surfaceInfo = DeskUi.Label("", 11, Muted);
    private readonly TextBlock baselineInfo = DeskUi.Label("", 11, Muted);
    private readonly Dictionary<(int Row, int Column), Button> cells = [];
    private TuneImage? image;
    private TuneImage? reference;
    private TuneImage? original;
    private TuneDefinition? definition;
    private TuneParameter? current;
    private int selectedRow, selectedColumn;
    private bool allowClose, dialogOpen, updatingGroups, updatingList;
    private string? lastCopy;

    public TuneWindow()
    {
        Title = "WrongWay Tuner · P59 Map Workspace";
        Width = 1240;
        Height = 760;
        MinWidth = 950;
        MinHeight = 570;
        Background = DeskUi.Frame;
        title.FontWeight = FontWeight.SemiBold;
        sections.ItemsSource = Categories;
        sections.SelectedIndex = 0;
        list.ItemTemplate = new FuncDataTemplate<TuneParameter>((parameter, _) =>
        {
            var label = DeskUi.Label(parameter?.Title ?? "", 12);
            var geometry = DeskUi.Label(parameter is null ? "" : $"{parameter.Rows} × {parameter.Columns}", 11, Muted);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,65"), ColumnSpacing = 4 };
            Put(row, label, 0);
            Put(row, geometry, 0, 1);
            if (parameter is not null)
                ToolTip.SetTip(label, parameter.Title + " · " + parameter.Category);
            return row;
        });
        var itemStyle = new Style(selector => selector.OfType<ListBoxItem>());
        itemStyle.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(8, 3)));
        itemStyle.Setters.Add(new Setter(MinHeightProperty, 23d));
        list.Styles.Add(itemStyle);
        FontFamily = new FontFamily("DejaVu Sans");

        var open = MakeButton("Open BIN", OpenBin, DeskUi.Open);
        pickXdf = MakeButton("Load XDF", OpenXdf);
        compare = MakeButton("Compare BIN", Compare);
        save = MakeButton("Save BIN copy", Save, DeskUi.Save);
        undo = MakeButton("Undo", () => ChangeHistory(redoEdit: false));
        redo = MakeButton("Redo", () => ChangeHistory(redoEdit: true));
        warnings = MakeButton("Skipped XDF items", ShowSkipped);
        apply = MakeButton("Apply", Apply);
        applyText = MakeButton("Apply text", ApplyText);
        entry.KeyDown += (_, args) =>
        {
            if (args.Key == Avalonia.Input.Key.Enter)
                Guard(Apply);
        };
        decodedText.KeyDown += (_, args) =>
        {
            if (args.Key == Avalonia.Input.Key.Enter && !decodedText.IsReadOnly)
                Guard(ApplyText);
        };
        filter.TextChanged += (_, _) => Filter();
        sections.SelectionChanged += (_, _) => UpdateGroups();
        groups.SelectionChanged += (_, _) => { if (!updatingGroups) Filter(); };
        list.SelectionChanged += (_, _) =>
        {
            if (!updatingList)
                Select(list.SelectedItem as TuneParameter);
        };
        heatMap.IsCheckedChanged += (_, _) => Paint(resetEntry: false);
        operation.SelectionChanged += (_, _) => RefreshEntry();
        notes = new ScrollViewer { Content = description, MaxHeight = 62, Padding = new Thickness(6) };
        Closing += async (_, args) =>
        {
            if (allowClose || image?.Dirty != true)
                return;
            args.Cancel = true;
            if (await ConfirmDiscard())
            {
                allowClose = true;
                Close();
            }
        };

        var menu = new Menu
        {
            ItemsSource = new[]
            {
                DeskUi.Menu("_File", DeskUi.Action("Open BIN", open), DeskUi.Action("Load XDF", pickXdf),
                    DeskUi.Action("Save BIN copy", save)),
                DeskUi.Menu("_Edit", DeskUi.Action("Undo", undo), DeskUi.Action("Redo", redo)),
                DeskUi.Menu("_View", DeskUi.Action("Compare BIN", compare), DeskUi.Action("Skipped XDF items", warnings))
            }
        };
        var toolbar = Row(open, pickXdf, save, compare, undo, redo, warnings);
        var header = new StackPanel { Spacing = 2, Children = { title, details } };
        var editor = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        var textLabel = DeskUi.Label("Decoded text", 12, Accent);
        AutomationProperties.SetName(decodedText, "Decoded text");
        editStrip = DeskUi.Strip(Row(scope, operation, entry, apply, heatMap));
        textStrip = DeskUi.Strip(Row(textLabel, decodedText, applyText, textHint));
        Put(editor, DeskUi.Strip(header, DeskUi.Header), 0);
        Put(editor, editStrip, 1);
        Put(editor, textStrip, 1);
        Put(editor, DeskUi.Strip(cellLocation), 2);
        Put(editor, table, 3);
        Put(editor, notes, 4);
        var browser = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        Put(browser, DeskUi.Strip(DeskUi.Section("Maps"), DeskUi.Header), 0);
        filter.Margin = new Thickness(5);
        Put(browser, filter, 1);
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,65") };
        Put(columns, DeskUi.Label("NAME", 10, Muted), 0);
        Put(columns, DeskUi.Label("GEOMETRY", 10, Muted), 0, 1);
        Put(browser, DeskUi.Strip(columns, DeskUi.Header), 2);
        Put(browser, list, 3);
        Put(browser, DeskUi.Strip(itemCount), 4);
        mapTop = new Grid { ColumnDefinitions = new ColumnDefinitions("2.2*,5,*") };
        Put(mapTop, PlotPane("Map profile · 2D", profile, profileInfo), 0);
        Put(mapTop, DeskUi.Splitter(horizontal: false), 0, 1);
        Put(mapTop, PlotPane("Current map · 3D", surface, surfaceInfo, orbit: true), 0, 2);
        var mapBottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,5,1.9*") };
        referencePane = PlotPane("Reference map · 3D", baselineSurface, baselineInfo, orbit: true);
        editorPane = DeskUi.FrameOf(editor);
        Put(mapBottom, referencePane, 0);
        Put(mapBottom, DeskUi.Splitter(horizontal: false), 0, 1);
        Put(mapBottom, editorPane, 0, 2);
        mapRows = new Grid { RowDefinitions = new RowDefinitions("*,5,*") };
        mapDivider = DeskUi.Splitter(horizontal: true);
        Put(mapRows, mapTop, 0);
        Put(mapRows, mapDivider, 1);
        Put(mapRows, mapBottom, 2);
        profile.PickCell = surface.PickCell = baselineSurface.PickCell =
            (row, column) => Guard(() => ChooseCell(row, column, focus: false));
        var workspace = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("265,5,*"), Margin = new Thickness(5)
        };
        workspace.ColumnDefinitions[0].MinWidth = 190;
        workspace.ColumnDefinitions[2].MinWidth = 525;
        Put(workspace, DeskUi.FrameOf(browser), 0);
        Put(workspace, DeskUi.Splitter(horizontal: false), 0, 1);
        Put(workspace, mapRows, 0, 2);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*,Auto") };
        Put(layout, DeskUi.Strip(menu, DeskUi.Surface), 0);
        Put(layout, DeskUi.Strip(toolbar), 1);
        Put(layout, DeskUi.Strip(files), 2);
        var sectionStrip = DeskUi.Strip(sections, DeskUi.Header);
        sectionStrip.Padding = new Thickness(6, 3.5);
        Put(layout, sectionStrip, 3);
        var groupStrip = DeskUi.Strip(new ScrollViewer
        {
            Content = groups, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        }, DeskUi.Surface);
        groupStrip.Padding = new Thickness(6, 3.5);
        Put(layout, groupStrip, 4);
        Put(layout, workspace, 5);
        Put(layout, DeskUi.Strip(status), 6);
        Content = layout;
        status.Text = "Open a full P59 BIN, then load the XML XDF for its operating system.";
        RefreshFiles();
        UpdateGroups();
        Select(null);
    }

    public void Load(string bin, string xdf) => Guard(() =>
    {
        SetImage(bin);
        SetDefinition(xdf);
    });

    private async Task OpenBin()
    {
        string? path = await Pick("Open a P59 BIN", "BIN image", "*.bin");
        if (path is not null && (image?.Dirty != true || await ConfirmDiscard()))
            SetImage(path);
    }

    private void SetImage(string path)
    {
        var candidate = new TuneImage(path);
        var baseline = candidate.Snapshot();
        image = candidate;
        original = baseline;
        reference = null;
        definition = null;
        lastCopy = null;
        UpdateGroups();
        RefreshFiles();
        Report("BIN validated. Load the XDF for this operating system.");
    }

    private async Task OpenXdf()
    {
        string? path = await Pick("Load the matching XDF", "TunerPro XDF", "*.xdf");
        if (path is not null)
            SetDefinition(path);
    }

    private void SetDefinition(string path)
    {
        definition = TuneDefinition.Load(path, image!);
        UpdateGroups();
        RefreshFiles();
        Report($"{definition.Parameters.Count} items loaded · {definition.Parameters.Count(parameter => parameter.Editable)} editable · {definition.Skipped.Count} skipped.");
    }

    private async Task Compare()
    {
        string? path = await Pick("Compare with another BIN for the same OS", "BIN image", "*.bin");
        if (path is null)
            return;
        var candidate = new TuneImage(path);
        if (!image!.CompatibleWith(candidate))
            throw new InvalidDataException("The comparison BIN must have the same OS and segment layout.");
        reference = candidate;
        RefreshFiles();
        Paint(resetEntry: current?.IsText != true);
        Report("Red values differ from the comparison BIN. Hover a cell to see its reference value.");
    }

    private async Task Save()
    {
        TuneImage active = image!;
        var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save a separate tuned BIN",
            SuggestedFileName = Path.GetFileNameWithoutExtension(active.Path) + "-edited.bin",
            DefaultExtension = "bin",
            FileTypeChoices = [new FilePickerFileType("BIN image") { Patterns = ["*.bin"] }]
        });
        string? path = destination?.TryGetLocalPath();
        if (path is null)
            return;
        active.SaveCopy(path);
        lastCopy = Path.GetFileName(path);
        RefreshFiles();
        Report($"Saved {lastCopy}. PCM Hammer checksum validation passed.");
    }

    private async Task<string?> Pick(string caption, string type, string pattern)
    {
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = caption, AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(type) { Patterns = [pattern] }]
        });
        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    private static string Primary(TuneParameter parameter)
    {
        string source = parameter.Category.Split(" / ", 2)[0].ToLowerInvariant();
        return source switch
        {
            "calibration info" or "os hardcoded parameters" or "os" => "OS",
            "engine calibration" or "engine parameters" or "engine" => "Engine",
            "engine diagnostics" => "Engine Diag",
            "transmission calibration" or "transmission" => "Trans",
            "transmission diagnostics" => "Trans Diag",
            "fuel system" => "Fuel Sys",
            "speedometer" or "speedo" => "Speedo",
            _ => "System"
        };
    }

    private static string Group(TuneParameter parameter)
    {
        string[] path = parameter.Category.Split(" / ", 3);
        return path.Length > 1 ? path[1] : "General";
    }

    private IEnumerable<TuneParameter> InSection()
    {
        string section = sections.SelectedItem as string ?? "All";
        return (definition?.Parameters ?? []).Where(parameter => section == "All" || Primary(parameter) == section);
    }

    private void UpdateGroups()
    {
        updatingGroups = true;
        bool scoped = sections.SelectedIndex > 0;
        var available = scoped ? InSection().Select(Group).Distinct().Order().ToArray() : [];
        groups.ItemsSource = new[] { "All parameters" }.Concat(available).ToArray();
        groups.SelectedIndex = 0;
        groups.IsEnabled = available.Length > 0;
        updatingGroups = false;
        Filter();
    }

    private void Filter()
    {
        string search = filter.Text?.Trim() ?? "";
        string? group = groups.SelectedIndex > 0 ? groups.SelectedItem as string : null;
        TuneParameter[] visible = InSection().Where(parameter =>
            (group is null || Group(parameter) == group) &&
            (parameter.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                parameter.Category.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        var next = current is not null && visible.Contains(current) ? current
            : (search.Length == 0 ? visible.FirstOrDefault(parameter => parameter.Editable && IsPlottable(parameter)
                && parameter.Title.Contains("Main VE", StringComparison.OrdinalIgnoreCase)) : null)
              ?? visible.FirstOrDefault(parameter => parameter.Editable) ?? visible.FirstOrDefault();
        updatingList = true;
        list.ItemsSource = visible;
        list.SelectedItem = next;
        updatingList = false;
        if (next != current)
            Select(next);
        itemCount.Text = $"{visible.Length:N0} parameters";
    }

    private void Select(TuneParameter? parameter)
    {
        current = parameter;
        selectedRow = selectedColumn = 0;
        cells.Clear();
        table.Content = null;
        table.Offset = default;
        title.Text = parameter is null ? "Calibration editor" : $"Table · {parameter.Title}";
        notes.IsVisible = !string.IsNullOrWhiteSpace(parameter?.Notes);
        description.Text = parameter?.Notes;
        textStrip.IsVisible = parameter?.IsText == true;
        editStrip.IsVisible = !textStrip.IsVisible;
        decodedText.IsReadOnly = parameter?.Editable != true;
        applyText.IsVisible = parameter?.Editable == true;
        cellLocation.Text = "";
        bool charts = IsPlottable(parameter);
        mapRows.RowDefinitions[0].Height = charts ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        mapRows.RowDefinitions[1].Height = new GridLength(charts ? 5 : 0);
        mapTop.IsVisible = mapDivider.IsVisible = referencePane.IsVisible = charts;
        Grid.SetColumn(editorPane, charts ? 2 : 0);
        Grid.SetColumnSpan(editorPane, charts ? 1 : 3);
        if (parameter is null)
            RefreshEntry();
        details.Text = parameter is null
            ? "Tables, scalars and switches appear here after loading a matching XDF."
            : $"{parameter.Category}   ·   {parameter.Rows} × {parameter.Columns}   ·   0x{parameter.BaseAddress:X}" +
              (parameter.Units.Length > 0 ? $"   ·   {parameter.Units}" : "") +
              (parameter.Editable ? "" : "   ·   Read-only");
        if (parameter is not null)
            BuildTable(parameter);
        RefreshButtons();
    }

    private void BuildTable(TuneParameter parameter)
    {
        bool isMap = parameter.Rows > 1 || parameter.Columns > 1;
        var grid = new Grid
        {
            RowSpacing = 1, ColumnSpacing = 1, Background = DeskUi.Edge, Margin = new Thickness(5),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        for (int row = 0; row <= parameter.Rows; row++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int column = 0; column <= parameter.Columns; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        if (isMap)
        {
            string axes = string.Join(" / ", new[] { parameter.RowUnits, parameter.ColumnUnits }.Where(unit => unit.Length > 0));
            Put(grid, Axis(axes), 0);
            for (int column = 0; column < parameter.Columns; column++)
                Put(grid, Axis(parameter.ColumnLabels[column]), 0, column + 1);
            for (int row = 0; row < parameter.Rows; row++)
                Put(grid, Axis(parameter.RowLabels[row]), row + 1);
        }

        for (int row = 0; row < parameter.Rows; row++)
        for (int column = 0; column < parameter.Columns; column++)
        {
            var position = (Row: row, Column: column);
            var cell = new Button
            {
                MinWidth = parameter.IsText ? 30 : isMap ? 54 : 135, Padding = new Thickness(4, 2),
                FontSize = 11.5, MinHeight = 23, CornerRadius = new CornerRadius(0),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            cell.Click += (_, _) => Guard(() => ChooseCell(position.Row, position.Column, focus: true));
            cells[position] = cell;
            Put(grid, cell, row + 1, column + 1);
        }
        table.Content = grid;
        Paint();
    }

    private void Paint(bool resetEntry = true)
    {
        if (image is null || original is null || current is null)
            return;
        var values = new Dictionary<(int Row, int Column), (double Value, double? Baseline)>();
        foreach (var (position, button) in cells)
        {
            var (row, column) = position;
            try
            {
                double value = image.Read(current, row, column);
                double? baseline = null;
                try { baseline = (reference ?? original).Read(current, row, column); }
                catch (Exception ex) when (ex is FormatException or InvalidDataException) { }
                values[position] = (value, baseline);
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException)
            {
                button.Content = "—";
                button.Background = DeskUi.Frame;
                ToolTip.SetTip(button, ex.Message);
            }
        }
        double low = values.Count > 0 ? values.Values.Min(item => item.Value) : 0;
        double range = values.Count > 0 ? values.Values.Max(item => item.Value) - low : 0;
        bool shade = !current.IsText && heatMap.IsChecked == true && range > 0 && double.IsFinite(range);
        string DisplayValue(double value) => current.IsText
            ? $"'{current.Format(value)}' (0x{(byte)value:X2})"
            : current.Format(value, forEdit: true);
        foreach (var (position, item) in values)
        {
            var (value, baseline) = item;
            Button button = cells[position];
            bool changed = baseline is not null && baseline != value;
            bool selected = position == (selectedRow, selectedColumn);
            int heat = shade ? (int)Math.Clamp((value - low) / range * (Heat.Length - 1), 0, Heat.Length - 1) : 0;
            button.Content = current.Format(value);
            button.Background = selected ? Selected : changed ? Changed : shade ? Heat[heat] :
                position.Row % 2 == 0 ? DeskUi.Surface : DeskUi.Frame;
            button.BorderBrush = selected ? Accent : Brushes.Transparent;
            button.Foreground = changed ? Warning : DeskUi.Ink;
            ToolTip.SetTip(button, $"Value {DisplayValue(value)}  ·  Reference {(baseline is double prior ? DisplayValue(prior) : "—")}");
        }

        if (IsPlottable(current))
        {
            var decoded = new double[current.Rows, current.Columns];
            var baseline = new double[current.Rows, current.Columns];
            double baseLow = low, baseHigh = low + range;
            for (int row = 0; row < current.Rows; row++)
            for (int col = 0; col < current.Columns; col++)
            {
                bool hasValue = values.TryGetValue((row, col), out var item);
                decoded[row, col] = hasValue ? item.Value : double.NaN;
                baseline[row, col] = hasValue ? item.Baseline ?? double.NaN : double.NaN;
                if (double.IsFinite(baseline[row, col]))
                {
                    baseLow = Math.Min(baseLow, baseline[row, col]);
                    baseHigh = Math.Max(baseHigh, baseline[row, col]);
                }
            }
            profile.SetData(current, decoded, low, low + range, selectedRow, selectedColumn);
            surface.SetData(current, decoded, baseLow, baseHigh, selectedRow, selectedColumn);
            baselineSurface.SetData(current, baseline, baseLow, baseHigh, selectedRow, selectedColumn);
            profileInfo.Text = current.Title;
            surfaceInfo.Text = $"{current.Rows} × {current.Columns}   ·   {current.Units}".TrimEnd(' ', '·');
            baselineInfo.Text = reference is null ? "Original BIN" : Path.GetFileName(reference.Path);
        }
        if (resetEntry)
            RefreshEntry();
        cellLocation.Text = $"Cell {selectedRow + 1}, {selectedColumn + 1}   ·   0x{current.AddressOf(selectedRow, selectedColumn):X}" +
            (current.Units.Length > 0 ? $"   ·   {current.Units}" : "") +
            (reference is null ? "   ·   Red values differ from the original" : "   ·   Red values differ from the comparison BIN");
    }

    private static bool IsPlottable(TuneParameter? map) => map is { Display: TuneFormat.Number, Mask: 0 }
        && (map.Rows > 1 || map.Columns > 1);

    private void ChooseCell(int row, int column, bool focus)
    {
        if (current is null || !cells.TryGetValue((row, column), out var cell))
            return;
        (selectedRow, selectedColumn) = (row, column);
        Paint(resetEntry: !current.IsText);
        if (!focus)
            cell.BringIntoView();
        else if (current.Editable && !current.IsText)
        {
            entry.Focus();
            entry.SelectAll();
        }
    }

    private Border PlotPane(string titleText, TunePlot plot, TextBlock info, bool orbit = false)
    {
        var header = new DockPanel();
        if (orbit)
        {
            Button reset = MakeButton("Reset view", plot.ResetView);
            DockPanel.SetDock(reset, Dock.Right);
            header.Children.Add(reset);
        }
        header.Children.Add(DeskUi.Section(titleText));
        var pane = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Put(pane, DeskUi.Strip(header, DeskUi.Header), 0);
        Put(pane, plot, 1);
        Put(pane, DeskUi.Strip(info), 2);
        return DeskUi.FrameOf(pane);
    }

    private void RefreshEntry()
    {
        if (image is null || current is null)
        {
            entry.Text = "";
            return;
        }
        if (current.IsText)
        {
            string value = image.ReadText(current);
            if (decodedText.Text != value)
                decodedText.Text = value;
            textHint.IsVisible = value.Contains('·');
            return;
        }
        if (operation.SelectedIndex > 0)
        {
            entry.Text = "";
            entry.Watermark = operation.SelectedIndex == 2 ? "Multiplier" : "Amount";
            return;
        }
        entry.Watermark = current.Mask != 0 ? "On or Off" : current.Display == TuneFormat.Hex ? "Hex value" : "New value";
        try { entry.Text = current.Format(image.Read(current, selectedRow, selectedColumn), forEdit: true); }
        catch (Exception ex) when (ex is FormatException or InvalidDataException) { entry.Text = ""; }
    }

    private void Apply()
    {
        if (image is null || current?.Editable != true || current.IsText)
            return;
        double operand = current.ParseEdit(entry.Text ?? "", setValue: operation.SelectedIndex == 0);
        var values = new List<(int Row, int Column, double Value)>();
        for (int row = 0; row < current.Rows; row++)
        for (int column = 0; column < current.Columns; column++)
        {
            if ((scope.SelectedIndex is 0 or 1 && row != selectedRow) ||
                (scope.SelectedIndex is 0 or 2 && column != selectedColumn))
                continue;
            double before = operation.SelectedIndex == 0 ? 0 : image.Read(current, row, column);
            double value = operation.SelectedIndex switch
            {
                1 => before + operand,
                2 => before * operand,
                3 => before * (1 + operand / 100),
                _ => operand
            };
            values.Add((row, column, value));
        }
        image.Apply(current, values, definition!.Parameters);
        Paint();
        RefreshFiles();
        Report($"Applied to {values.Count} cell(s) · Stored selection {current.Format(image.Read(current, selectedRow, selectedColumn))} {current.Units}".TrimEnd());
    }

    private void ApplyText()
    {
        if (image is null || current?.IsText != true)
            return;
        image.ApplyText(current, decodedText.Text ?? "", definition!.Parameters);
        Paint();
        RefreshFiles();
        Report($"Applied text to {current.Title}.");
    }

    private void ChangeHistory(bool redoEdit)
    {
        if (redoEdit)
            image?.Redo();
        else
            image?.Undo();
        Paint();
        RefreshFiles();
        Report(redoEdit ? "Edit redone." : "Edit undone.");
    }

    private async Task<bool> ConfirmDiscard()
    {
        if (dialogOpen)
            return false;
        dialogOpen = true;
        try
        {
            var prompt = new Window
            {
                Title = "Unsaved calibration", Width = 420, Height = 155, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var keep = new Button { Content = "Keep editing" };
            var discard = new Button { Content = "Discard edits" };
            keep.Click += (_, _) => prompt.Close(false);
            discard.Click += (_, _) => prompt.Close(true);
            prompt.Content = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 16,
                Children = { new TextBlock { Text = "Discard the unsaved changes to this BIN?", TextWrapping = TextWrapping.Wrap }, Row(keep, discard) }
            };
            return await prompt.ShowDialog<bool>(this);
        }
        finally
        {
            dialogOpen = false;
        }
    }

    private async Task ShowSkipped()
    {
        if (definition is null)
            return;
        var report = new Window
        {
            Title = "Skipped XDF items", Width = 680, Height = 370,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ListBox { ItemsSource = definition.Skipped, Margin = new Thickness(12) }
        };
        await report.ShowDialog(this);
    }

    private void RefreshFiles()
    {
        files.Text = image is null ? "No BIN loaded" :
            $"{Path.GetFileName(image.Path)}   ·   P59 OS {image.Os}   ·   {definition?.Title ?? "No XDF loaded"}" +
            (reference is null ? "" : $"   ·   Compare {Path.GetFileName(reference.Path)}") +
            (lastCopy is null ? "" : $"   ·   Saved {lastCopy}") +
            (image.Dirty ? "   ·   Unsaved edits" : "");
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        pickXdf.IsEnabled = image is not null;
        compare.IsEnabled = definition is not null;
        warnings.IsEnabled = definition?.Skipped.Count > 0;
        save.IsEnabled = definition is not null;
        undo.IsEnabled = image?.CanUndo == true;
        redo.IsEnabled = image?.CanRedo == true;
        apply.IsEnabled = entry.IsEnabled = current?.Editable == true && !current.IsText;
        bool isMap = current is not null && (current.Rows > 1 || current.Columns > 1);
        scope.IsEnabled = apply.IsEnabled && isMap;
        heatMap.IsEnabled = isMap;
        if (!isMap)
            scope.SelectedIndex = 0;
        operation.IsEnabled = apply.IsEnabled && current!.Mask == 0;
        if (!operation.IsEnabled)
            operation.SelectedIndex = 0;
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { Report(ex.Message, error: true); }
    }

    private Button MakeButton(string label, Action action)
    {
        var button = DeskUi.Tool(label);
        button.Click += (_, _) => Guard(action);
        return button;
    }

    private Button MakeButton(string label, Func<Task> action, string? glyph = null)
    {
        var button = DeskUi.Tool(label, glyph);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(); }
            catch (Exception ex) { Report(ex.Message, error: true); }
            finally
            {
                button.IsEnabled = true;
                RefreshButtons();
            }
        };
        return button;
    }

    private void Report(string message, bool error = false)
    {
        status.Text = message;
        status.Foreground = error ? Warning : Muted;
        ToolTip.SetTip(status, message);
    }

    private static TabStrip CategoryTabs()
    {
        var tabs = new TabStrip
        {
            ItemTemplate = new FuncDataTemplate<string>((label, _) => DeskUi.Label(label ?? "", 13))
        };
        var style = new Style(selector => selector.OfType<TabStripItem>());
        style.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(9, 5.5)));
        style.Setters.Add(new Setter(MinHeightProperty, 33d));
        tabs.Styles.Add(style);
        return tabs;
    }

    private static Border Axis(string text)
    {
        var label = DeskUi.Section(text);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        return new Border { Background = DeskUi.Header, Padding = new Thickness(5, 3), Child = label };
    }

    private static StackPanel Row(params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (Control control in controls)
            row.Children.Add(control);
        return row;
    }

    private static void Put(Grid grid, Control control, int row, int column = 0)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }
}
