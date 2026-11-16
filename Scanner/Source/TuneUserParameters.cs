using System.Globalization;
using Avalonia.Controls;

namespace WrongWay;

sealed partial class TuneWindow
{
    private UserTuneParameter[] userDefinitions = [];
    private TuneParameter[] userParameters = [];
    private bool userDefinitionsReadable = true;
    private IReadOnlyList<TuneParameter> AllParameters => [.. (definition?.Parameters ?? []), .. userParameters];

    private int BuildUserParameters()
    {
        var valid = new List<TuneParameter>();
        foreach (var item in userDefinitions)
        {
            try { valid.Add(item.Build(image!, definition?.Parameters ?? [])); }
            catch (FormatException) { }
        }
        userParameters = valid.ToArray();
        return userDefinitions.Length - valid.Count;
    }

    private async Task EditUserParameters()
    {
        var target = image!;
        var xdf = definition?.Parameters ?? [];
        var result = await DeskUi.EditList(this, $"User Defined Parameters · OS {target.Os}",
            "Define a scalar or row-major table. Addresses are BIN file offsets. Use X for raw value and L for an optional linked scalar.",
            userDefinitions, UserTuneParameter.Maximum,
            (owner, parameter) => EditUserParameter(owner, target, xdf, parameter),
            items => items.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Count
                ? "Give each user-defined parameter a unique name." : null);
        if (result is null)
            return;
        var validated = result.Select(item => item.Build(target, xdf)).ToArray();
        UserTuneParameter.Save(target.Os, result);
        userDefinitions = result;
        userParameters = validated;
        sections.SelectedItem = "User Defined";
        filter.Text = "";
        UpdateGroups();
        RefreshFiles();
        Report($"Saved {result.Length} user-defined parameters for OS {target.Os}.");
    }

    private static Task<UserTuneParameter?> EditUserParameter(Window owner, TuneImage target,
        IReadOnlyList<TuneParameter> xdf, UserTuneParameter? item)
    {
        TextBox Field(string? value = null) => new() { Text = value, MaxLength = 800 };
        var name = Field(item?.Name);
        var units = Field(item?.Units);
        var address = Field(item is null ? "" : $"0x{item.Address:X}");
        var storage = new ComboBox { ItemsSource = Enum.GetValues<TuneStorage>(),
            SelectedItem = item?.Storage ?? TuneStorage.U16 };
        var endian = new CheckBox { Content = "Little-endian", IsChecked = item?.LittleEndian ?? false };
        var rows = Field(item?.Rows.ToString(CultureInfo.InvariantCulture) ?? "1");
        var columns = Field(item?.Columns.ToString(CultureInfo.InvariantCulture) ?? "1");
        var rowLabels = Field(item?.RowLabels);
        var columnLabels = Field(item?.ColumnLabels);
        var equation = Field(item?.Equation ?? "X");
        var scalars = new[] { "(None)" }
            .Concat(xdf.Where(p => !p.IsText && p.Rows == 1 && p.Columns == 1).Select(p => p.Title).Order())
            .ToArray();
        int linkedIndex = string.IsNullOrWhiteSpace(item?.LinkedParameter)
            ? 0
            : Array.FindIndex(scalars, s => s.Equals(item.LinkedParameter, StringComparison.OrdinalIgnoreCase));
        var linked = new ComboBox { ItemsSource = scalars, SelectedIndex = Math.Max(0, linkedIndex) };
        var notes = Field(item?.Notes);
        return DeskUi.Form(owner, item is null ? "Add User Defined Parameter" : "Edit User Defined Parameter",
            DeskUi.Fields(("Name", name), ("BIN offset (hex 0x… or decimal)", address),
                ("Storage type", storage), ("Byte order (unchecked uses big-endian)", endian),
                ("Rows", rows), ("Columns", columns), ("Row labels (optional, comma-separated)", rowLabels),
                ("Column labels (optional, comma-separated)", columnLabels),
                ("Linked scalar as L (optional)", linked),
                ("Conversion equation (X = raw, L = linked)", equation), ("Display units", units), ("Notes", notes)), () =>
            {
                string offset = address.Text?.Trim() ?? "";
                bool hex = offset.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                if (!int.TryParse(hex ? offset[2..] : offset,
                    hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                    throw new FormatException("Enter a BIN file offset such as 0x10008.");
                if (!int.TryParse(rows.Text, out int height) || !int.TryParse(columns.Text, out int width))
                    throw new FormatException("Rows and columns must be whole numbers.");
                string? linkedTitle = linked.SelectedIndex > 0 ? linked.SelectedItem as string : null;
                var entered = new UserTuneParameter(name.Text?.Trim() ?? "", units.Text?.Trim() ?? "",
                    index, (TuneStorage)storage.SelectedItem!, endian.IsChecked == true, height, width,
                    rowLabels.Text?.Trim() ?? "", columnLabels.Text?.Trim() ?? "",
                    equation.Text?.Trim() ?? "", notes.Text?.Trim() ?? "", linkedTitle);
                entered.Build(target, xdf);
                return entered;
            }, height: 700);
    }
}
