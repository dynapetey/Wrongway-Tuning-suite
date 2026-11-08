using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace WrongWay;

enum TuneFormat { Number, Hex, Text }

sealed record TuneParameter(
    string Title, string Category, string Notes, string Units, uint Id, int BaseAddress,
    int Width, bool Signed, bool LittleEndian, bool Floating, uint Mask,
    int Rows, int Columns, int RowStep, int ColumnStep, string[] RowLabels, string[] ColumnLabels,
    string RowUnits, string ColumnUnits, int Decimals, double Minimum, double Maximum,
    TuneEquation Equation, TuneParameter? Link, bool Editable, TuneFormat Display,
    IReadOnlyDictionary<(int Row, int Column), int>? AddressOverrides)
{
    public bool IsText => Display == TuneFormat.Text;

    public int AddressOf(int row, int column)
    {
        if ((uint)row >= Rows || (uint)column >= Columns)
            throw new ArgumentOutOfRangeException(nameof(row), "The cell is outside the table.");
        if (AddressOverrides?.TryGetValue((row, column), out int address) == true)
            return address;
        return checked(BaseAddress + row * RowStep + column * ColumnStep);
    }

    public TuneConversion Conversion(TuneImage image) =>
        Equation.Evaluate(Link is null ? 0 : image.Read(Link, 0, 0));

    public string Format(double value, bool forEdit = false)
    {
        if (Mask != 0)
            return value == 0 ? "Off" : "On";
        if (IsText)
            return (value is >= 0x20 and <= 0x7E ? (char)value : '·').ToString();
        if (Display == TuneFormat.Hex && value == Math.Truncate(value) && Math.Abs(value) <= uint.MaxValue)
        {
            string digits = ((uint)Math.Abs(value)).ToString($"X{Width * 2}", CultureInfo.InvariantCulture);
            return value < 0 ? $"-0x{digits}" : $"0x{digits}";
        }
        return value.ToString(forEdit ? "G" : $"0.{new string('#', Decimals)}", CultureInfo.InvariantCulture);
    }

    public double ParseEdit(string text, bool setValue)
    {
        text = text.Trim();
        if (setValue && Mask != 0)
            return text.ToUpperInvariant() switch
            {
                "ON" or "1" => 1,
                "OFF" or "0" => 0,
                _ => throw new FormatException("Enter On or Off.")
            };
        if (setValue && Display == TuneFormat.Hex)
        {
            bool negative = text.StartsWith('-');
            ReadOnlySpan<char> digits = text.AsSpan(negative ? 1 : 0);
            if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(digits[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint hex))
                return negative ? -(double)hex : hex;
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value))
            return value;
        throw new FormatException(setValue && Display == TuneFormat.Hex
            ? "Enter a hex value such as 0x2A or a decimal number."
            : "Enter a finite number. Use a decimal point for fractions.");
    }

    public override string ToString() => Title;
}

sealed record TuneDefinition(string Title, IReadOnlyList<TuneParameter> Parameters, IReadOnlyList<string> Skipped)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static TuneDefinition Load(string path, TuneImage image)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024
        });
        XElement root = XElement.Load(reader);
        if (root.Name != "XDFFORMAT" || root.Element("XDFHEADER") is not XElement header)
            throw new InvalidDataException("Choose a TunerPro XML XDF with a definition header.");
        string title = Value(header, "deftitle");
        string[] ids = Regex.Matches(title, @"\b\d{8}\b").Select(match => match.Value).Distinct().ToArray();
        if (ids.Length == 0)
            ids = Regex.Matches(Value(header, "description"), @"(?i)\b(?:operating system|OS)\s+(\d{8})\b")
                .Select(match => match.Groups[1].Value).Distinct().ToArray();
        if (ids.Length != 1 || ids[0] != image.Os.ToString(Invariant))
            throw new InvalidDataException($"The XDF must identify the BIN’s operating system {image.Os} in its header.");

        XElement? baseElement = header.Element("BASEOFFSET");
        int baseOffset = Number(baseElement, "offset");
        if (Number(baseElement, "subtract") != 0)
            baseOffset = checked(-baseOffset);
        XElement? defaults = header.Element("DEFAULTS");
        int defaultWidth = Number(defaults, "datasizeinbits", 8);
        int defaultFlags = (Number(defaults, "signed") != 0 ? 1 : 0) |
                           (Number(defaults, "lsbfirst") != 0 ? 2 : 0) |
                           (Number(defaults, "float") != 0 ? 0x10000 : 0);
        var categories = header.Elements("CATEGORY").ToDictionary(
            category => Number(category, "index") + 1, category => (string?)category.Attribute("name") ?? "Other");

        var skipped = new List<string>();
        var parameters = new List<TuneParameter>();
        var scalars = new Dictionary<uint, TuneParameter>();
        var duplicateIds = new HashSet<uint>();
        // Read native scalar definitions before resolving table equations that reference them.
        foreach (XElement item in root.Elements().Where(item => item.Name != "XDFHEADER")
                     .OrderBy(item => item.Name == "XDFCONSTANT" ? 0 : 1))
        {
            string name = Value(item, "title", item.Name.LocalName).Trim();
            try
            {
                TuneParameter parameter = Parse(item, name);
                parameters.Add(parameter);
                if (item.Name == "XDFCONSTANT" && parameter.Id != 0 && parameter.Link is null &&
                    !scalars.TryAdd(parameter.Id, parameter))
                    duplicateIds.Add(parameter.Id);
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or OverflowException)
            {
                skipped.Add($"{name} · {ex.Message}");
            }
        }
        if (parameters.Count == 0)
            throw new InvalidDataException("The XDF contains no supported parameters for this BIN.");
        return new TuneDefinition(title, parameters.OrderBy(parameter => parameter.Category)
            .ThenBy(parameter => parameter.Title).ToArray(), skipped);

        TuneParameter Parse(XElement item, string name)
        {
            bool isTable = item.Name == "XDFTABLE";
            bool isFlag = item.Name == "XDFFLAG";
            if (!isTable && !isFlag && item.Name != "XDFCONSTANT")
                throw new FormatException("Unsupported definition type.");
            XElement? Axis(string id) => item.Elements("XDFAXIS").FirstOrDefault(axis => (string?)axis.Attribute("id") == id);
            XElement? x = isTable ? Axis("x") : null;
            XElement? y = isTable ? Axis("y") : null;
            XElement? owner = isTable ? Axis("z") : item;
            XElement? storage = owner?.Element("EMBEDDEDDATA");
            if (storage?.Attribute("mmedaddress") is null)
                throw new FormatException("No mapped value address.");
            int address = checked(Number(storage, "mmedaddress") + baseOffset);
            int flags = Number(storage, "mmedtypeflags", defaultFlags);
            int bits = Number(storage, "mmedelementsizebits", defaultWidth);
            bool floating = (flags & 0x10000) != 0;
            if ((flags & ~0x10007) != 0 || bits is not (8 or 16 or 32) || floating && (bits != 32 || isFlag))
                throw new FormatException("Unsupported binary storage format.");
            int width = bits / 8;
            int rows = isTable ? Number(storage, "mmedrowcount", 1) : 1;
            int columns = isTable ? Number(storage, "mmedcolcount", 1) : 1;
            if (rows < 1 || columns < 1 || (long)rows * columns > 20000)
                throw new FormatException("Unsupported table dimensions.");
            int majorBits = Number(storage, "mmedmajorstridebits", bits);
            int minorBits = Number(storage, "mmedminorstridebits", bits);
            if (majorBits == 0) majorBits = bits;
            if (minorBits == 0) minorBits = bits;
            if (majorBits < bits || minorBits < bits || majorBits % 8 != 0 || minorBits % 8 != 0)
                throw new FormatException("Unsupported or overlapping table stride.");

            bool columnMajor = (flags & 4) != 0;
            int withinLine = majorBits / 8;
            int lineStep = checked(((columnMajor ? rows : columns) - 1) * withinLine + minorBits / 8);
            int rowStep = columnMajor ? withinLine : lineStep;
            int columnStep = columnMajor ? lineStep : withinLine;
            int last = checked(address + (rows - 1) * rowStep + (columns - 1) * columnStep);
            if (!image.Contains(address, width) || !image.Contains(last, width))
                throw new FormatException("Address is outside the BIN.");

            uint mask = isFlag ? checked((uint)Integer(Value(item, "mask", "0"))) : 0;
            if (isFlag && (mask == 0 || (mask & (mask - 1)) != 0 || (bits < 32 && (mask >> bits) != 0)))
                throw new FormatException("Unsupported switch mask.");
            XElement[] maths = owner!.Elements("MATH")
                .Where(math => math.Attribute("row") is null && math.Attribute("col") is null).ToArray();
            if (maths.Length > 1)
                throw new FormatException("Multiple default equations are unsupported.");
            string equationText = ((string?)maths.FirstOrDefault()?.Attribute("equation") ?? "X").Trim();

            TuneParameter? link = null;
            string? linkName = null;
            foreach (XElement variable in maths.SelectMany(math => math.Elements("VAR")))
            {
                string nameInMath = ((string?)variable.Attribute("id") ?? "").ToUpperInvariant();
                string type = (string?)variable.Attribute("type") ?? "0";
                if (nameInMath == "X" && type is "0" or "native")
                    continue;
                uint linkId = Identifier(variable, "linkid");
                if (type != "link" || link is not null || duplicateIds.Contains(linkId) ||
                    !scalars.TryGetValue(linkId, out link) || nameInMath.Length != 1 || nameInMath == "X")
                    throw new FormatException("Unsupported equation reference.");
                linkName = nameInMath;
            }
            var equation = new TuneEquation(equationText, linkName);
            var conversion = equation.Evaluate(link is null ? 0 : image.Read(link, 0, 0));

            Dictionary<(int Row, int Column), int>? overrides = null;
            foreach (XElement math in owner.Elements("MATH").Except(maths))
            {
                var variables = math.Elements("VAR").ToArray();
                int row = Number(math, "row", rows == 1 ? 1 : 0) - 1;
                int column = Number(math, "col", columns == 1 ? 1 : 0) - 1;
                if (!isTable || baseOffset != 0 || floating || (flags & 3) != 0 || link is not null ||
                    (uint)row >= rows || (uint)column >= columns ||
                    !string.Equals(((string?)math.Attribute("equation"))?.Trim(), equationText, StringComparison.OrdinalIgnoreCase) ||
                    variables is not [var variable] ||
                    (string?)variable.Attribute("id") != "X" || (string?)variable.Attribute("type") != "address" ||
                    variable.Attributes().Any(attribute => attribute.Name.LocalName is not ("id" or "type" or "address" or "sizeinbits")) ||
                    variable.Attribute("address") is null || Number(variable, "sizeinbits") != bits)
                    throw new FormatException("Unsupported per-cell address or equation.");
                int mapped = Number(variable, "address");
                overrides ??= [];
                if (!image.Contains(mapped, width) || !overrides.TryAdd((row, column), mapped))
                    throw new FormatException("Invalid or duplicate per-cell address.");
            }
            int first = address;
            if (overrides is not null)
            {
                int[] addresses = Enumerable.Range(0, rows * columns).Select(index =>
                {
                    int row = index / columns, column = index % columns;
                    return overrides.GetValueOrDefault((row, column), address + row * rowStep + column * columnStep);
                }).Order().ToArray();
                if (addresses.Zip(addresses.Skip(1)).Any(pair => pair.Second - pair.First < width))
                    throw new FormatException("Overlapping per-cell addresses.");
                first = addresses[0];
                last = addresses[^1];
            }

            string notes = Value(item, "description");
            double minimum = Limit(owner, isTable ? "min" : "rangelow", double.NegativeInfinity);
            double maximum = Limit(owner, isTable ? "max" : "rangehigh", double.PositiveInfinity);
            if (minimum >= maximum)
                (minimum, maximum) = (double.NegativeInfinity, double.PositiveInfinity);
            string category = string.Join(" / ", item.Elements("CATEGORYMEM")
                .Select(membership => categories.GetValueOrDefault(Number(membership, "category"), "Other")).Distinct());
            string units = Value(owner, "units");
            bool isVin = isTable && width == 1 && rows == 1 && columns == 17 &&
                (units.Trim().Equals("VIN", StringComparison.OrdinalIgnoreCase) ||
                 Regex.IsMatch(name, @"\AVIN(?: [12])?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            TuneFormat display = isVin || Value(owner, "outputtype") == "4" ? TuneFormat.Text
                : Value(owner, "outputtype") == "3" ? TuneFormat.Hex : TuneFormat.Number;
            if (display == TuneFormat.Text && (width != 1 || rows != 1 || link is not null ||
                conversion != new TuneConversion(1, 0)))
                throw new FormatException("Unsupported text field layout or equation.");
            // Requiring the whole span to fit one segment also protects each segment's header.
            bool editable = !isVin && image.CanWrite(first, checked(last - first + width)) &&
                !Regex.IsMatch(notes, @"(?i)\b(?:do not (?:hand )?edit|information only|read.only)\b");
            int decimals = int.TryParse(Value(owner, "decimalpl"), out int places) ? Math.Clamp(places, 0, 15) : 4;

            return new TuneParameter(name, category.Length == 0 ? "Other" : category, notes, units,
                Identifier(item, "uniqueid"), address, width, (flags & 1) != 0, (flags & 2) != 0, floating, mask,
                rows, columns, rowStep, columnStep, Labels(y, rows), Labels(x, columns),
                Value(y, "units"), Value(x, "units"), decimals, minimum, maximum, equation, link, editable, display, overrides);
        }
    }

    private static string[] Labels(XElement? axis, int count)
    {
        if (axis?.Element("EMBEDDEDDATA")?.Attribute("mmedaddress") is not null ||
            axis?.Element("embedinfo") is not null)
            throw new FormatException("Binary-backed axis breakpoints are unsupported.");
        string[] labels = Enumerable.Range(1, count).Select(index => $"#{index}").ToArray();
        foreach (XElement label in axis?.Elements("LABEL") ?? [])
        {
            int index = Number(label, "index", -1);
            if ((uint)index < count && (string?)label.Attribute("value") is string value)
                labels[index] = value;
        }
        return labels;
    }

    private static int Number(XElement? element, string attribute, int fallback = 0) =>
        (string?)element?.Attribute(attribute) is string value ? checked((int)Integer(value)) : fallback;

    private static uint Identifier(XElement element, string attribute) =>
        (string?)element.Attribute(attribute) is string value ? checked((uint)Integer(value)) : 0;

    private static long Integer(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? long.Parse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, Invariant)
        : long.Parse(value, NumberStyles.Integer, Invariant);

    private static string Value(XElement? element, string child, string fallback = "") =>
        element?.Element(child)?.Value ?? fallback;

    private static double Limit(XElement element, string child, double fallback) =>
        double.TryParse(Value(element, child), NumberStyles.Float, Invariant, out double value) &&
        double.IsFinite(value) ? value : fallback;
}
