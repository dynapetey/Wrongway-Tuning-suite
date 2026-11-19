using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace WrongWay;

sealed partial class TuneWindow
{
    private async Task ApplyMaths()
    {
        var (active, map) = NeedTable(editable: true, numeric: true);
        var selected = selection.ToArray();
        var presets = TuneMaths.Load();
        var dialog = new Window
        {
            Title = "Maths · " + map.Title, Width = 690, Height = 540,
            MinWidth = 540, MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = DeskUi.Frame, ShowInTaskbar = false
        };
        var preset = new ComboBox { ItemsSource = presets.Select(item => item.Name).ToArray() };
        var expression = new TextBox { Text = "[X] * (1 + [K] / 100)", MaxLength = 1024 };
        var constant = new TextBox { Text = "5" };
        var preview = new SelectableTextBlock
        {
            Foreground = DeskUi.Ink, FontFamily = new FontFamily("DejaVu Sans Mono"), FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        var error = new TextBlock { Foreground = Warning, TextWrapping = TextWrapping.Wrap };
        var applyMath = DeskUi.Tool("Apply to selection");
        applyMath.IsEnabled = false;
        (int Row, int Column, double Value)[]? pending = null;

        void Invalidate()
        {
            pending = null;
            applyMath.IsEnabled = false;
            preview.Text = "Preview the formula to check its results before applying.";
            error.Text = "";
        }
        expression.TextChanged += (_, _) => Invalidate();
        constant.TextChanged += (_, _) => Invalidate();
        preset.SelectionChanged += (_, _) =>
        {
            if (preset.SelectedIndex < 0)
                return;
            expression.Text = presets[preset.SelectedIndex].Formula;
            constant.Text = presets[preset.SelectedIndex].Constant.ToString("G", CultureInfo.InvariantCulture);
        };

        var calculate = MakeButton("Preview", () =>
        {
            Invalidate();
            try
            {
                if (!double.TryParse(constant.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double amount))
                    throw new FormatException("Enter a numeric constant.");
                var formula = TuneMaths.Compile(expression.Text ?? "");
                var edits = TuneMaths.Calculate(active, map, selected, formula, amount, reference, AllParameters);
                var quantized = active.Snapshot();
                quantized.Apply(map, edits, AllParameters);
                preview.Text = $"{edits.Length} selected cell(s) · {map.Units}\n\nRow / Column    Current → Result\n" +
                    string.Join("\n", edits.Take(40).Select(edit =>
                        $"{edit.Row + 1,4} / {edit.Column + 1,-4}   " +
                        $"{map.Format(active.Read(map, edit.Row, edit.Column))} → {map.Format(quantized.Read(map, edit.Row, edit.Column))}")) +
                    (edits.Length > 40 ? $"\n…and {edits.Length - 40} more cells." : "");
                pending = edits;
                applyMath.IsEnabled = true;
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException or InvalidOperationException)
            {
                error.Text = ex.Message;
            }
        });
        applyMath.Click += (_, _) =>
        {
            if (pending is null)
                return;
            try
            {
                ApplyTableEdits(pending, "Applied maths formula");
                dialog.Close();
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        var help = new TextBlock
        {
            Text = "[X] current decoded cell · [K] constant · [B] comparison BIN cell\n" +
                "[Row] and [Column] numeric axis labels\n" +
                "Functions include abs, min, max, pow, sqrt, round, clamp and if.",
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, FontSize = 12
        };
        var fields = DeskUi.Fields(("Preset", preset), ("Formula", expression), ("Constant [K]", constant));
        fields.Children.Add(help);
        var root = new Grid { Margin = new Thickness(14), RowSpacing = 10,
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };
        Put(root, fields, 0);
        Put(root, new ScrollViewer { Content = preview }, 1);
        Put(root, error, 2);
        Put(root, Row(calculate, applyMath, MakeButton("Cancel", dialog.Close)), 3);
        dialog.Content = root;
        Invalidate();
        await dialog.ShowDialog(this);
    }

    private async Task EditMathsPresets()
    {
        var saved = await DeskUi.EditList(this, "Tuner maths presets",
            "Save formulas for selected decoded cells. Use [X], [K], [B], [Row] and [Column].",
            TuneMaths.Load(), 100, (owner, item) =>
            {
                var name = new TextBox { Text = item?.Name, MaxLength = 80 };
                var formula = new TextBox { Text = item?.Formula ?? "[X]", MaxLength = 1024 };
                var constant = new TextBox { Text = (item?.Constant ?? 0).ToString("G", CultureInfo.InvariantCulture) };
                return DeskUi.Form(owner, item is null ? "Add maths preset" : "Edit maths preset",
                    DeskUi.Fields(("Name", name), ("Formula", formula), ("Default constant [K]", constant)), () =>
                    {
                        if (string.IsNullOrWhiteSpace(name.Text))
                            throw new FormatException("Enter a preset name.");
                        if (!double.TryParse(constant.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                            !double.IsFinite(value))
                            throw new FormatException("Enter a finite numeric constant.");
                        TuneMaths.Compile(formula.Text ?? "");
                        return new TuneMathPreset(name.Text.Trim(), formula.Text!.Trim(), value);
                    }, height: 300);
            }, items => items.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Count
                ? "Give each maths preset a unique name." : null);
        if (saved is null)
            return;
        TuneMaths.Save(saved);
        Report($"Saved {saved.Length} tuner maths preset(s).");
    }
}
