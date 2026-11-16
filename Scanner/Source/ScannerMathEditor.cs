using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace WrongWay;

sealed partial class ScannerWindow
{
    private MathChannel[] mathChannels = [];

    private void RefreshMaths()
    {
        mathChannels = log is null ? [] : MathChannel.Build(log, scannerLayout.Maths ?? MathSetting.Defaults);
    }

    private async Task EditMaths()
    {
        if (hardware is not null || busy)
            return;
        var result = await EditList("Maths",
            "Create named formula channels for the chart, gauges, and graph. Inputs use recorded units. Bracket a channel name or PID.",
            scannerLayout.Maths ?? MathSetting.Defaults, MathSetting.Maximum, EditMath,
            items => items.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == items.Count
                ? null : "Give each math channel a unique name.");
        if (result is not null)
        {
            string message = "Maths saved.";
            if (log is { } current)
            {
                int unavailable = result.Count(setting => setting.Enabled) -
                    result.Count(setting => setting.Enabled && new MathFormula(setting.Formula).Inputs.All(
                        input => MathChannel.Resolve(current.Channels, input) is not null));
                if (unavailable > 0)
                    message += $" {unavailable} formulas need channels that are missing or ambiguous in this log.";
            }
            ApplyLayout(scannerLayout with { Maths = result }, message);
        }
    }

    private Task<MathSetting?> EditMath(Window owner, MathSetting? setting)
    {
        var name = Field(setting?.Name ?? "");
        var unit = Field(setting?.Unit ?? "");
        var formula = Field(setting?.Formula ?? "");
        formula.MaxLength = 512;
        var enabled = new CheckBox { Content = "Calculate this channel", IsChecked = setting?.Enabled ?? true };
        var sources = log?.Channels.Where(channel => !channel.IsMath).Select(MathToken).Distinct().ToArray() ?? [];
        var source = Choice(sources, sources.Length > 0 ? 0 : -1);
        if (sources.Length == 0)
            source.PlaceholderText = "Open a log to list its inputs";
        var insert = Button("Insert channel", () =>
        {
            if (source.SelectedItem is not string token)
                return;
            string existing = formula.Text ?? "";
            int offset = Math.Clamp(formula.CaretIndex, 0, existing.Length);
            string inserted = existing.Insert(offset, token);
            if (inserted.Length > formula.MaxLength)
                return;
            formula.Text = inserted;
            formula.CaretIndex = offset + token.Length;
            formula.Focus();
        });
        insert.IsEnabled = sources.Length > 0;
        var hint = new TextBlock
        {
            Text = "Use [12] for PID 12 or [name|unit] for an exact channel. Functions include abs, sqrt, min, max, pow and if. Example: ([7101.10] / 0.5) + 9.57.",
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5)
        };
        var fields = DeskUi.Fields(("Name", name), ("Output units", unit), ("Formula", formula),
            ("Recorded channel", source));
        fields.Children.Add(insert);
        fields.Children.Add(hint);
        fields.Children.Add(enabled);

        return DeskUi.Form(owner, setting is null ? "Add Math Channel" : "Edit Math Channel", fields, () =>
        {
            var next = new MathSetting(name.Text?.Trim() ?? "", unit.Text?.Trim() ?? "",
                formula.Text?.Trim() ?? "", enabled.IsChecked == true, setting?.Id ?? Guid.NewGuid());
            if (next.Name.Length is < 1 or > 120 || next.Unit.Length > 80)
                throw new FormatException("Enter a name of up to 120 characters and units of up to 80 characters.");
            _ = new MathFormula(next.Formula);
            return next;
        }, height: 425);
    }

    private static string MathToken(Channel channel)
    {
        if (channel.Name.IndexOfAny(['[', ']', '|', '\r', '\n']) < 0 &&
            channel.Unit.IndexOfAny(['[', ']', '\r', '\n']) < 0)
            return $"[{channel.Name}|{channel.Unit}]";
        return channel.Pid is int pid ? $"[{pid}]" : $"[{channel.Name}]";
    }
}
