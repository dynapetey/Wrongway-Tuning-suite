using System.Globalization;
using System.Linq.Expressions;
using System.Text.RegularExpressions;

namespace WrongWay;

readonly record struct TuneConversion(double Scale, double Offset, bool Reciprocal = false,
    Func<double, double>? Forward = null, Func<double, double>? Inverse = null)
{
    public double Decode(double raw) => Forward is not null ? Forward(raw) : Reciprocal ? Scale / raw : raw * Scale + Offset;
    public double Encode(double value) => Inverse is not null ? Inverse(value) : Reciprocal ? Scale / value : (value - Offset) / Scale;
}

// Evaluates affine, reciprocal, and monotonic nonlinear XDF/user equations without executing arbitrary code.
sealed class TuneEquation
{
    private readonly record struct Linear(double Scale, double Offset);
    private delegate Linear LinearExpression(double linkedValue);
    private static readonly Regex Token = new(@"\G\s*(?:(\d+(?:\.\d*)?|\.\d+)([eE][+-]?\d+)?|([A-Za-z]|[-+*/()]))");
    private static readonly Regex GeneralTokens = new(
        @"\G\s*(?:(\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?|[a-zA-Z][a-zA-Z0-9]*|[-+*/^(),])");
    private static readonly Regex Reciprocal = new(@"^([+-]?(?:\d+(?:\.\d*)?|\.\d+))\s*/\s*X$", RegexOptions.IgnoreCase);
    private readonly LinearExpression? expression;
    private readonly double numerator;
    private Func<double, double, double>? generalForward;
    private readonly string formula;
    private readonly string? linkedVariable;
    private (double Linked, int Width, bool Signed, TuneConversion Conversion)? lastNonlinear;

    public TuneEquation(string equation, string? linkedName)
    {
        equation = equation.Trim();
        formula = equation;
        linkedVariable = linkedName?.ToUpperInvariant();
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
        try
        {
            expression = ParseAffine(equation, linkedVariable);
        }
        catch (FormatException)
        {
            generalForward = ParseGeneral(equation, linkedVariable);
        }
    }

    private static LinearExpression ParseAffine(string equation, string? linkedName)
    {
        int index = 0;
        string token = "";
        Advance();
        LinearExpression result = Sum();
        if (token.Length != 0)
            throw Unsupported();
        return result;

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

        LinearExpression Sum()
        {
            LinearExpression leftExpr = Product();
            while (token is "+" or "-")
            {
                int sign = token == "+" ? 1 : -1;
                Advance();
                LinearExpression left = leftExpr;
                LinearExpression right = Product();
                leftExpr = linked =>
                {
                    Linear a = left(linked), b = right(linked);
                    return new Linear(a.Scale + sign * b.Scale, a.Offset + sign * b.Offset);
                };
            }
            return leftExpr;
        }

        LinearExpression Product()
        {
            LinearExpression leftExpr = Term();
            while (token is "*" or "/")
            {
                bool divide = token == "/";
                Advance();
                LinearExpression left = leftExpr;
                LinearExpression right = Term();
                leftExpr = linked =>
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
            return leftExpr;
        }

        LinearExpression Term()
        {
            string value = token;
            if (value.Length == 0)
                throw Unsupported();
            Advance();
            if (value == "(")
            {
                LinearExpression inner = Sum();
                if (token != ")")
                    throw Unsupported();
                Advance();
                return inner;
            }
            if (value is "+" or "-")
            {
                LinearExpression operand = Term();
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

    private static Func<double, double, double> ParseGeneral(string equation, string? linkedName)
    {
        var matches = GeneralTokens.Matches(equation);
        if (matches.Sum(m => m.Length) != equation.Length)
            throw Unsupported();
        string[] tokens = matches.Select(m => m.Value.Trim()).ToArray();
        int pos = 0;
        var xParam = Expression.Parameter(typeof(double), "X");
        var linkParam = Expression.Parameter(typeof(double), "L");
        bool usesX = false;

        string Peek() => pos < tokens.Length ? tokens[pos] : "";

        Expression ParseExpr(int minPrec)
        {
            Expression left = ParseTerm();
            while (true)
            {
                string op = Peek();
                int prec = op switch { "+" or "-" => 1, "*" or "/" => 2, "^" => 4, _ => 0 };
                if (prec < minPrec || prec == 0)
                    return left;
                pos++;
                var right = ParseExpr(op == "^" ? prec : prec + 1);
                left = op switch
                {
                    "+" => Expression.Add(left, right),
                    "-" => Expression.Subtract(left, right),
                    "*" => Expression.Multiply(left, right),
                    "/" => Expression.Divide(left, right),
                    _ => Expression.Power(left, right)
                };
            }
        }

        Expression ParseTerm()
        {
            string tok = Peek();
            if (tok.Length == 0)
                throw Unsupported();
            pos++;
            if (tok == "-")
                return Expression.Negate(ParseExpr(3));
            if (tok == "+")
                return ParseExpr(3);
            if (tok == "(")
            {
                var inner = ParseExpr(0);
                if (Peek() != ")")
                    throw Unsupported();
                pos++;
                return inner;
            }
            if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double num) && double.IsFinite(num))
                return Expression.Constant(num);
            string upper = tok.ToUpperInvariant();
            if (upper == "X")
            {
                usesX = true;
                return xParam;
            }
            if (linkedName is not null && upper == linkedName)
                return linkParam;

            (string method, int arity) = tok.ToLowerInvariant() switch
            {
                "abs" => (nameof(Math.Abs), 1),
                "sqrt" => (nameof(Math.Sqrt), 1),
                "log" => (nameof(Math.Log), 1),
                "log10" => (nameof(Math.Log10), 1),
                "exp" => (nameof(Math.Exp), 1),
                "pow" => (nameof(Math.Pow), 2),
                _ => throw Unsupported()
            };
            if (Peek() != "(")
                throw Unsupported();
            pos++;
            var args = new Expression[arity];
            for (int i = 0; i < arity; i++)
            {
                if (i > 0)
                {
                    if (Peek() != ",")
                        throw Unsupported();
                    pos++;
                }
                args[i] = ParseExpr(0);
            }
            if (Peek() != ")")
                throw Unsupported();
            pos++;
            return Expression.Call(typeof(Math), method, Type.EmptyTypes, args);
        }

        var body = ParseExpr(0);
        if (pos != tokens.Length || !usesX)
            throw Unsupported();
        return Expression.Lambda<Func<double, double, double>>(body, xParam, linkParam).Compile();
    }

    public TuneConversion Evaluate(double linked, int width, bool signed, bool floating)
    {
        if (expression is null && generalForward is null)
            return new TuneConversion(numerator, 0, Reciprocal: true);
        if (expression is not null && generalForward is null)
        {
            try
            {
                var (scale, offset) = expression(linked);
                if (scale == 0 || !double.IsFinite(scale) || !double.IsFinite(offset))
                    throw Unsupported();
                return new TuneConversion(scale, offset);
            }
            catch (FormatException)
            {
                // A syntactically affine expression can still contain a product or divisor in X.
                // Keep trying the affine path if its linked value may make it valid later.
            }
        }

        if (floating || width is not (1 or 2))
            throw new FormatException("Nonlinear editing requires an 8-bit or 16-bit integer storage type.");
        if (lastNonlinear is { } last && last.Linked == linked && last.Width == width && last.Signed == signed)
            return last.Conversion;

        var parsed = generalForward ?? ParseGeneral(formula, linkedVariable);
        double Forward(double raw) => parsed(raw, linked);
        int magnitude = 1 << (width * 8 - (signed ? 1 : 0));
        int lower = signed ? -magnitude : 0;
        int upper = magnitude - 1;
        double first = Forward(lower);
        double lastValue = Forward(upper);
        if (!double.IsFinite(first) || !double.IsFinite(lastValue) || first == lastValue)
            throw Unsupported();

        bool ascending = lastValue > first;
        double previous = first;
        // Prove the direction at every representable raw value before doing a binary search.
        for (int raw = lower + 1; raw <= upper; raw++)
        {
            double value = Forward(raw);
            if (!double.IsFinite(value) || (ascending ? value <= previous : value >= previous))
                throw Unsupported();
            previous = value;
        }

        double Inverse(double target)
        {
            if (!double.IsFinite(target) || target < Math.Min(first, lastValue) || target > Math.Max(first, lastValue))
                throw new ArgumentOutOfRangeException(nameof(target), "The value exceeds the nonlinear conversion range.");
            int lo = lower, hi = upper;
            while (hi - lo > 1)
            {
                int middle = lo + (hi - lo) / 2;
                if ((Forward(middle) < target) == ascending)
                    lo = middle;
                else
                    hi = middle;
            }
            return Math.Abs(Forward(lo) - target) < Math.Abs(Forward(hi) - target) ? lo : hi;
        }

        var conversion = new TuneConversion(1, 0, Forward: Forward, Inverse: Inverse);
        lastNonlinear = (linked, width, signed, conversion);
        return conversion;
    }

    private static FormatException Unsupported() => new("Unsupported or non-invertible XDF equation.");
}
