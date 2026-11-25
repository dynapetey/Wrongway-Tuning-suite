namespace WrongWay;

readonly record struct ImperialConversion(string Unit, double Scale = 1, double Offset = 0)
{
    public double Convert(double value) => value * Scale + Offset;
    public double Restore(double value) => (value - Offset) / Scale;
}

// Display only: recorded samples and formula inputs retain their original units.
static class ImperialUnits
{
    public static ImperialConversion For(string unit) => unit.Trim().ToLowerInvariant() switch
    {
        "c" or "°c" or "degc" or "celsius" => new("°F", 1.8, 32),
        "f" or "°f" or "degf" or "fahrenheit" => new("°F"),
        "k" or "kelvin" => new("°F", 1.8, -459.67),
        "kpa" => new("psi", 1 / 6.894757293168),
        "pa" => new("psi", 1 / 6894.757293168),
        "mpa" => new("psi", 1000 / 6.894757293168),
        "bar" => new("psi", 100 / 6.894757293168),
        "psi" => new("psi"),
        "mmhg" => new("inHg", 1 / 25.4),
        "kph" or "km/h" or "km/hr" or "kmh" => new("mph", 1 / 1.609344),
        "km" => new("mi", 1 / 1.609344),
        "m/s" => new("mph", 3600 / 1609.344),
        "mm" => new("in", 1 / 25.4),
        "cm" => new("in", 1 / 2.54),
        "m" => new("ft", 1 / 0.3048),
        "sqmm" or "mm²" or "mm2" => new("in²", 1 / (25.4 * 25.4)),
        "l" or "liter" or "liters" or "litre" or "litres" => new("in³", 1000 / 16.387064),
        "cc" or "cm³" or "cm3" => new("in³", 1 / 16.387064),
        "kg" => new("lb", 1 / 0.45359237),
        "g" => new("lb", 1 / 453.59237),
        "g/s" or "g/sec" => new("lb/min", 60 / 453.59237),
        "kg/h" or "kg/hr" => new("lb/hr", 1 / 0.45359237),
        "g/cyl" => new("lb/cyl", 1 / 453.59237),
        "l/h" or "l/hr" => new("US gal/hr", 1 / 3.785411784),
        "cc/min" => new("US gal/min", 1 / 3785.411784),
        "n-m" or "n·m" or "nm" or "n*m" => new("lb-ft", 1 / 1.3558179483314),
        "kw" => new("hp", 1000 / 745.69987158227),
        _ => new(unit)
    };

    public static string SourceUnit(ChannelSource source) => source.Unit.Length > 0 ? source.Unit
        : source == ChannelSource.Map ? "kPa" : "";

    public static double Range(double value, ChannelSource source, string rangeUnit) =>
        For(rangeUnit.Length > 0 ? rangeUnit : SourceUnit(source)).Convert(value);

    public static void SelfTest()
    {
        void Check(string unit, double input, string target, double expected)
        {
            var conversion = For(unit);
            if (conversion.Unit != target || Math.Abs(conversion.Convert(input) - expected) > 0.000001
                || Math.Abs(conversion.Restore(expected) - input) > 0.000001)
                throw new InvalidDataException($"Imperial conversion failed: {unit} to {target}.");
        }
        Check("°C", 100, "°F", 212);
        Check("kPa", 100, "psi", 14.503773773);
        Check("kph", 100, "mph", 62.137119224);
        Check("g/s", 100, "lb/min", 13.227735731);
        Check("N-m", 100, "lb-ft", 73.756214928);
        Check("Liters", 5.3, "in³", 323.425843702);
        Check("mV", 450, "mV", 450);
        Check("%", -12.5, "%", -12.5);
        Check("Volts", 2.5, "Volts", 2.5);
        var channel = new Channel(0, 5, "Coolant", "°C");
        var raw = new Reading(0, 80);
        channel.Readings.Add(raw);
        if (channel.DisplayUnit != "°F" || channel.DisplayNumber(raw) != 176 || raw.Number != 80)
            throw new InvalidDataException("Imperial display changed the source sample.");
        channel.Display = new(new("Coolant", "°C"), "Coolant", "°F", 1.8, 32, 1, false);
        if (channel.DisplayNumber(raw) != 176)
            throw new InvalidDataException("Existing Fahrenheit display was converted twice.");
        var axis = new GraphAxis(new("Coolant", "°C"), 10, []);
        if (axis.Bin(177) != 176)
            throw new InvalidDataException("Metric histogram bins were not converted with their temperature offset.");
        if (Math.Abs(Range(120, ChannelSource.Map, "") - 17.4045285276) > 0.000001
            || Range(20, ChannelSource.Map, "psi") != 20)
            throw new InvalidDataException("Existing metric and new imperial gauge ranges disagree.");
        var log = new ScanLog("imperial export");
        log.Channels.Add(channel);
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
        try
        {
            CsvLog.Export(log, path);
            var loaded = CsvLog.Load(path).Channels.Single();
            if (loaded.Unit != "°F" || loaded.Readings.Single().Number != 176)
                throw new InvalidDataException("Imperial CSV values and units do not match.");
        }
        finally { File.Delete(path); }
        Console.WriteLine("Imperial units, existing display rules, histogram bins and CSV export checks passed.");
    }
}

sealed record RecordingUnit(string Source)
{
    public override string ToString() => ImperialUnits.For(Source).Unit;
}
