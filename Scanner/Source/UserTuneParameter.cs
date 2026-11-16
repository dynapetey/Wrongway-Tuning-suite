using System.Globalization;
using System.Text.Json;

namespace WrongWay;

enum TuneStorage { U8, S8, U16, S16, U32, S32, Float32 }

sealed record UserTuneParameter(string Name, string Units, int Address, TuneStorage Storage,
    bool LittleEndian, int Rows, int Columns, string RowLabels, string ColumnLabels,
    string Equation, string Notes, string? LinkedParameter = null)
{
    public const int Maximum = 128;

    public TuneParameter Build(TuneImage image, IReadOnlyList<TuneParameter> xdf)
    {
        if (Name is not { Length: > 0 and <= 120 } || Units is not { Length: <= 80 } ||
            Equation is not { Length: > 0 and <= 250 } || Notes is not { Length: <= 800 } ||
            !Enum.IsDefined(Storage) || Rows is < 1 or > 128 || Columns is < 1 or > 128 || Rows * Columns > 4096)
            throw new FormatException("Enter a name, conversion, storage type, and a table with at most 4,096 cells.");

        int width = Storage switch
        {
            TuneStorage.U8 or TuneStorage.S8 => 1,
            TuneStorage.U16 or TuneStorage.S16 => 2,
            _ => 4
        };
        int size = Rows * Columns * width;
        if (!image.CanWrite(Address, size))
            throw new FormatException("The parameter must fit inside one writable calibration segment.");
        if (xdf.Where(item => !item.Editable).Any(item => Overlaps(item, Address, size)))
            throw new FormatException("The parameter overlaps a read-only XDF item.");

        TuneParameter? link = null;
        if (!string.IsNullOrWhiteSpace(LinkedParameter))
        {
            var matches = xdf.Where(item => !item.IsText && item.Rows == 1 && item.Columns == 1 &&
                item.Title.Equals(LinkedParameter.Trim(), StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length != 1)
                throw new FormatException($"Choose a unique linked scalar from the loaded XDF. '{LinkedParameter}' did not match exactly one.");
            link = matches[0];
            if (Overlaps(link, Address, size))
                throw new FormatException("The linked scalar overlaps this parameter's storage.");
        }
        var equation = new TuneEquation(Equation, link is null ? null : "L");
        bool signed = Storage is TuneStorage.S8 or TuneStorage.S16 or TuneStorage.S32;
        bool floating = Storage == TuneStorage.Float32;
        equation.Evaluate(link is null ? 0 : image.Read(link, 0, 0), width, signed, floating);
        return new(Name, "User Defined", Notes, Units, 0, Address, width, signed,
            LittleEndian, floating, 0, Rows, Columns, Columns * width, width,
            Labels(RowLabels, Rows, "row"), Labels(ColumnLabels, Columns, "column"), "", "", 6,
            double.NegativeInfinity, double.PositiveInfinity, equation, link, true, TuneFormat.Number, null);
    }

    private static bool Overlaps(TuneParameter item, int address, int size)
    {
        for (int row = 0; row < item.Rows; row++)
        for (int column = 0; column < item.Columns; column++)
        {
            int cell = item.AddressOf(row, column);
            if (cell < address + size && address < cell + item.Width)
                return true;
        }
        return false;
    }

    private static string[] Labels(string value, int length, string axis)
    {
        if (value == "")
            return Enumerable.Range(0, length).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();
        string[] labels = value?.Split(',', StringSplitOptions.TrimEntries) ?? [];
        if (labels.Length != length || labels.Any(label => label.Length is < 1 or > 50))
            throw new FormatException($"Enter {length} comma-separated {axis} labels or leave them blank.");
        return labels;
    }

    private static string FileFor(uint os) => Path.Combine(Path.GetDirectoryName(ScannerLayout.DefaultPath)!,
        $"tuner-parameters-{os}.json");

    public static UserTuneParameter[] Load(uint os)
    {
        string path = FileFor(os);
        if (!File.Exists(path))
            return [];
        using var stream = File.OpenRead(path);
        if (stream.Length > 1024 * 1024)
            throw new InvalidDataException("The user-defined parameter file exceeds 1 MiB.");
        var items = JsonSerializer.Deserialize<UserTuneParameter[]>(stream);
        if (items is null || items.Length > Maximum || items.Any(item => item is null))
            throw new InvalidDataException("The user-defined parameter file is invalid.");
        return items;
    }

    public static void Save(uint os, UserTuneParameter[] items)
    {
        string path = FileFor(os);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew))
                JsonSerializer.Serialize(stream, items, new JsonSerializerOptions { WriteIndented = true });
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static void SelfTest(string existingBin)
    {
        var image = new TuneImage(existingBin);
        int writable = Enumerable.Range(0x8000, 0x18000).First(address => image.CanWrite(address, 4));
        var definition = new UserTuneParameter("Test map", "units", writable, TuneStorage.S16,
            true, 1, 2, "", "1000,2000", "X * 0.5 + 1", "");
        var parameter = definition.Build(image, []);
        double original = image.Read(parameter, 0, 1);
        image.Apply(parameter, [(0, 1, -25.5)], [parameter]);
        if (image.Read(parameter, 0, 1) != -25.5 || parameter.AddressOf(0, 1) != writable + 2)
            throw new InvalidDataException("User-defined parameter conversion or table indexing failed.");
        image.Undo();
        if (image.Read(parameter, 0, 1) != original)
            throw new InvalidDataException("Undo did not restore the user-defined parameter.");
        image.Redo();
        string path = Path.Combine(Path.GetTempPath(), $"wrongway-udp-{Guid.NewGuid():N}.bin");
        try
        {
            image.SaveCopy(path);
            if (new TuneImage(path).Read(parameter, 0, 1) != -25.5)
                throw new InvalidDataException("Reloaded BIN did not preserve the edited value.");
        }
        finally { File.Delete(path); }

        var nonlinear = new UserTuneParameter("Nonlinear test", "units", writable, TuneStorage.U16,
            true, 1, 1, "", "", "X^2 * 0.01", "");
        var nlParam = nonlinear.Build(image, []);
        image.Apply(nlParam, [(0, 0, 25)], [nlParam]);
        if (Math.Abs(image.Read(nlParam, 0, 0) - 25) > 0.5)
            throw new InvalidDataException("Nonlinear user-defined parameter round-trip failed.");

        try
        {
            (nonlinear with { Storage = TuneStorage.S16 }).Build(image, []);
            throw new InvalidOperationException("A conversion with two possible raw values was accepted.");
        }
        catch (FormatException) { }
        try
        {
            (definition with { Address = 0x500 }).Build(image, []);
            throw new InvalidOperationException("A protected BIN address accepted a user definition.");
        }
        catch (FormatException) { }
    }

    public override string ToString() => $"{Name}   ·   0x{Address:X}   ·   {Rows} × {Columns}";
}
