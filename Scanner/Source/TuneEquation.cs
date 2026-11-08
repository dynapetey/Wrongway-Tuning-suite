using System.Globalization;
using System.Text.RegularExpressions;

namespace WrongWay;

readonly record struct TuneConversion(double Scale, double Offset, bool Reciprocal = false)
{
    public double Decode(double raw) => Reciprocal ? Scale / raw : raw * Scale + Offset;
    public double Encode(double value) => Reciprocal ? Scale / value : (value - Offset) / Scale;
}

// This grammar evaluates affine and direct reciprocal conversions without executing XDF code.
sealed class TuneEquation
{
    private readonly record struct Linear(double Scale, double Offset);
    private delegate Linear Expression(double linkedValue);
    private static readonly Regex Token = new(@"\G\s*(?:(\d+(?:\.\d*)?|\.\d+)([eE][+-]?\d+)?|([A-Za-z]|[-+*/()]))");
    private static readonly Regex Reciprocal = new(@"^([+-]?(?:\d+(?:\.\d*)?|\.\d+))\s*/\s*X$", RegexOptions.IgnoreCase);
    private readonly Expression? expression;
    private readonly double numerator;

    public TuneEquation(string equation, string? linkedName)
    {
        equation = equation.Trim();
        if (equation.Length is 0 or > 256)
            throw Unsupported();
        Match reciprocal = Reciprocal.Match(equation);
        if (reciprocal.Success)
        {
            numerator = double.Parse(reciprocal.Groups[1].Value, CultureInfo.InvariantCulture);
            if (numerator == 0 || !double.IsFinite(numerator))
                throw Unsupported();
            return;
        }
        int index = 0;
        string token = "";
        Advance();
        expression = Sum();
        if (token.Length != 0)
            throw Unsupported();

        void Advance()
        {
            if (index == equation.Length)
            {
                token = "";
                return;
            }
            Match match = Token.Match(equation, index);
            if (!match.Success)
                throw Unsupported();
            index += match.Length;
            token = match.Value.Trim().ToUpperInvariant();
        }

        Expression Sum()
        {
            Expression result = Product();
            while (token is "+" or "-")
            {
                int sign = token == "+" ? 1 : -1;
                Advance();
                Expression left = result;
                Expression right = Product();
                result = linked =>
                {
                    Linear a = left(linked), b = right(linked);
                    return new Linear(a.Scale + sign * b.Scale, a.Offset + sign * b.Offset);
                };
            }
            return result;
        }

        Expression Product()
        {
            Expression result = Term();
            while (token is "*" or "/")
            {
                bool divide = token == "/";
                Advance();
                Expression left = result;
                Expression right = Term();
                result = linked =>
                {
                    Linear a = left(linked), b = right(linked);
                    if (divide)
                        return b.Scale == 0 && b.Offset != 0
                            ? new Linear(a.Scale / b.Offset, a.Offset / b.Offset) : throw Unsupported();
                    if (a.Scale != 0 && b.Scale != 0)
                        throw Unsupported();
                    return new Linear(a.Scale * b.Offset + b.Scale * a.Offset, a.Offset * b.Offset);
                };
            }
            return result;
        }

        Expression Term()
        {
            string value = token;
            if (value.Length == 0)
                throw Unsupported();
            Advance();
            if (value == "(")
            {
                Expression inner = Sum();
                if (token != ")")
                    throw Unsupported();
                Advance();
                return inner;
            }
            if (value is "+" or "-")
            {
                Expression operand = Term();
                return value == "+" ? operand : linked =>
                {
                    Linear term = operand(linked);
                    return new Linear(-term.Scale, -term.Offset);
                };
            }
            if (value == "X")
                return _ => new Linear(1, 0);
            if (value == linkedName)
                return linked => new Linear(0, linked);
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                return _ => new Linear(0, number);
            throw Unsupported();
        }
    }

    public TuneConversion Evaluate(double linked)
    {
        if (expression is null)
            return new TuneConversion(numerator, 0, Reciprocal: true);
        var (scale, offset) = expression(linked);
        if (scale == 0 || !double.IsFinite(scale) || !double.IsFinite(offset))
            throw Unsupported();
        return new TuneConversion(scale, offset);
    }

    private static FormatException Unsupported() => new("Unsupported or non-invertible XDF equation.");
}
