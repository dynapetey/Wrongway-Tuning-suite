using System.Globalization;
using System.Text.Json;

namespace WrongWay;

sealed record TuneMathPreset(string Name, string Formula, double Constant);

static class TuneMaths
{
    public static readonly TuneMathPreset[] Defaults =
    [
        new("Percentage correction", "[X] * (1 + [K] / 100)", 5),
        new("Multiply", "[X] * [K]", 1.05),
        new("Add", "[X] + [K]", 1),
        new("Set value", "[K]", 0),
        new("Blend with comparison (%)", "[X] + ([B] - [X]) * [K] / 100", 50),
        new("Restore comparison value", "[B]", 0)
    ];

    public static string FilePath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "wrongway-suite", "tuner-maths.json");

    public static TuneMathPreset[] Load()
    {
        if (!File.Exists(FilePath))
            return Defaults.ToArray();
        if (new FileInfo(FilePath).Length > 1024 * 1024)
            throw new InvalidDataException("The saved tuner maths file exceeds 1 MiB.");
        var presets = JsonSerializer.Deserialize<TuneMathPreset[]>(File.ReadAllText(FilePath))
            ?? throw new InvalidDataException("The saved tuner maths file is empty.");
        Validate(presets);
        return presets;
    }

    public static void Save(TuneMathPreset[] presets)
    {
        Validate(presets);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(presets, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    public static MathFormula Compile(string expression)
    {
        var formula = new MathFormula(expression);
        foreach (var input in formula.InputSpecs)
            if (input.Operation != ChannelWindowOp.Value || input.BaseToken.ToUpperInvariant() is not
                ("X" or "K" or "B" or "ROW" or "COLUMN"))
                throw new FormatException("Use [X], [K], [B], [Row] or [Column] in a table formula.");
        return formula;
    }

    private static void Validate(TuneMathPreset[] presets)
    {
        if (presets.Length > 100 || presets.Any(p => p is null || string.IsNullOrWhiteSpace(p.Name) ||
                p.Name.Length > 80 || !double.IsFinite(p.Constant)) ||
            presets.Select(p => p.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != presets.Length)
            throw new FormatException("Use up to 100 uniquely named maths presets with finite constants.");
        foreach (var preset in presets)
            Compile(preset.Formula);
    }

    public static (int Row, int Column, double Value)[] Calculate(TuneImage image, TuneParameter parameter,
        IEnumerable<(int Row, int Column)> selection, MathFormula formula, double constant, TuneImage? reference,
        IReadOnlyList<TuneParameter> definitions)
    {
        if (!double.IsFinite(constant))
            throw new FormatException("Enter a finite constant.");
        if (formula.Inputs.Any(input => input.Equals("B", StringComparison.OrdinalIgnoreCase)) && reference is null)
            throw new FormatException("Open a comparison BIN to use [B].");
        var values = new double[formula.Inputs.Count];
        var edits = new List<(int Row, int Column, double Value)>();
        foreach (var (row, column) in selection.OrderBy(cell => cell.Row).ThenBy(cell => cell.Column))
        {
            double value = image.Read(parameter, row, column);
            for (int index = 0; index < values.Length; index++)
                values[index] = formula.Inputs[index].ToUpperInvariant() switch
                {
                    "X" => value,
                    "K" => constant,
                    "B" => reference!.Read(parameter, row, column),
                    "ROW" => Axis(parameter.RowLabels[row]),
                    "COLUMN" => Axis(parameter.ColumnLabels[column]),
                    _ => throw new FormatException("Unknown table formula input.")
                };
            double result = formula.Evaluate(values);
            if (!double.IsFinite(result))
                throw new FormatException($"The formula returned a nonfinite value at row {row + 1}, column {column + 1}.");
            edits.Add((row, column, result));
        }
        if (edits.Count == 0)
            throw new FormatException("Select one or more numeric cells first.");
        // Validate the entire batch, including storage ranges and linked conversions, before previewing it.
        var snapshot = image.Snapshot();
        snapshot.Apply(parameter, edits, definitions);
        return edits.ToArray();
    }

    private static double Axis(string label) =>
        double.TryParse(label, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
            ? value : throw new FormatException($"Axis label '{label}' is not numeric.");

    public static void SelfTest(string? existingBin = null)
    {
        Validate(Defaults);
        foreach (string expression in new[] { "[Unknown] + 1", "[X.avg(500)]", "[7101.10] * 2" })
        {
            try
            {
                Compile(expression);
                throw new InvalidDataException("A scanner channel was accepted as a calibration table input.");
            }
            catch (FormatException) { }
        }
        if (existingBin is null)
            return;
        var image = new TuneImage(existingBin);
        int writable = Enumerable.Range(0x8000, 0x18000).First(address => image.CanWrite(address, 8));
        var parameter = new UserTuneParameter("Maths test", "units", writable, TuneStorage.S16,
            false, 2, 2, "100,200", "1000,2000", "X * 0.5", "").Build(image, []);
        var selected = new[] { (Row: 0, Column: 0), (Row: 0, Column: 1) };
        image.Apply(parameter, [(0, 0, 10), (0, 1, 20), (1, 0, 30), (1, 1, 40)], [parameter]);
        var reference = image.Snapshot();
        var edits = Calculate(image, parameter, selected, Compile("[X] * (1 + [K]/100)"), 10, null, [parameter]);
        if (image.Read(parameter, 0, 0) != 10 || edits[0].Value != 11 || edits[1].Value != 22)
            throw new InvalidDataException("Maths preview changed the BIN or calculated the wrong correction.");
        image.Apply(parameter, edits, [parameter]);
        if (image.Read(parameter, 0, 1) != 22 || image.Read(parameter, 1, 0) != 30)
            throw new InvalidDataException("Maths changed cells outside the selection.");
        image.Undo();
        if (image.Read(parameter, 0, 0) != 10 || image.Read(parameter, 0, 1) != 20)
            throw new InvalidDataException("Undo did not restore the maths batch.");
        edits = Calculate(image, parameter, selected, Compile("[B] + [Row]/100 + [Column]/1000"), 0, reference, [parameter]);
        if (edits[0].Value != 12 || edits[1].Value != 23)
            throw new InvalidDataException("Comparison BIN or axis maths returned incorrect values.");
        foreach (string expression in new[] { "[X]/([Column]-2000)", "if([Column]==2000,1000000,[X])", "[B]" })
        {
            bool rejected = false;
            try { Calculate(image, parameter, selected, Compile(expression), 0, null, [parameter]); }
            catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException) { rejected = true; }
            if (!rejected || image.Read(parameter, 0, 0) != 10 || image.Read(parameter, 0, 1) != 20)
                throw new InvalidDataException("Invalid maths failed to leave the complete selection unchanged.");
        }
    }
}
