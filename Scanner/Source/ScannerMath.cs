// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace WrongWay;

enum ChannelWindowOp
{
    Value,
    Avg,
    Min,
    Max,
    Sum,
    Count,
    Slope,
    Derivative,
    Integral,
    Delta,
    Prev
}

sealed record ChannelInputSpec(string RawToken, string BaseToken, ChannelWindowOp Operation, double WindowMs)
{
    private static readonly Regex MethodSuffix = new(
        @"^(?<base>.+)\.(?<op>avg|mean|min|max|sum|count|slope|rate|derivative|deriv|roc|integral|integ|delta|diff|lag|delay|prev)\(\s*(?<arg>[+-]?(?:\d+(?:\.\d*)?|\.\d+))?\s*\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ChannelInputSpec Parse(string rawToken)
    {
        string trimmed = rawToken.Trim();
        var match = MethodSuffix.Match(trimmed);
        if (!match.Success)
            return new ChannelInputSpec(trimmed, trimmed, ChannelWindowOp.Value, 0);

        string baseToken = match.Groups["base"].Value.Trim();
        string opName = match.Groups["op"].Value.ToLowerInvariant();
        string argText = match.Groups["arg"].Value;
        double windowMs = 0;
        if (!string.IsNullOrEmpty(argText) &&
            (!double.TryParse(argText, NumberStyles.Float, CultureInfo.InvariantCulture, out windowMs)
                || !double.IsFinite(windowMs) || Math.Abs(windowMs) > int.MaxValue))
            throw new FormatException($"Invalid time window '{argText}' in [{trimmed}].");

        ChannelWindowOp op = opName switch
        {
            "avg" or "mean" => ChannelWindowOp.Avg,
            "min" => ChannelWindowOp.Min,
            "max" => ChannelWindowOp.Max,
            "sum" => ChannelWindowOp.Sum,
            "count" => ChannelWindowOp.Count,
            "slope" or "rate" => ChannelWindowOp.Slope,
            "derivative" or "deriv" or "roc" => ChannelWindowOp.Derivative,
            "integral" or "integ" => ChannelWindowOp.Integral,
            "delta" or "diff" => ChannelWindowOp.Delta,
            "lag" or "delay" or "prev" => ChannelWindowOp.Prev,
            _ => ChannelWindowOp.Value
        };
        if (op is ChannelWindowOp.Derivative && string.IsNullOrEmpty(argText))
            windowMs = 250;
        else if (op is ChannelWindowOp.Slope && string.IsNullOrEmpty(argText))
            windowMs = 500;
        else if (op is ChannelWindowOp.Avg or ChannelWindowOp.Min or ChannelWindowOp.Max && string.IsNullOrEmpty(argText))
            windowMs = 500;
        else if (op is ChannelWindowOp.Delta or ChannelWindowOp.Prev && string.IsNullOrEmpty(argText))
            windowMs = 100;

        return new ChannelInputSpec(trimmed, baseToken, op, windowMs);
    }

    public double? Evaluate(Channel channel, long time, int maximumAgeMs = 500)
    {
        var readings = channel.Readings;
        if (readings.Count == 0)
            return null;
        if (WindowMs < 0 && Operation is not (ChannelWindowOp.Prev or ChannelWindowOp.Delta) &&
            readings[^1].Time < time + Math.Abs(WindowMs))
            return null;

        if (Operation == ChannelWindowOp.Value)
        {
            var current = channel.AsOf(time);
            if (current?.Number is not double val || time - current.Time > maximumAgeMs)
                return null;
            return val;
        }

        if (Operation == ChannelWindowOp.Prev)
        {
            long targetTime = WindowMs == 0 ? time - 1 : time - (long)Math.Abs(WindowMs);
            var prev = channel.AsOf(targetTime);
            return prev?.Number;
        }

        if (Operation == ChannelWindowOp.Delta)
        {
            var nowReading = channel.AsOf(time);
            if (nowReading?.Number is not double nowVal || time - nowReading.Time > maximumAgeMs)
                return null;
            long refTime = WindowMs == 0 ? nowReading.Time - 1 : time - (long)Math.Abs(WindowMs);
            var prevReading = channel.AsOf(refTime);
            if (prevReading?.Number is not double prevVal)
                return 0d;
            return nowVal - prevVal;
        }

        if (Operation == ChannelWindowOp.Integral)
        {
            long start = WindowMs == 0 ? readings[0].Time : (WindowMs > 0 ? time - (long)Math.Round(WindowMs) : time);
            long end = WindowMs >= 0 ? time : time + (long)Math.Round(Math.Abs(WindowMs));
            int idxStart = Math.Max(0, channel.UpperBound(start) - 1);
            int idxEnd = Math.Min(readings.Count - 1, channel.UpperBound(end) - 1);
            if (idxEnd < idxStart)
                return 0d;
            double accum = 0d;
            for (int i = idxStart; i < idxEnd; i++)
            {
                if (readings[i].Number is double v0 && readings[i + 1].Number is double v1)
                {
                    long t0 = Math.Max(start, readings[i].Time);
                    long t1 = Math.Min(end, readings[i + 1].Time);
                    if (t1 > t0)
                        accum += 0.5 * (v0 + v1) * ((t1 - t0) / 1000.0);
                }
            }
            return accum;
        }

        // HP Tuners VCM Scanner convention: positive ms = trailing window [time - ms, time]; negative ms = lookahead window [time, time + |ms|]
        long winStart = WindowMs >= 0 ? time - (long)Math.Round(WindowMs) : time;
        long winEnd = WindowMs >= 0 ? time : time + (long)Math.Round(Math.Abs(WindowMs));
        if (winEnd < winStart)
            (winStart, winEnd) = (winEnd, winStart);

        int firstIdx = Math.Max(0, channel.UpperBound(winStart) - 1);
        int lastIdx = Math.Min(readings.Count - 1, channel.UpperBound(winEnd) - 1);

        if (Operation is ChannelWindowOp.Slope or ChannelWindowOp.Derivative)
        {
            Reading? r0 = null;
            Reading? r1 = null;
            for (int i = firstIdx; i <= lastIdx && i < readings.Count; i++)
            {
                var r = readings[i];
                if (r.Time >= winStart && r.Time <= winEnd && r.Number.HasValue)
                {
                    r0 ??= r;
                    r1 = r;
                }
            }
            if ((r0 is null || r1 is null || r0.Time == r1.Time) && lastIdx >= 1)
            {
                int prevIdx = Math.Max(0, lastIdx - 1);
                r0 = readings[prevIdx];
                r1 = readings[lastIdx];
            }
            if (r0?.Number is not double y0 || r1?.Number is not double y1)
                return 0d;
            if (r1.Time == r0.Time)
                return 0d;
            return (y1 - y0) / ((r1.Time - r0.Time) / 1000.0);
        }

        double sum = 0d;
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        int count = 0;
        for (int i = firstIdx; i <= lastIdx && i < readings.Count; i++)
        {
            var r = readings[i];
            if (r.Time < winStart && i < lastIdx)
                continue;
            if (r.Time > winEnd && count > 0)
                break;
            if (r.Number is double v)
            {
                sum += v;
                if (v < min) min = v;
                if (v > max) max = v;
                count++;
            }
        }
        if (count == 0)
        {
            var fallback = channel.AsOf(time);
            if (fallback?.Number is double fv && time - fallback.Time <= Math.Max(maximumAgeMs, (int)Math.Abs(WindowMs)))
            {
                sum = min = max = fv;
                count = 1;
            }
        }
        if (count == 0)
            return Operation == ChannelWindowOp.Count ? 0d : null;

        return Operation switch
        {
            ChannelWindowOp.Avg => sum / count,
            ChannelWindowOp.Min => min,
            ChannelWindowOp.Max => max,
            ChannelWindowOp.Sum => sum,
            ChannelWindowOp.Count => count,
            _ => sum / count
        };
    }
}

sealed record MathSetting(string Name, string Unit, string Formula, bool Enabled = true, Guid? Id = null)
{
    public const int Maximum = 64;
    public static MathSetting[] Defaults =>
    [
        new("Wideband AFR", "AFR", "(([7101.10] / 0.5) + 9.57)",
            Id: new Guid("39872d15-8366-4dea-a179-2af70c8c8abe"))
    ];

    public static MathSetting[] VcmTelemetryBuiltIns =>
    [
        new("Wideband AFR", "AFR", "(([7101.10] / 0.5) + 9.57)",
            Id: new Guid("39872d15-8366-4dea-a179-2af70c8c8abe")),
        new("AFR Error %", "%", "(([50120.150] - [50121.150]) / [50121.150]) * 100",
            Enabled: false, Id: new Guid("4a912d15-8366-4dea-a179-2af70c8c8ac1")),
        new("Combined Fuel Trim", "%", "[50116.156] + [50114.156]",
            Enabled: false, Id: new Guid("4a912d15-8366-4dea-a179-2af70c8c8ac2")),
        new("MAF CL Error %", "%", "(([2320.71]+([2320.71]*([50116.156]+[50114.156])/100))-[50040.71])/[50040.71]*100",
            Enabled: false, Id: new Guid("4a912d15-8366-4dea-a179-2af70c8c8ac3")),
        new("MAF PE Error %", "%", "(([2320.71]+([2320.71]*([50119.238]-[50118.238])/[50118.238]))-[50040.71])/[50040.71]*100",
            Enabled: false, Id: new Guid("4a912d15-8366-4dea-a179-2af70c8c8ac4")),
        new("PE FOR AFR", "%", "(([2320.71]+([2320.71]*(([6001]-[50121])/[50121])))-[16.71])/[16.71]*100",
            Enabled: false, Id: new Guid("4a912d15-8366-4dea-a179-2af70c8c8ac5")),
        new("VVE FOR CL", "%", "(([2320.71]+([2320.71]*([50116.156]+[50114.156])/100))-[2311.71])/[2311.71]*100",
            Enabled: false, Id: new Guid("4a912d15-8366-4dea-a179-2af70c8c8ac6")),
        new("VVE FOR PE", "%", "(([2320.71]+([2320.71]*([50119.238]-[50118.238])/[50118.238]))-[2311.71])/[2311.71]*100",
            Enabled: false, Id: new Guid("4a912d15-8366-4dea-a179-2af70c8c8ac7")),
        new("VVE FOR AFR", "%", "(([2320.71]+([2320.71]*(([50120]-[50121])/[50121])))-[2311.71])/[2311.71]*100",
            Enabled: false, Id: new Guid("4a912d15-8366-4dea-a179-2af70c8c8ac8")),
        new("CL Steady Filter", "flag", "[2517.161.avg(1500)]=0 and [2517.161.avg(-200)]=0 and (abs([50090.156.slope(1500)])+abs([50090.156.slope(-500)]))<2 and [50118.238] = 1.00",
            Enabled: false, Id: new Guid("4a912d15-8366-4dea-a179-2af70c8c8ac9")),
        new("PE Steady Filter", "flag", "[2517.161.avg(1500)]=0 and [2517.161.avg(-200)]=0 and (abs([50090.156.slope(250)])+abs([50090.156.slope(-250)]))<2 and [50118.238.avg(250)] < 1.00",
            Enabled: false, Id: new Guid("4a912d15-8366-4dea-a179-2af70c8c8aca")),
        new("Boost Pressure", "psi", "max(0, ([50030.91] - 101.325) * 0.1450377)",
            Enabled: false, Id: new Guid("4a912d15-8366-4dea-a179-2af70c8c8acb")),
        new("Estimated Wheel HP", "HP", "[50040.73] * 1.08",
            Enabled: false, Id: new Guid("4a912d15-8366-4dea-a179-2af70c8c8acc"))
    ];

    public override string ToString() => $"{Name} {(Unit.Length == 0 ? "" : $"[{Unit}] ")}· {Formula}" +
        (Enabled ? "" : " · Disabled");

    public static bool Validate(MathSetting[] items)
    {
        if (items.Length > Maximum || items.Any(item => item is null ||
            item.Name is not { Length: > 0 and <= 120 } || item.Unit is not { Length: <= 80 }) ||
            items.Select(item => item.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length ||
            items.Where(item => item.Id.HasValue).GroupBy(item => item.Id).Any(group => group.Count() != 1))
            return false;
        try
        {
            var parsed = new List<(MathSetting Setting, MathFormula Formula)>(items.Length);
            foreach (var item in items)
                parsed.Add((item, new MathFormula(item.Formula)));
            return !HasCycle(parsed);
        }
        catch (FormatException) { return false; }
    }

    internal static bool HasCycle(IReadOnlyList<(MathSetting Setting, MathFormula Formula)> items)
    {
        var byName = items.Select((item, index) => (item.Setting, index))
            .ToDictionary(pair => pair.Setting.Name.Trim(), pair => pair.index, StringComparer.OrdinalIgnoreCase);
        var state = new int[items.Count]; // 0 = unvisited, 1 = visiting, 2 = visited
        for (int i = 0; i < items.Count; i++)
            if (state[i] == 0 && Visit(i))
                return true;
        return false;

        bool Visit(int index)
        {
            state[index] = 1;
            foreach (var input in items[index].Formula.InputSpecs)
            {
                if (TryMatchSetting(input.BaseToken, items, byName, out int dep) &&
                    (state[dep] == 1 || (state[dep] == 0 && Visit(dep))))
                    return true;
            }
            state[index] = 2;
            return false;
        }
    }

    internal static bool TryMatchSetting(string token,
        IReadOnlyList<(MathSetting Setting, MathFormula Formula)> items,
        IReadOnlyDictionary<string, int> byName, out int index)
    {
        string baseToken = ChannelInputSpec.Parse(token).BaseToken;
        var parts = baseToken.Split('|', 2, StringSplitOptions.TrimEntries);
        if (byName.TryGetValue(parts[0], out index) &&
            (parts.Length == 1 || items[index].Setting.Unit.Trim().Equals(parts[1], StringComparison.OrdinalIgnoreCase)))
            return true;
        if (VcmConfigImporter.TryResolveMathName(parts[0], out string? aliasName) &&
            byName.TryGetValue(aliasName, out index))
            return true;
        index = -1;
        return false;
    }

    public static MathSetting[] PresetsFromAssets(uint os = LiveScanner.DefaultOs)
    {
        var results = new List<MathSetting>(VcmTelemetryBuiltIns);
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Parameters.Math.xml");
        if (!File.Exists(path))
            return results.ToArray();
        var catalog = LiveScanner.Catalog(os).ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var root = XElement.Load(reader);
        foreach (var element in root.Elements("MathParameter"))
        {
            string? name = (string?)element.Attribute("name") ?? (string?)element.Attribute("Name");
            var conversion = element.Element("Conversion");
            string? expression = (string?)conversion?.Attribute("expression") ?? (string?)element.Attribute("Expression");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(expression))
                continue;
            string units = ((string?)conversion?.Attribute("units") ?? (string?)element.Attribute("Unit"))?.Trim() ?? "";
            if (int.TryParse(units, NumberStyles.Integer, CultureInfo.InvariantCulture, out int unitId))
                units = VcmConfigImporter.UnitSymbol(unitId);
            string xId = ((string?)element.Attribute("xParameterId"))?.Trim() ?? "";
            string xUnit = ((string?)element.Attribute("xParameterConversion"))?.Trim() ?? "";
            string yId = ((string?)element.Attribute("yParameterId"))?.Trim() ?? "";
            string yUnit = ((string?)element.Attribute("yParameterConversion"))?.Trim() ?? "";

            string TokenFor(string id, string unit)
            {
                string label = catalog.TryGetValue(id, out var choice) ? choice.Name : id;
                return string.IsNullOrEmpty(unit) ? $"[{label}]" : $"[{label}|{unit}]";
            }

            string formula = !string.IsNullOrEmpty(xId)
                ? Regex.Replace(expression.Trim(), @"\b([xy])\b", match =>
                    match.Value == "x" ? TokenFor(xId, xUnit) : TokenFor(yId, yUnit))
                : expression.Trim();
            try
            {
                _ = new MathFormula(formula);
                if (!results.Any(r => r.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
                    results.Add(new MathSetting(name.Trim(), units, formula, true, Guid.NewGuid()));
            }
            catch (FormatException) { }
        }
        return results.ToArray();
    }

    public static MathSetting[] LoadVcmXml(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 256 * 1024)
            throw new InvalidDataException("The VCM MathParameter XML exceeds 256 KB.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var root = XElement.Load(reader);
        var elements = root.Name.LocalName.Equals("MathParameter", StringComparison.OrdinalIgnoreCase)
            ? [root]
            : root.Descendants().Where(e => e.Name.LocalName.Equals("MathParameter", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (elements.Length == 0)
            throw new InvalidDataException("No <MathParameter> element found in the XML file.");
        var loaded = new List<MathSetting>();
        foreach (var el in elements)
        {
            string name = ((string?)el.Attribute("Name") ?? (string?)el.Attribute("name") ?? Path.GetFileNameWithoutExtension(path)).Trim();
            string expr = ((string?)el.Attribute("Expression") ?? (string?)el.Element("Conversion")?.Attribute("expression") ?? "").Trim();
            string unitRaw = ((string?)el.Attribute("Unit") ?? (string?)el.Element("Conversion")?.Attribute("units") ?? "").Trim();
            if (int.TryParse(unitRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int unitCode))
                unitRaw = VcmConfigImporter.UnitSymbol(unitCode);
            if (name.Length == 0 || expr.Length == 0)
                throw new InvalidDataException("The VCM MathParameter is missing a Name or Expression attribute.");
            _ = new MathFormula(expr);
            loaded.Add(new MathSetting(name, unitRaw, expr, true, Guid.NewGuid()));
        }
        return loaded.ToArray();
    }
}

sealed class MathFormula
{
    private static readonly Regex Tokens = new(
        @"\G\s*(\[[^\[\]\r\n]+\]|(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?%?|[a-zA-Z_][a-zA-Z0-9_]*|<=|>=|==|!=|<>|&&|\|\||[-+*/%^(),<>!=])");
    private readonly List<string> inputs = [];
    private readonly List<ChannelInputSpec> inputSpecs = [];
    private readonly ParameterExpression values = Expression.Parameter(typeof(double[]));
    private readonly string[] tokens;
    private int position;

    public IReadOnlyList<string> Inputs => inputs;
    public IReadOnlyList<ChannelInputSpec> InputSpecs => inputSpecs;
    public Func<double[], double> Evaluate { get; }

    public MathFormula(string? text)
    {
        if (text is not { Length: > 0 and <= 1024 })
            throw new FormatException("Enter a formula of at most 1024 characters.");
        text = WebUtilityDecode(text.Trim());
        var matches = Tokens.Matches(text);
        tokens = matches.Select(match => match.Groups[1].Value).ToArray();
        if (matches.Sum(match => match.Length) != text.Length)
            throw new FormatException("Use numbers, [channels], operators, and supported VCM math functions.");
        var body = Parse(0);
        if (position != tokens.Length || inputs.Count == 0)
            throw new FormatException("Enter a complete expression with at least one [channel].");
        Evaluate = Expression.Lambda<Func<double[], double>>(body, values).Compile();
    }

    private static string WebUtilityDecode(string text) =>
        text.Replace("&lt;", "<", StringComparison.OrdinalIgnoreCase)
            .Replace("&gt;", ">", StringComparison.OrdinalIgnoreCase)
            .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);

    private string Peek => position == tokens.Length ? "" : tokens[position];

    private static string NormalizeOp(string token) => token.ToLowerInvariant() switch
    {
        "or" or "||" => "||",
        "and" or "&&" => "&&",
        "=" or "==" => "==",
        "<>" or "!=" => "!=",
        _ => token
    };

    private Expression Parse(int precedence)
    {
        Expression left = Term();
        while (true)
        {
            string op = NormalizeOp(Peek);
            int order = op switch
            {
                "||" => 1,
                "&&" => 2,
                "<" or ">" or "<=" or ">=" or "==" or "!=" => 3,
                "+" or "-" => 4,
                "*" or "/" or "%" => 5,
                "^" => 7,
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
                "==" => Numeric(Expression.LessThan(
                    Expression.Call(typeof(Math), nameof(Math.Abs), Type.EmptyTypes, Expression.Subtract(left, right)),
                    Expression.Constant(1e-9))),
                "!=" => Numeric(Expression.GreaterThanOrEqual(
                    Expression.Call(typeof(Math), nameof(Math.Abs), Type.EmptyTypes, Expression.Subtract(left, right)),
                    Expression.Constant(1e-9))),
                "&&" => Numeric(Expression.AndAlso(IsTruthy(left), IsTruthy(right))),
                _ => Numeric(Expression.OrElse(IsTruthy(left), IsTruthy(right)))
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
            return Expression.Negate(Parse(6));
        if (token == "+")
            return Parse(6);
        if (token == "!" || token.Equals("not", StringComparison.OrdinalIgnoreCase))
            return Numeric(Expression.Not(IsTruthy(Parse(6))));
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
            var spec = ChannelInputSpec.Parse(input);
            int index = inputs.FindIndex(value => value.Equals(spec.RawToken, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                index = inputs.Count;
                inputs.Add(spec.RawToken);
                inputSpecs.Add(spec);
            }
            return Expression.ArrayIndex(values, Expression.Constant(index));
        }

        string numToken = token.EndsWith('%') ? token[..^1] : token;
        if (double.TryParse(numToken, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number))
            return Expression.Constant(number);

        string fn = token.ToLowerInvariant();
        if (fn is "pi")
            return Expression.Constant(Math.PI);
        if (fn is "true")
            return Expression.Constant(1d);
        if (fn is "false")
            return Expression.Constant(0d);

        if (fn is "avg" or "mean" or "slope" or "rate" or "derivative" or "deriv" or "integral" or "integ" or "delta" or "lag" or "delay" &&
            Peek == "(" && position + 1 < tokens.Length && tokens[position + 1].StartsWith('['))
        {
            Expect("(");
            string chToken = Peek;
            position++;
            string rawCh = chToken[1..^1].Trim();
            string windowArg = "";
            if (Peek == ",")
            {
                position++;
                if (Peek == "-" || Peek == "+")
                {
                    windowArg = Peek;
                    position++;
                }
                windowArg += Peek;
                position++;
            }
            Expect(")");
            string combined = $"{rawCh}.{fn}({windowArg})";
            var spec = ChannelInputSpec.Parse(combined);
            int index = inputs.FindIndex(value => value.Equals(spec.RawToken, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                index = inputs.Count;
                inputs.Add(spec.RawToken);
                inputSpecs.Add(spec);
            }
            return Expression.ArrayIndex(values, Expression.Constant(index));
        }

        (string method, int arity) = fn switch
        {
            "abs" => (nameof(Math.Abs), 1),
            "sqrt" => (nameof(Math.Sqrt), 1),
            "log" or "ln" => (nameof(Math.Log), 1),
            "log10" => (nameof(Math.Log10), 1),
            "exp" => (nameof(Math.Exp), 1),
            "round" => (nameof(Math.Round), 1),
            "floor" => (nameof(Math.Floor), 1),
            "ceil" or "ceiling" => (nameof(Math.Ceiling), 1),
            "sin" => (nameof(Math.Sin), 1),
            "cos" => (nameof(Math.Cos), 1),
            "tan" => (nameof(Math.Tan), 1),
            "asin" => (nameof(Math.Asin), 1),
            "acos" => (nameof(Math.Acos), 1),
            "atan" => (nameof(Math.Atan), 1),
            "sign" => ("sign", 1),
            "min" => (nameof(Math.Min), 2),
            "max" => (nameof(Math.Max), 2),
            "pow" => (nameof(Math.Pow), 2),
            "iff" or "if" => ("if", 3),
            "clamp" => ("clamp", 3),
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
        return method switch
        {
            "if" => Expression.Condition(IsTruthy(arguments[0]), arguments[1], arguments[2]),
            "clamp" => Expression.Call(typeof(Math), nameof(Math.Clamp), Type.EmptyTypes, arguments[0], arguments[1], arguments[2]),
            "sign" => Expression.Convert(Expression.Call(typeof(Math), nameof(Math.Sign), Type.EmptyTypes, arguments[0]), typeof(double)),
            _ => Expression.Call(typeof(Math), method, Type.EmptyTypes, arguments)
        };
    }

    private void Expect(string token)
    {
        if (Peek != token)
            throw new FormatException($"Expected '{token}' in the formula.");
        position++;
    }

    private static Expression IsTruthy(Expression value) =>
        Expression.NotEqual(value, Expression.Constant(0d));

    private static Expression Numeric(Expression value) =>
        Expression.Condition(value, Expression.Constant(1d), Expression.Constant(0d));
}

sealed class MathChannel(Channel output, (Channel Channel, ChannelInputSpec Spec)[] inputs, Func<double[], double> evaluate, Func<long, bool>? valid = null)
{
    private const int MaximumAge = 500;
    private readonly double[] values = new double[inputs.Length];

    public static MathChannel[] Build(ScanLog log, MathSetting[] settings)
    {
        log.Channels.RemoveAll(channel => channel.IsMath);
        var enabled = new List<(MathSetting Setting, MathFormula Formula)>();
        foreach (var setting in settings.Where(setting => setting.Enabled))
        {
            if (log.Channels.Any(channel => channel.Name.Equals(setting.Name, StringComparison.OrdinalIgnoreCase)
                && channel.Unit.Equals(setting.Unit, StringComparison.OrdinalIgnoreCase)))
                continue;
            enabled.Add((setting, new MathFormula(setting.Formula)));
        }

        var byName = enabled.Select((item, index) => (item.Setting, index))
            .ToDictionary(pair => pair.Setting.Name.Trim(), pair => pair.index, StringComparer.OrdinalIgnoreCase);
        var order = new List<int>(enabled.Count);
        var state = new int[enabled.Count]; // 0 = unvisited, 1 = visiting, 2 = visited, 3 = invalid/cyclic

        void Visit(int index)
        {
            if (state[index] != 0)
                return;
            state[index] = 1;
            foreach (var input in enabled[index].Formula.InputSpecs)
            {
                if (Resolve(log.Channels, input.BaseToken, includeMath: false) is null &&
                    MathSetting.TryMatchSetting(input.BaseToken, enabled, byName, out int dep))
                {
                    if (state[dep] == 0)
                        Visit(dep);
                    if (state[dep] is 1 or 3)
                    {
                        state[index] = 3;
                        return;
                    }
                }
            }
            state[index] = 2;
            order.Add(index);
        }

        for (int i = 0; i < enabled.Count; i++)
            if (state[i] == 0)
                Visit(i);

        var available = new List<Channel>(log.Channels);
        var results = new List<MathChannel>();
        int key = log.Channels.Select(channel => channel.Key).DefaultIfEmpty(-1).Max() + 1;
        foreach (int index in order)
        {
            var (setting, formula) = enabled[index];
            var sources = formula.InputSpecs.Select(spec =>
                (Channel: Resolve(available, spec.BaseToken, includeMath: true), Spec: spec)).ToArray();
            if (sources.Any(source => source.Channel is null))
                continue;
            int? aliasPid = VcmConfigImporter.TryGetVcmMathId(setting.Name);
            var output = new Channel(key++, aliasPid, setting.Name, setting.Unit) { IsMath = true, MathId = setting.Id };
            var resolvedInputs = sources.Select(s => (s.Channel!, s.Spec)).ToArray();
            Func<long, bool>? valid = null;
            // Keep raw voltage untouched; a powered-down input is not a rich AFR.
            if (setting.Unit.Equals("AFR", StringComparison.OrdinalIgnoreCase)
                && formula.InputSpecs.Any(s => s.BaseToken == "7101.10"))
            {
                var voltage = Resolve(log.Channels, "7101.10");
                var rpm = log.Channels.FirstOrDefault(c => c.Pid == 0x000C);
                var ignition = log.Channels.FirstOrDefault(c => c.Pid == 0x1141);
                valid = time => voltage?.AsOf(time) is { Number: > 0 and < 5 } v
                    && time - v.Time <= MaximumAge
                    && rpm?.AsOf(time)?.Number is not <= 0
                    && ignition?.AsOf(time)?.Number is not <= 0;
            }
            var result = new MathChannel(output, resolvedInputs, formula.Evaluate, valid);
            result.Replay();
            results.Add(result);
            available.Add(output);
        }
        log.Channels.AddRange(results.Select(result => result.Output));
        return results.ToArray();
    }

    public Channel Output => output;

    public static Func<long, bool>? BuildFilter(ScanLog log, string? filterExpression)
    {
        if (string.IsNullOrWhiteSpace(filterExpression))
            return null;
        var formula = new MathFormula(filterExpression);
        var resolved = formula.InputSpecs.Select(spec =>
            (Channel: Resolve(log.Channels, spec.BaseToken, includeMath: true), Spec: spec)).ToArray();
        if (resolved.Any(r => r.Channel is null))
            return _ => false;
        var bound = resolved.Select(r => (Channel: r.Channel!, r.Spec)).ToArray();
        var scratch = new double[bound.Length];
        return time =>
        {
            for (int i = 0; i < bound.Length; i++)
            {
                if (bound[i].Spec.Evaluate(bound[i].Channel, time, MaximumAge) is not double v)
                    return false;
                scratch[i] = v;
            }
            double res = formula.Evaluate(scratch);
            return double.IsFinite(res) && Math.Abs(res) > 1e-9;
        };
    }

    public static Channel? Resolve(IEnumerable<Channel> channels, string token, bool includeMath = false)
    {
        string baseToken = ChannelInputSpec.Parse(token).BaseToken;
        var parts = baseToken.Split('|', 2, StringSplitOptions.TrimEntries);
        string name = parts[0];
        string? explicitUnit = parts.Length > 1 ? parts[1] : null;
        int? unitCode = null;
        bool voltage = false;

        int dotIndex = name.LastIndexOf('.');
        if (dotIndex > 0 && int.TryParse(name[(dotIndex + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedUnit))
        {
            unitCode = parsedUnit;
            voltage = parsedUnit == 10;
            name = name[..dotIndex];
        }

        bool isHex = name.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        bool byPid = int.TryParse(isHex ? name[2..] : name,
            isHex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out int pid);

        var pool = channels.Where(channel => includeMath || !channel.IsMath).ToArray();
        var matches = pool.Where(channel =>
            (byPid ? MatchesPidOrVcmId(channel, pid, voltage)
                : channel.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) &&
            (explicitUnit is null || channel.Unit.Trim().Equals(explicitUnit, StringComparison.OrdinalIgnoreCase)) &&
            (!byPid || !voltage || channel.Unit.Equals("V", StringComparison.OrdinalIgnoreCase)
                || channel.Unit.Equals("Volts", StringComparison.OrdinalIgnoreCase)
                || channel.Unit.Equals("Volt", StringComparison.OrdinalIgnoreCase))).ToArray();

        if (matches.Length > 1 && unitCode.HasValue)
        {
            string vcmUnit = VcmConfigImporter.UnitSymbol(unitCode.Value);
            if (!string.IsNullOrEmpty(vcmUnit))
            {
                var unitFiltered = matches.Where(c => VcmConfigImporter.UnitsCompatible(c.Unit, vcmUnit)).ToArray();
                if (unitFiltered.Length == 1)
                    return unitFiltered[0];
            }
        }
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool MatchesPidOrVcmId(Channel channel, int id, bool voltage)
    {
        if (channel.Pid == id)
            return true;
        if (id == 7101 && voltage && channel.Pid is null or 0x1144 &&
            (channel.Name is "A/C Pressure Sensor Input" or "A/C Pressure Sensor Voltage" or "A/C High Side Pressure Sensor"))
            return true;
        return VcmConfigImporter.MatchesChannel(channel, id);
    }

    public static void SelfTest()
    {
        var log = new ScanLog("math test");
        var volts = new Channel(0, 7101, "A/C Pressure Sensor Input", "V");
        var pressure = new Channel(1, 7101, "A/C Pressure Sensor Input", "kPa");
        var rpm = new Channel(2, 12, "Engine Speed", "RPM");
        var tps = new Channel(3, 17, "Throttle Position", "%");
        var dfco = new Channel(4, 2517, "DFCO Active", "deg");
        var eqCmd = new Channel(5, 6010, "Commanded Equivalence Ratio", "EQR");
        log.Channels.AddRange([volts, pressure, rpm, tps, dfco, eqCmd]);
        volts.Readings.AddRange([new(0, 2.5), new(200, 3)]);
        rpm.Readings.AddRange([new(100, 2500), new(800, 2600)]);
        tps.Readings.AddRange([new(0, 25.0), new(100, 25.0), new(200, 25.0), new(850, 25.0)]);
        dfco.Readings.AddRange([new(0, 0), new(100, 0), new(200, 0), new(850, 0)]);
        eqCmd.Readings.AddRange([new(0, 1.0), new(100, 1.0), new(200, 1.0), new(850, 1.0)]);

        var afrError = new MathSetting("AFR Error", "%", "(([Wideband AFR|AFR] - 14.57) / 14.57) * 100");
        var clFilter = new MathSetting("CL", "",
            "[2517.161.avg(1500)]=0 and [2517.161.avg(-200)]=0 and (abs([50090.156.slope(1500)])+abs([50090.156.slope(-500)]))<2 and [50118.238] = 1.00");
        var settings = new[]
        {
            afrError,
            MathSetting.Defaults[0],
            new MathSetting("Test channel", "test", "if([12] > 2000, pow([7101.10], 2), 0)"),
            clFilter
        };
        var calculated = Build(log, settings);
        var afr = calculated[0].Output;
        var chained = calculated[1].Output;
        var afrSource = ChannelSource.From(log, afr);
        var paired = calculated[2].Output;
        var clOut = calculated[3].Output;
        if (Math.Abs(afr.Readings[0].Number!.Value - 14.57) > 1e-9 ||
            Math.Abs(chained.Readings[0].Number!.Value) > 1e-9 ||
            Math.Abs(chained.Readings[1].Number!.Value - (15.57 - 14.57) / 14.57 * 100) > 1e-6 ||
            paired.Readings[0] != new Reading(100, 6.25) ||
            paired.Readings[^1] != new Reading(800, null) ||
            clOut.Readings[1].Number != 1d ||
            Resolve(log.Channels, "7101") is not null)
            throw new InvalidDataException("Maths failed to resolve voltage, chain math channels, evaluate VCM .avg/.slope telemetry, or mark stale input.");

        volts.Readings.Add(new(850, 3));
        foreach (var ch in calculated)
            ch.Append(850);
        if (paired.Readings[^1] != new Reading(850, 9) || Math.Abs(chained.Readings[^1].Number!.Value - (15.57 - 14.57) / 14.57 * 100) > 1e-6)
            throw new InvalidDataException("Maths did not append a live chained sample.");
        if (BuildFilter(log, "[Missing Filter Channel] > 0") is not { } missingFilter || missingFilter(0))
            throw new InvalidDataException("A histogram with a missing filter input accepted samples.");
        if (ChannelInputSpec.Parse("Throttle Position.avg(-500)").Evaluate(tps, 850) is not null)
            throw new InvalidDataException("A lookahead calculation used an incomplete future window.");
        if (MathSetting.Validate([new("A", "", "[B] + 1"), new("B", "", "[A] + 1")]))
            throw new InvalidDataException("Maths validation accepted a cyclic dependency.");
        var shutdown = new ScanLog("wideband shutdown");
        var wb = new Channel(0, 7101, "A/C Pressure Sensor Input", "V");
        var running = new Channel(1, 12, "Engine Speed", "RPM");
        shutdown.Channels.AddRange([wb, running]);
        wb.Readings.AddRange([new(0, 2.5), new(100, 0), new(200, 2.5), new(300, 5)]);
        running.Readings.AddRange([new(0, 900), new(100, 800), new(200, 0), new(300, 900)]);
        var guarded = Build(shutdown, [MathSetting.Defaults[0]])[0];
        if (guarded.Output.AsOf(0)?.Number != 14.57 || guarded.Output.AsOf(100)?.Number is not null
            || guarded.Output.AsOf(200)?.Number is not null || guarded.Output.AsOf(300)?.Number is not null)
            throw new InvalidDataException("Wideband accepted a zero/railed input or stopped engine.");
        wb.Readings.Add(new(400, 2.5)); running.Readings.Add(new(400, 900)); guarded.Append(400);
        if (guarded.Output.AsOf(400)?.Number != 14.57)
            throw new InvalidDataException("Wideband did not recover after valid input returned.");
        Build(log, [settings[1] with { Name = "Renamed", Formula = "[7101.10] + 1" }]);
        if (log.Channels.Count != 7 || log.Channels[^1].Name != "Renamed" || afrSource.Resolve(log) != log.Channels[^1])
            throw new InvalidDataException("Editing Maths left an outdated channel in the log.");
    }

    private void Replay()
    {
        int[] indexes = new int[inputs.Length];
        while (true)
        {
            long time = long.MaxValue;
            for (int i = 0; i < inputs.Length; i++)
                if (indexes[i] < inputs[i].Channel.Readings.Count)
                    time = Math.Min(time, inputs[i].Channel.Readings[indexes[i]].Time);
            if (time == long.MaxValue)
                return;
            for (int i = 0; i < inputs.Length; i++)
            {
                var readings = inputs[i].Channel.Readings;
                while (indexes[i] < readings.Count && readings[indexes[i]].Time <= time)
                    indexes[i]++;
            }
            Add(time);
        }
    }

    public void Append(long time) => Add(time);

    private void Add(long time)
    {
        double? result = null;
        bool complete = valid?.Invoke(time) ?? true;
        for (int i = 0; i < inputs.Length && complete; i++)
        {
            var (ch, spec) = inputs[i];
            if (spec.Evaluate(ch, time, MaximumAge) is not double value)
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
