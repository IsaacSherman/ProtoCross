using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Types;

namespace ProtoCross.Backend.CSharp;

public sealed partial class CSharpBackend
{
    private static string EmitBinary(IrBinary binary, Placement placement, string receiverName)
    {
        // The binder gives two maps no operator but == and !=.
        if (binary.Left.Type is MapType)
        {
            return EmitMapEquality(binary, placement, receiverName);
        }

        var left = Expression(binary.Left, placement, receiverName);
        var right = Expression(binary.Right, placement, receiverName);
        var op = OperatorText(binary.Operator);

        // Integer / and % arrive as IrIntegerDivision, so only + - * reach here.
        if (binary.OverflowingType is not null)
        {
            // Wrapping is the one policy C# can state inline: unchecked() gives both the semantics
            // and the grouping parentheses. The other two need to inspect the result, so they go
            // through the runtime.
            if (binary.Behavior == ArithmeticBehavior.Wrap)
            {
                return $"unchecked({left} {op} {right})";
            }

            var helper = binary.Operator switch
            {
                IrBinaryOperator.Add => "Add",
                IrBinaryOperator.Subtract => "Subtract",
                IrBinaryOperator.Multiply => "Multiply",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(binary), binary.Operator, "Not an overflowing binary operator."),
            };

            return $"{CSharpRuntime.TypeName}.{CSharpRuntime.Stem(binary.Behavior)}{helper}({left}, {right})";
        }

        if (binary.ShiftCountMask is { } mask)
        {
            return $"({left} {op} {ShiftCount(binary.Right, right, mask)})";
        }

        return $"({left} {op} {right})";
    }

    /// <summary>A shift's count, reduced to its low bits and made the <c>int</c> C# shifts by.</summary>
    /// <remarks>
    /// C# masks a count itself, but only an <c>int</c> one, and a <c>long</c> or <c>ulong</c> count
    /// has to be narrowed to reach it. Narrowing first would throw under a consumer's
    /// <c>CheckForOverflowUnderflow</c>, so the mask comes first and the cast after it, when what is
    /// cast is already small enough for any context. The mask is written even for an <c>int</c> count,
    /// which C# would mask the same way unaided, because spec 10.1 puts the rule in the generated code
    /// rather than in the target's default.
    /// </remarks>
    private static string ShiftCount(IrExpression count, string emitted, int mask)
        => count.Type is ScalarType { Kind: ScalarKind.Int32 }
            ? $"({emitted} & {mask})"
            : $"(int)({emitted} & {mask})";

    /// <remarks>
    /// A fallback is written after <c>??</c>, which evaluates it only where the helper found a zero
    /// divisor, so a fallback that can end the program does not end one whose divisor was not zero
    /// (spec 10.2.1). A division whose clause refuses was tested before its statement, and is what that
    /// test held (<see cref="EmitClauseRefusal"/>).
    /// </remarks>
    private static string EmitIntegerDivision(IrIntegerDivision division, Placement placement, string receiverName)
        => division.ZeroBehavior switch
        {
            ZeroDivisorBehavior.Unreachable => Divided(division, string.Empty, placement, receiverName),
            ZeroDivisorBehavior.Fail => Divided(division, "OrFail", placement, receiverName),
            ZeroDivisorBehavior.Fallback =>
                $"({Divided(division, "OrNull", placement, receiverName)} ?? {Expression(division.OnZero!, placement, receiverName)})",
            ZeroDivisorBehavior.Refuse => placement.Built[division],
            _ => throw new ArgumentOutOfRangeException(
                nameof(division), division.ZeroBehavior, "Unhandled zero-divisor behavior."),
        };

    /// <summary>
    /// <paramref name="division"/> through the runtime's helper for it, of <paramref name="form"/>: the bare
    /// helper, <c>OrFail</c>, or <c>OrNull</c>, which gives null for a zero divisor.
    /// </summary>
    /// <remarks>
    /// Every form goes through a helper, even one whose divisor is a non-zero literal: <c>MIN / -1</c> traps at
    /// the hardware level regardless of <c>unchecked</c>.
    /// </remarks>
    private static string Divided(IrIntegerDivision division, string form, Placement placement, string receiverName)
    {
        var stem = CSharpRuntime.Stem(division.Behavior)
            + (division.Operator == IrBinaryOperator.Modulo ? "Modulo" : "Divide");

        return $"{CSharpRuntime.TypeName}.{stem}{form}("
            + $"{Expression(division.Left, placement, receiverName)}, {Expression(division.Right, placement, receiverName)})";
    }

    private static string EmitUnary(IrUnary unary, Placement placement, string receiverName)
    {
        var operand = Expression(unary.Operand, placement, receiverName);

        if (unary.Operator == IrUnaryOperator.Negate)
        {
            if (unary.OverflowingType is null)
            {
                return $"(-{operand})";
            }

            return unary.Behavior == ArithmeticBehavior.Wrap
                ? $"unchecked(-{operand})"
                : $"{CSharpRuntime.TypeName}.{CSharpRuntime.Stem(unary.Behavior)}Negate({operand})";
        }

        return unary.Operator == IrUnaryOperator.BitwiseNot ? $"(~{operand})" : $"(!{operand})";
    }

    /// <summary>
    /// Emits an explicit conversion (spec 10.3).
    /// </summary>
    /// <remarks>
    /// Integer targets are wrapped in <c>unchecked</c>, which states the wrapping and also stops a
    /// consumer's <c>CheckForOverflowUnderflow</c> from turning a deliberate narrowing into an
    /// <see cref="OverflowException"/>. Conversions to a floating-point type are fully defined in
    /// C# and unaffected by checked context, so a plain cast says everything. A floating-point
    /// source converting to an integer is the one case the language leaves unspecified when the
    /// value is out of range -- and throws under a checked context -- so it goes through the
    /// runtime, where the saturating result is spelled out.
    /// </remarks>
    private static string EmitConversion(IrConversion conversion, Placement placement, string receiverName)
    {
        if (conversion.Behavior != ConversionBehavior.WrapOrSaturate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(conversion), conversion.Behavior, "Unhandled conversion behavior.");
        }

        // Every Expression() result is self-delimiting -- an identifier, a member access, a call, a
        // non-negative literal, an object creation, or something already wrapped in parentheses --
        // so a cast can be prefixed without re-parenthesizing the operand.
        var operand = Expression(conversion.Operand, placement, receiverName);
        var target = TypeName(conversion.TargetType);

        return conversion.Kind switch
        {
            ConversionKind.Identity => operand,
            ConversionKind.IntegerToInteger => $"unchecked(({target}){operand})",
            ConversionKind.IntegerToFloat or ConversionKind.FloatToFloat => $"({target}){operand}",
            ConversionKind.FloatToInteger =>
                $"{CSharpRuntime.TypeName}.{FloatToIntegerHelper(conversion.TargetType)}((double){operand})",
            _ => throw new ArgumentOutOfRangeException(
                nameof(conversion), conversion.Kind, "Unhandled conversion kind."),
        };
    }

    /// <summary>Emits an enum value made from a number (spec 12).</summary>
    /// <remarks>
    /// C# stores any <c>int</c> in an enum, so keeping a number needs only the cast. A fallback and a
    /// failure go through the runtime, which asks <c>Enum.IsDefined</c> whether the schema names the
    /// number. protoc's C# enum declares exactly the values the schema does, aliases included, so the
    /// answer is the one C++'s <c>_IsValid</c> gives. A fallback is written after <c>??</c>, so it is
    /// evaluated only for a number the schema does not name (spec 12.1). A conversion whose clause refuses was
    /// tested before its statement, and is what that test held (<see cref="EmitClauseRefusal"/>).
    /// </remarks>
    private static string EmitNumberToEnum(IrNumberToEnum conversion, Placement placement, string receiverName)
        => conversion.OnUnnamed switch
        {
            UnnamedNumberBehavior.Keep => CastToEnum(conversion, placement, receiverName),
            UnnamedNumberBehavior.Fallback =>
                $"({NamedOrNull(conversion, placement, receiverName)} ?? {FallbackOf(conversion, placement, receiverName)})",
            UnnamedNumberBehavior.Fail =>
                $"{CSharpRuntime.EnumsTypeName}.NamedOrFail({CastToEnum(conversion, placement, receiverName)}, "
                + $"{FormatString(conversion.EnumType.DisplayName)})",
            UnnamedNumberBehavior.Refuse => placement.Built[conversion],
            _ => throw new ArgumentOutOfRangeException(
                nameof(conversion), conversion.OnUnnamed, "Unhandled unnamed-number behavior."),
        };

    /// <summary>The number cast to the enum, which C# allows whether or not the enum names it.</summary>
    private static string CastToEnum(IrNumberToEnum conversion, Placement placement, string receiverName)
        => $"(({TypeName(conversion.EnumType)}){Expression(conversion.Operand, placement, receiverName)})";

    /// <summary>The enum value, or null where the enum does not name the number.</summary>
    private static string NamedOrNull(IrNumberToEnum conversion, Placement placement, string receiverName)
        => $"{CSharpRuntime.EnumsTypeName}.NamedOrNull({CastToEnum(conversion, placement, receiverName)})";

    /// <summary>
    /// The value a conversion to an enum falls back to: the one its clause wrote, or the one the
    /// project's configuration names (spec 12.1).
    /// </summary>
    private static string FallbackOf(IrNumberToEnum conversion, Placement placement, string receiverName)
        => conversion.Fallback is { } written
            ? Expression(written, placement, receiverName)
            : EnumValue(conversion.ConfiguredFallback!);

    /// <summary>
    /// The runtime helper for a floating-point to integer conversion. The source is widened to
    /// <c>double</c> at the call site, which is exact, so one helper per target covers both
    /// <c>float</c> and <c>double</c> sources.
    /// </summary>
    private static string FloatToIntegerHelper(ScalarType target) => target.Kind switch
    {
        ScalarKind.Int32 => "ToInt32",
        ScalarKind.Int64 => "ToInt64",
        ScalarKind.UInt32 => "ToUInt32",
        ScalarKind.UInt64 => "ToUInt64",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target.Kind, "Not an integer kind."),
    };

    private static string OperatorText(IrBinaryOperator op) => op switch
    {
        IrBinaryOperator.Add => "+",
        IrBinaryOperator.Subtract => "-",
        IrBinaryOperator.Multiply => "*",
        IrBinaryOperator.Divide => "/",
        IrBinaryOperator.Modulo => "%",
        IrBinaryOperator.Equal => "==",
        IrBinaryOperator.NotEqual => "!=",
        IrBinaryOperator.LessThan => "<",
        IrBinaryOperator.LessThanOrEqual => "<=",
        IrBinaryOperator.GreaterThan => ">",
        IrBinaryOperator.GreaterThanOrEqual => ">=",
        IrBinaryOperator.LogicalAnd => "&&",
        IrBinaryOperator.LogicalOr => "||",
        IrBinaryOperator.BitwiseAnd => "&",
        IrBinaryOperator.BitwiseOr => "|",
        IrBinaryOperator.BitwiseXor => "^",
        IrBinaryOperator.ShiftLeft => "<<",
        IrBinaryOperator.ShiftRight => ">>",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unhandled operator."),
    };
}
