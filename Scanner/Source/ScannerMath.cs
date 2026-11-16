using System.Globalization;
using System.Linq.Expressions;
using System.Text.RegularExpressions;

namespace WrongWay;

sealed record MathSetting(string Name, string Unit, string Formula, bool Enabled = true, Guid? Id = null)
{
    public const int Maximum = 32;
    public static MathSetting[] Defaults => [new("Wideband AFR", "AFR", "(([7101.10] / 0.5) + 9.57)",
        Id: new Guid("39872d15-8366-4dea-a179-2af70c8c8abe"))];
    public override string ToString() => $"{Name} {(Unit.Length == 0 ? "" : $"[{Unit}] ")}· {Formula}" +
        (Enabled ? "" : " · Disabled");

    public static bool Validate(MathSetting[] items)
    {
        if (items.Length > Maximum || items.Any(item => item is null ||
            item.Name is not { Length: > 0 and <= 120 } || item.Unit is not { Length: <= 80 }) ||
            items.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length ||
            items.Where(item => item.Id.HasValue).GroupBy(item => item.Id).Any(group => group.Count() != 1))
            return false;
        try
        {
            foreach (var item in items)
                _ = new MathFormula(item.Formula);
            return true;
        }
        catch (FormatException) { return false; }
    }
}

sealed class MathFormula
{
    private static readonly Regex Tokens = new(
        @"\G\s*(\[[^\[\]\r\n]+\]|(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?|[a-zA-Z][a-zA-Z0-9]*|<=|>=|==|!=|[-+*/%^(),<>])");
    private readonly List<string> inputs = [];
    private readonly ParameterExpression values = Expression.Parameter(typeof(double[]));
    private readonly string[] tokens;
    private int position;

    public IReadOnlyList<string> Inputs => inputs;
    public Func<double[], double> Evaluate { get; }

    public MathFormula(string? text)
    {
        if (text is not { Length: > 0 and <= 512 })
            throw new FormatException("Enter a formula of at most 512 characters.");
        text = text.Trim();
        var matches = Tokens.Matches(text);
        tokens = matches.Select(match => match.Groups[1].Value).ToArray();
        if (matches.Sum(match => match.Length) != text.Length)
            throw new FormatException("Use numbers, [channels], operators, and supported math functions.");
        var body = Parse(0);
        if (position != tokens.Length || inputs.Count == 0)
            throw new FormatException("Enter a complete expression with at least one [channel].");
        Evaluate = Expression.Lambda<Func<double[], double>>(body, values).Compile();
    }

    private string Peek => position == tokens.Length ? "" : tokens[position];

    private Expression Parse(int precedence)
    {
        Expression left = Term();
        while (true)
        {
            string op = Peek;
            int order = op switch
            {
                "<" or ">" or "<=" or ">=" or "==" or "!=" => 1,
                "+" or "-" => 2,
                "*" or "/" or "%" => 3,
                "^" => 5,
                _ => 0
            };
            if (order < precedence || order == 0)
                return left;
            position++;
            var right = Parse(op == "^" ? order : order + 1);
            left = op switch
            {
                "+" => Expression.Add(left, right),
                "-" => Expression.Subtract(left, right),
                "*" => Expression.Multiply(left, right),
                "/" => Expression.Divide(left, right),
                "%" => Expression.Modulo(left, right),
                "^" => Expression.Power(left, right),
                "<" => Numeric(Expression.LessThan(left, right)),
                ">" => Numeric(Expression.GreaterThan(left, right)),
                "<=" => Numeric(Expression.LessThanOrEqual(left, right)),
                ">=" => Numeric(Expression.GreaterThanOrEqual(left, right)),
                "==" => Numeric(Expression.Equal(left, right)),
                _ => Numeric(Expression.NotEqual(left, right))
            };
        }
    }

    private Expression Term()
    {
        string token = Peek;
        if (token.Length == 0)
            throw new FormatException("The formula ends before a value.");
        position++;
        if (token == "-")
            return Expression.Negate(Parse(4));
        if (token == "+")
            return Parse(4);
        if (token == "(")
        {
            var inside = Parse(0);
            Expect(")");
            return inside;
        }
        if (token[0] == '[')
        {
            string input = token[1..^1].Trim();
            if (input.Length == 0)
                throw new FormatException("Enter a channel name or PID inside the brackets.");
            int index = inputs.FindIndex(value => value.Equals(input, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                index = inputs.Count;
                inputs.Add(input);
            }
            return Expression.ArrayIndex(values, Expression.Constant(index));
        }
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number))
            return Expression.Constant(number);

        (string method, int arity) = token.ToLowerInvariant() switch
        {
            "abs" => (nameof(Math.Abs), 1),
            "sqrt" => (nameof(Math.Sqrt), 1),
            "log" => (nameof(Math.Log), 1),
            "log10" => (nameof(Math.Log10), 1),
            "exp" => (nameof(Math.Exp), 1),
            "round" => (nameof(Math.Round), 1),
            "min" => (nameof(Math.Min), 2),
            "max" => (nameof(Math.Max), 2),
            "pow" => (nameof(Math.Pow), 2),
            "if" => ("if", 3),
            _ => throw new FormatException($"Unrecognized formula value '{token}'.")
        };
        Expect("(");
        var arguments = new Expression[arity];
        for (int i = 0; i < arity; i++)
        {
            if (i != 0)
                Expect(",");
            arguments[i] = Parse(0);
        }
        Expect(")");
        return method == "if"
            ? Expression.Condition(Expression.NotEqual(arguments[0], Expression.Constant(0d)), arguments[1], arguments[2])
            : Expression.Call(typeof(Math), method, Type.EmptyTypes, arguments);
    }

    private void Expect(string token)
    {
        if (Peek != token)
            throw new FormatException($"Expected '{token}' in the formula.");
        position++;
    }

    private static Expression Numeric(Expression value) =>
        Expression.Condition(value, Expression.Constant(1d), Expression.Constant(0d));
}

sealed class MathChannel(Channel output, Channel[] inputs, Func<double[], double> evaluate)
{
    private const int MaximumAge = 500;
    private readonly double[] values = new double[inputs.Length];

    public static MathChannel[] Build(ScanLog log, MathSetting[] settings)
    {
        log.Channels.RemoveAll(channel => channel.IsMath);
        var results = new List<MathChannel>();
        int key = log.Channels.Select(channel => channel.Key).DefaultIfEmpty(-1).Max() + 1;
        foreach (var setting in settings.Where(setting => setting.Enabled))
        {
            if (log.Channels.Any(channel => channel.Name.Equals(setting.Name, StringComparison.OrdinalIgnoreCase)
                && channel.Unit.Equals(setting.Unit, StringComparison.OrdinalIgnoreCase)))
                continue;
            var formula = new MathFormula(setting.Formula);
            var sources = formula.Inputs.Select(input => Resolve(log.Channels, input)).ToArray();
            if (sources.Any(source => source is null))
                continue;
            var output = new Channel(key++, null, setting.Name, setting.Unit) { IsMath = true, MathId = setting.Id };
            var inputs = sources.OfType<Channel>().ToArray();
            var result = new MathChannel(output, inputs, formula.Evaluate);
            result.Replay();
            results.Add(result);
        }
        log.Channels.AddRange(results.Select(result => result.Output));
        return results.ToArray();
    }

    public Channel Output => output;

    public static Channel? Resolve(IEnumerable<Channel> channels, string token)
    {
        var parts = token.Split('|', 2, StringSplitOptions.TrimEntries);
        string name = parts[0];
        bool voltage = name.EndsWith(".10", StringComparison.Ordinal);
        if (voltage)
            name = name[..^3];
        bool isHex = name.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        bool byPid = int.TryParse(isHex ? name[2..] : name,
            isHex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out int pid);
        var matches = channels.Where(channel => !channel.IsMath &&
            (byPid ? channel.Pid == pid || pid == 7101 && voltage && channel.Pid is null &&
                channel.Name is "A/C Pressure Sensor Input" or "A/C Pressure Sensor Voltage"
                : channel.Name.Trim().Equals(parts[0], StringComparison.OrdinalIgnoreCase)) &&
            (parts.Length == 1 || channel.Unit.Trim().Equals(parts[1], StringComparison.OrdinalIgnoreCase)) &&
            (!byPid || !voltage || channel.Unit.Equals("V", StringComparison.OrdinalIgnoreCase)
                || channel.Unit.Equals("Volts", StringComparison.OrdinalIgnoreCase))).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static void SelfTest()
    {
        var log = new ScanLog("math test");
        var volts = new Channel(0, 7101, "A/C Pressure Sensor Input", "V");
        var pressure = new Channel(1, 7101, "A/C Pressure Sensor Input", "kPa");
        var rpm = new Channel(2, 12, "Engine Speed", "RPM");
        log.Channels.AddRange([volts, pressure, rpm]);
        volts.Readings.AddRange([new(0, 2.5), new(200, 3)]);
        rpm.Readings.AddRange([new(100, 2500), new(800, 2600)]);
        var settings = new[] { MathSetting.Defaults[0],
            new MathSetting("Test channel", "test", "if([12] > 2000, pow([7101.10], 2), 0)") };
        var calculated = Build(log, settings);
        var afr = calculated[0].Output;
        var afrSource = ChannelSource.From(log, afr);
        var paired = calculated[1].Output;
        if (Math.Abs(afr.Readings[0].Number!.Value - 14.57) > 1e-9 || paired.Readings[0] != new Reading(100, 6.25) ||
            paired.Readings[^1] != new Reading(800, null) || Resolve(log.Channels, "7101") is not null)
            throw new InvalidDataException("Maths failed to resolve voltage, evaluate an expression, or mark stale input.");
        volts.Readings.Add(new(850, 3));
        calculated[1].Append(850);
        if (paired.Readings[^1] != new Reading(850, 9))
            throw new InvalidDataException("Maths did not append a live sample.");
        Build(log, [settings[0] with { Name = "Renamed", Formula = "[7101.10] + 1" }]);
        if (log.Channels.Count != 4 || log.Channels[^1].Name != "Renamed" || afrSource.Resolve(log) != log.Channels[^1])
            throw new InvalidDataException("Editing Maths left an outdated channel in the log.");
    }

    private void Replay()
    {
        int[] indexes = new int[inputs.Length];
        while (true)
        {
            long time = long.MaxValue;
            for (int i = 0; i < inputs.Length; i++)
                if (indexes[i] < inputs[i].Readings.Count)
                    time = Math.Min(time, inputs[i].Readings[indexes[i]].Time);
            if (time == long.MaxValue)
                return;
            for (int i = 0; i < inputs.Length; i++)
            {
                var readings = inputs[i].Readings;
                while (indexes[i] < readings.Count && readings[indexes[i]].Time <= time)
                    indexes[i]++;
            }
            Add(time, i => indexes[i] == 0 ? null : inputs[i].Readings[indexes[i] - 1]);
        }
    }

    public void Append(long time) => Add(time, i => inputs[i].AsOf(time));

    private void Add(long time, Func<int, Reading?> read)
    {
        double? result = null;
        bool complete = true;
        for (int i = 0; i < inputs.Length; i++)
        {
            var reading = read(i);
            if (reading?.Number is not double value || time - reading.Time > MaximumAge)
            {
                complete = false;
                break;
            }
            values[i] = value;
        }
        if (complete)
        {
            double value = evaluate(values);
            result = double.IsFinite(value) ? value : null;
        }
        if (result.HasValue || output.Readings.LastOrDefault()?.Number is not null)
            output.Readings.Add(new(time, result));
    }
}
