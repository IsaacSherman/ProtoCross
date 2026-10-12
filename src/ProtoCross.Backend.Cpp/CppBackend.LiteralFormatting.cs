using System.Globalization;
using System.Text;
using ProtoCross.Ir;
using ProtoCross.Types;

namespace ProtoCross.Backend.Cpp;

public sealed partial class CppBackend
{
    private static string EmitLiteral(IrLiteral literal) => literal.Value switch
    {
        null => "{}",
        bool value => value ? "true" : "false",
        long value when literal.LiteralType is ScalarType scalar => FormatInteger(value, scalar),
        long value => value.ToString(CultureInfo.InvariantCulture),
        ulong value when literal.LiteralType is ScalarType scalar => FormatInteger(value, scalar),
        double value => FormatDouble(value, literal.LiteralType),
        string value => FormatString(value),
        _ => throw new ArgumentOutOfRangeException(nameof(literal), literal.Value, "Unhandled literal."),
    };

    /// <summary>
    /// Formats a string literal. The lexer decodes escapes, so the IR holds real control
    /// characters; re-escaping them here is what keeps the emitted literal on one line.
    /// </summary>
    private static string FormatString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');

        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                    {
                        // Octal, not \x: a hex escape in C++ is greedy and would swallow any hex
                        // digit that happens to follow it. Octal escapes stop after three digits.
                        builder.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
                    }
                    else
                    {
                        // Non-ASCII passes through as UTF-8; generated files are written as UTF-8.
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>Formats a signed integer literal with the suffix its type requires.</summary>
    /// <remarks>
    /// <para>
    /// A negative one is parenthesized, so that it reads as one expression wherever it lands, as
    /// everything else this backend emits does.
    /// </para>
    /// <para>
    /// The most negative value of each type has no literal of its own. C++ has no negative literals,
    /// only negations of positive ones, and the magnitude of each MIN is one more than its type's MAX:
    /// <c>2147483648</c> is not an <c>int</c>, and <c>9223372036854775808</c> is not a <c>long long</c>
    /// -- or anything signed at all. Each is spelled the way <c>&lt;climits&gt;</c> spells it, as MAX
    /// negated less one, which is a constant expression of exactly the right type.
    /// </para>
    /// </remarks>
    private static string FormatInteger(long value, ScalarType scalar)
    {
        if (value == long.MinValue)
        {
            return "(-9223372036854775807LL - 1)";
        }

        if (scalar.Kind == ScalarKind.Int32 && value == int.MinValue)
        {
            return "(-2147483647 - 1)";
        }

        var text = value.ToString(CultureInfo.InvariantCulture) + IntegerSuffix(scalar);
        return value < 0 ? $"({text})" : text;
    }

    private static string FormatInteger(ulong value, ScalarType scalar)
        => value.ToString(CultureInfo.InvariantCulture) + IntegerSuffix(scalar);

    private static string IntegerSuffix(ScalarType scalar) => scalar.Kind switch
    {
        ScalarKind.Int64 => "LL",
        ScalarKind.UInt64 => "ULL",
        ScalarKind.UInt32 => "U",
        _ => string.Empty,
    };

    private static string FormatDouble(double value, PlType type)
    {
        var isFloat = type is ScalarType { Kind: ScalarKind.Float };

        // No literal form in C++; these come from <limits> via the numeric_limits template.
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            var cppType = isFloat ? "float" : "double";
            var accessor = double.IsNaN(value) ? "quiet_NaN()" : "infinity()";
            var text2 = $"::std::numeric_limits<{cppType}>::{accessor}";
            return double.IsNegativeInfinity(value) ? $"(-{text2})" : text2;
        }

        // A float is spelled as the float it is, in the fewest digits that give it back, rather than as
        // the double that holds it: both round-trip, but only the first is what an author wrote.
        var text = isFloat
            ? ((float)value).ToString("R", CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);

        if (!text.Contains('.', StringComparison.Ordinal)
            && !text.Contains('E', StringComparison.Ordinal)
            && !text.Contains('e', StringComparison.Ordinal))
        {
            text += ".0";
        }

        if (isFloat)
        {
            text += "f";
        }

        return double.IsNegative(value) ? $"({text})" : text;
    }
}
