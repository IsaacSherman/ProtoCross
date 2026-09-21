using System.Globalization;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Binding;

public sealed partial class Binder
{
    // --- numeric literals ---
    //
    // Which type a literal takes, and which value it has there (spec 10.3). A literal is the one
    // expression that takes its type from where it is used rather than from what it is, so it is also
    // the one place a value is rounded or range-checked against a type the author never wrote down.

    /// <summary>An integer literal as written: its magnitude, and whether a <c>-</c> was written on it.</summary>
    private readonly record struct WrittenInteger(ulong Magnitude, bool IsNegative)
    {
        /// <summary>The value, which lies within uint64 MAX of zero either side.</summary>
        public Int128 Value => IsNegative ? -(Int128)Magnitude : Magnitude;

        public override string ToString()
            => (IsNegative ? "-" : string.Empty) + Magnitude.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The integer literal <paramref name="expression"/> is, with a <c>-</c> written directly on it
    /// folded in, or null when it is not one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fold is what lets int32 MIN and int64 MIN be literals at all. The magnitude of each is one
    /// more than its type's MAX, so a literal that took the type first and was negated afterwards
    /// could never reach either one.
    /// </para>
    /// <para>
    /// It takes one <c>-</c> only: in <c>-(-5)</c> the outer one negates a negative literal, and is
    /// ordinary arithmetic under the overflow policy. Parentheses between the sign and the digits make
    /// no difference, because the parser does not keep them, and they would make none to the value.
    /// </para>
    /// </remarks>
    private static WrittenInteger? IntegerLiteralOf(Expression expression) => expression switch
    {
        IntegerLiteralExpression literal => new WrittenInteger(literal.Value, IsNegative: false),
        UnaryExpression { Operator: UnaryOperatorKind.Negate, Operand: IntegerLiteralExpression literal }
            => new WrittenInteger(literal.Value, IsNegative: true),
        _ => null,
    };

    /// <summary>
    /// Whether <paramref name="expression"/> is a numeric literal as written, a <c>-</c> written
    /// directly on it included -- which is to say, something that will take whatever type it is asked
    /// to.
    /// </summary>
    private static bool IsNumericLiteral(Expression expression)
        => IntegerLiteralOf(expression) is not null
            || expression is FloatLiteralExpression
                or UnaryExpression { Operator: UnaryOperatorKind.Negate, Operand: FloatLiteralExpression };

    /// <summary>
    /// Binds an integer literal in the type expected of it where that is a numeric type, and in its
    /// natural type where nothing is expected.
    /// </summary>
    private IrExpression BindIntegerLiteral(WrittenInteger literal, SourceSpan span, PlType? expectedType)
    {
        if (expectedType is ScalarType { IsFloatingPoint: true } floatingPoint)
        {
            return new IrLiteral(RoundedTo(floatingPoint, literal), floatingPoint, span);
        }

        var type = expectedType is ScalarType { IsInteger: true } integer ? integer : NaturalType(literal.Value);

        if (!FitsIn(literal.Value, type))
        {
            _diagnostics.Report(
                DiagnosticCodes.LiteralOutOfRangeForItsType,
                $"{literal} is outside the range of '{type.DisplayName}'.",
                span);
        }

        return new IrLiteral(Representation(literal.Value, type), type, span);
    }

    /// <summary>
    /// The type an integer literal takes where nothing expects one: <c>int64</c>, or <c>uint64</c>
    /// where only <c>uint64</c> can hold the value.
    /// </summary>
    /// <remarks>
    /// <c>int64</c> first, because that is what an unadorned literal has always been, and a literal
    /// nobody typed is far more often a count than a bit pattern. <c>uint64</c> second, because the
    /// alternative is refusing <c>0xFFFF_FFFF_FFFF_FFFF</c> -- a mask the language has only just
    /// learned to spell -- anywhere it is not assigned straight into a <c>uint64</c>. A value that fits
    /// neither is reported against <c>int64</c>, the type it would otherwise have had.
    /// </remarks>
    private static ScalarType NaturalType(Int128 value)
        => !FitsIn(value, ScalarType.Int64Type) && FitsIn(value, ScalarType.UInt64Type)
            ? ScalarType.UInt64Type
            : ScalarType.Int64Type;

    private static bool FitsIn(Int128 value, ScalarType scalar) => scalar.Kind switch
    {
        ScalarKind.Int32 => value >= int.MinValue && value <= int.MaxValue,
        ScalarKind.Int64 => value >= long.MinValue && value <= long.MaxValue,
        ScalarKind.UInt32 => value >= 0 && value <= uint.MaxValue,
        ScalarKind.UInt64 => value >= 0 && value <= ulong.MaxValue,
        _ => false,
    };

    /// <summary>
    /// What an integer literal of <paramref name="type"/> holds: a <see cref="long"/> if the type is
    /// signed, and a <see cref="ulong"/> if it is not.
    /// </summary>
    /// <remarks>
    /// One representation per signedness rather than per width, so a reader of a literal has two cases
    /// to handle instead of four, and every value of every integer type fits the one it gets. The
    /// conversion truncates; only a literal already reported as out of range reaches that, and
    /// truncating keeps the node well formed for whatever reads it next.
    /// </remarks>
    private static object Representation(Int128 value, ScalarType type)
        => type.IsSigned ? (object)unchecked((long)value) : unchecked((ulong)value);

    /// <summary>
    /// An integer literal's value in a floating-point type: its magnitude rounded once, and then its
    /// sign.
    /// </summary>
    /// <remarks>
    /// The sign goes on last so that <c>-0</c> is negative zero where a floating-point type is expected,
    /// which is the value <c>-0.0</c> has. It is also the value <c>-0</c> had before a <c>-</c> was
    /// folded into a literal, when the literal took the type first and was negated second, so the fold
    /// changes how the value is spelled and not what it is. Rounding the magnitude and then negating
    /// is otherwise the same as rounding the negative value, because ties go to even either way up.
    /// </remarks>
    private static double RoundedTo(ScalarType floatingPoint, WrittenInteger literal)
    {
        var rounded = FloatingPointValue.Parse(literal.Magnitude.ToString(CultureInfo.InvariantCulture));
        var magnitude = floatingPoint.Kind == ScalarKind.Float ? rounded.Single : rounded.Double;

        return literal.IsNegative ? -magnitude : magnitude;
    }

    /// <summary>
    /// Binds a floating-point literal: a <c>float</c> where a <c>float</c> is expected, and a
    /// <c>double</c> everywhere else.
    /// </summary>
    /// <remarks>
    /// A <c>float</c> literal holds the float its decimal rounds to in one step, widened to a double
    /// exactly. The double it would otherwise hold is a different number, which each backend would
    /// print and each target compiler would round a second time.
    /// </remarks>
    private IrExpression BindFloatLiteral(FloatLiteralExpression literal, PlType? expectedType)
    {
        if (expectedType is not ScalarType { Kind: ScalarKind.Float })
        {
            return new IrLiteral(literal.Value, ScalarType.DoubleType, literal.Span);
        }

        // Out of range for a float only where it was in range for a double. One that was not has been
        // reported where it was read, and __INF is meant to be infinite as both.
        if (float.IsInfinity(literal.SingleValue) && !double.IsInfinity(literal.Value))
        {
            _diagnostics.Report(
                DiagnosticCodes.FloatingPointLiteralOutOfRange,
                $"{literal.Value.ToString("R", CultureInfo.InvariantCulture)} is outside the range of 'float'.",
                literal.Span,
                "The largest float is 3.4028235e38. If an infinity is what you mean, write __INF.");
        }

        return new IrLiteral((double)literal.SingleValue, ScalarType.FloatType, literal.Span);
    }
}
