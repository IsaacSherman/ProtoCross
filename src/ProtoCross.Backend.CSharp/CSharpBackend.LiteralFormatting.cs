using System.Globalization;
using System.Text;
using ProtoCross.Ir;
using ProtoCross.Types;

namespace ProtoCross.Backend.CSharp;

public sealed partial class CSharpBackend
{
    private static string EmitLiteral(IrLiteral literal) => literal.Value switch
    {
        null => "default",
        bool value => value ? "true" : "false",
        long value => literal.LiteralType is ScalarType scalar
            ? FormatInteger(value, scalar)
            : value.ToString(CultureInfo.InvariantCulture),
        ulong value when literal.LiteralType is ScalarType scalar => FormatInteger(value, scalar),
        double value => FormatFloatingPoint(value, literal.LiteralType),
        string value => FormatString(value),
        _ => throw new ArgumentOutOfRangeException(nameof(literal), literal.Value, "Unhandled literal."),
    };

    /// <summary>
    /// Formats a floating-point literal with the suffix its type requires. A <c>float</c>-typed
    /// literal must carry <c>f</c>: C# will not implicitly narrow a <c>double</c> literal.
    /// </summary>
    private static string FormatFloatingPoint(double value, PlType type)
    {
        var isFloat = type is ScalarType { Kind: ScalarKind.Float };
        var typeName = isFloat ? "float" : "double";

        // These have no literal form in C# and would otherwise emit as 'Infinityd'.
        if (double.IsNaN(value))
        {
            return $"{typeName}.NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return $"{typeName}.PositiveInfinity";
        }

        if (double.IsNegativeInfinity(value))
        {
            return $"{typeName}.NegativeInfinity";
        }

        // A float is spelled as the float it is, in the fewest digits that give it back, rather than as
        // the double that holds it: both round-trip, but only the first is what an author wrote.
        var text = isFloat
            ? ((float)value).ToString("R", CultureInfo.InvariantCulture) + "f"
            : value.ToString("R", CultureInfo.InvariantCulture) + "d";

        return double.IsNegative(value) ? $"({text})" : text;
    }

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
                case '\0': builder.Append("\\0"); break;
                default:
                    // \uXXXX is fixed-width, so it cannot absorb the character that follows.
                    if (char.IsControl(c))
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
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
    /// A negative one is parenthesized, so that every expression this backend emits stays
    /// self-delimiting (see <see cref="EmitConversion"/>). Bare, <c>-5L</c> under a negation would read
    /// <c>--5L</c>, which is a decrement. int32 MIN and int64 MIN need nothing more than that: C# reads
    /// a <c>-</c> directly before <c>2147483648</c>, or before <c>9223372036854775808L</c>, as the one
    /// constant each spells.
    /// </remarks>
    private static string FormatInteger(long value, ScalarType scalar)
    {
        var text = value.ToString(CultureInfo.InvariantCulture) + IntegerSuffix(scalar);
        return value < 0 ? $"({text})" : text;
    }

    private static string FormatInteger(ulong value, ScalarType scalar)
        => value.ToString(CultureInfo.InvariantCulture) + IntegerSuffix(scalar);

    private static string IntegerSuffix(ScalarType scalar) => scalar.Kind switch
    {
        ScalarKind.Int64 => "L",
        ScalarKind.UInt64 => "UL",
        ScalarKind.UInt32 => "U",
        _ => string.Empty,
    };
}
