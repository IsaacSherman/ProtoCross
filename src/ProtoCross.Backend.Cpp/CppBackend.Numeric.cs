using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Types;

namespace ProtoCross.Backend.Cpp;

public sealed partial class CppBackend
{
    private static string EmitBinary(IrBinary binary, Placement placement)
    {
        // The binder gives two maps no operator but == and !=.
        if (binary.Left.Type is MapType)
        {
            return EmitMapEquality(binary, placement);
        }

        var left = Expression(binary.Left, placement);
        var right = Expression(binary.Right, placement);

        if (binary.OverflowingType is { } scalar)
        {
            // Never a bare operator, under any policy: signed overflow is undefined behavior, so
            // even the wrapping case has to be spelled out in the unsigned domain.
            var stem = CppRuntime.Stem(binary.Behavior);
            return $"{RuntimeNamespace}::{stem}_{ArithmeticHelperName(binary.Operator)}_{HelperSuffix(scalar)}"
                + $"({left}, {right})";
        }

        if (IsFloatingRemainder(binary))
        {
            return $"::std::fmod({left}, {right})";
        }

        if (IsFoldableFloatingDivision(binary))
        {
            // A call is not a constant expression, so the quotient is left for the program to work
            // out, exactly as one taken from fields already is. ::std::divides is specified to
            // return left / right and nothing besides, which keeps the repair in this header
            // instead of in protocross_runtime.h, which every generated project carries.
            return $"::std::divides<{TypeName(binary.ResultType)}>{{}}({left}, {right})";
        }

        // C++20 defines a shift of a signed value, left and right, but only by a count from zero to
        // one less than the width: anything else is undefined behavior. The mask is what brings every
        // count into that range, which is also what spec 10.1 says a count means.
        if (binary.ShiftCountMask is { } mask)
        {
            return $"({left} {OperatorText(binary.Operator)} ({right} & {mask}))";
        }

        return $"({left} {OperatorText(binary.Operator)} {right})";
    }

    /// <summary>
    /// Whether <paramref name="binary"/> is <c>%</c> on <c>float</c> or <c>double</c> operands, which
    /// C++ spells <c>std::fmod</c> rather than <c>%</c>.
    /// </summary>
    /// <remarks>
    /// C++ defines the built-in <c>%</c> on integers only, so a bare operator here does not compile.
    /// <c>std::fmod</c> computes exactly the remainder spec 10.2 states -- truncated, exact, carrying
    /// the sign of the dividend -- and it is fully specified by the C standard, so no runtime helper
    /// is needed the way the undefined integer and conversion cases need one. The emitter and
    /// <see cref="UsesFloatingRemainder"/> both ask this, so the <c>&lt;cmath&gt;</c> include can never
    /// fall out of step with the calls that need it.
    /// </remarks>
    private static bool IsFloatingRemainder(IrBinary binary)
        => binary.Operator == IrBinaryOperator.Modulo
            && binary.ResultType is ScalarType { IsFloatingPoint: true };

    /// <summary>
    /// Whether anything in <paramref name="module"/> is a floating-point remainder, and so needs
    /// <c>&lt;cmath&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The include is conditional rather than unconditional so that a header for a module with no
    /// floating remainder stays byte-for-byte what it was before this construct compiled at all.
    /// </remarks>
    private static bool UsesFloatingRemainder(IrModule module)
        => IrWalk.DescendantsAndSelf(module).OfType<IrBinary>().Any(IsFloatingRemainder);

    /// <summary>
    /// Whether <paramref name="binary"/> is a floating-point <c>/</c> the C++ front end would work
    /// out for itself, which is the case a compiler may refuse rather than emit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A division by zero is undefined behavior in C++ however it is written, so a compiler that can
    /// see one is entitled to reject it, and MSVC does: <c>1.0 / 0.0</c> is error C2124, "divide or
    /// mod by zero", and the generated header does not compile at all. Spec 10.2 says that quotient
    /// is an infinity, so the language would be promising an answer one backend cannot deliver for
    /// the most direct way an author can ask for it.
    /// </para>
    /// <para>
    /// Both operands, not just the divisor. The refusal comes from constant folding, so it needs
    /// every operand to be a constant: <c>self.numerator() / 0.0</c> compiles, and it is still
    /// emitted as a bare <c>/</c>. Asking about the divisor alone would put a function call around
    /// every <c>x / 2.0</c> in the corpus to fix something that was never broken.
    /// </para>
    /// <para>
    /// <see cref="IsConstantExpression"/> answers the other half, and answers it by node kind rather
    /// than by value, because the zero is often not written as one -- <c>-0.0</c>, <c>0 as double</c>
    /// and <c>0.0 * 2.0</c> are all divisors the fold reaches and a list of spellings would not.
    /// Knowing the value would need a constant evaluator in the backend, and having one would buy a
    /// narrower rule for a construct nobody writes twice.
    /// </para>
    /// </remarks>
    private static bool IsFoldableFloatingDivision(IrBinary binary)
        => binary.Operator == IrBinaryOperator.Divide
            && binary.ResultType is ScalarType { IsFloatingPoint: true }
            && IsConstantExpression(binary.Left)
            && IsConstantExpression(binary.Right);

    /// <summary>
    /// Whether this expression is emitted as something the C++ front end can evaluate while
    /// compiling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A question about the emitted C++ rather than about the IR, so it follows what the emitter
    /// writes. The node kinds here become literals, operators, casts and enum constants; everything
    /// else becomes a call or a member access, and neither is a constant expression. Integer
    /// arithmetic is the case worth naming: it looks constant and is not, because it routes through
    /// <c>protocross_runtime.h</c>, so <c>(2 - 2) as double</c> is a divisor the front end cannot
    /// work out. Counting it as constant anyway costs an inlined call that was not needed, which is
    /// the direction this predicate is allowed to be wrong in.
    /// </para>
    /// <para>
    /// An enum constant is a constant expression, and so is a cast to or from an enum, so
    /// <c>Level.LEVEL_ZERO as int32 as double</c> and <c>0 as Level as int32 as double</c> are zeros the
    /// front end works out, and a division by either is refused by MSVC as <c>C2124</c> unless it goes
    /// through <c>std::divides</c>. A conversion to an enum that falls back or fails is a call to the
    /// runtime instead, and is counted as constant anyway when its number is, in the direction this is
    /// allowed to be wrong in.
    /// </para>
    /// <para>
    /// A reference to a local is deliberately absent. MSVC does fold through a <c>const</c> local,
    /// so this answer depends on <see cref="EmitStatement"/> declaring locals without <c>const</c> --
    /// which it must anyway, since a ProtoCross local can be assigned.
    /// </para>
    /// </remarks>
    private static bool IsConstantExpression(IrExpression expression) => expression switch
    {
        IrLiteral => true,
        IrUnary unary => IsConstantExpression(unary.Operand),
        IrBinary binary => IsConstantExpression(binary.Left) && IsConstantExpression(binary.Right),
        IrConversion conversion => IsConstantExpression(conversion.Operand),
        IrEnumValue => true,
        IrEnumToNumber number => IsConstantExpression(number.Operand),
        IrNumberToEnum conversion => IsConstantExpression(conversion.Operand),
        _ => false,
    };

    /// <summary>
    /// Whether anything in <paramref name="module"/> is a foldable floating-point division, and so
    /// needs <c>&lt;functional&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Conditional for the same reason <see cref="UsesFloatingRemainder"/> is: a header for a module
    /// that writes no such division stays byte-for-byte what it was before this repair existed.
    /// </remarks>
    private static bool UsesFoldableFloatingDivision(IrModule module)
        => IrWalk.DescendantsAndSelf(module).OfType<IrBinary>().Any(IsFoldableFloatingDivision);

    /// <remarks>
    /// A fallback is handed over as a lambda (<see cref="Deferred"/>), so the helper calls it only for
    /// a zero divisor, and a fallback that can end the program does not end one whose divisor was not
    /// zero (spec 10.2.1).
    /// </remarks>
    private static string EmitIntegerDivision(IrIntegerDivision division, Placement placement)
    {
        if (division.ResultType is not ScalarType scalar)
        {
            throw new ArgumentOutOfRangeException(
                nameof(division), division.ResultType, "Integer division must produce a scalar.");
        }

        var left = Expression(division.Left, placement);
        var right = Expression(division.Right, placement);
        var stem = CppRuntime.Stem(division.Behavior)
            + (division.Operator == IrBinaryOperator.Modulo ? "_mod" : "_div");
        var suffix = HelperSuffix(scalar);

        return division.ZeroBehavior switch
        {
            ZeroDivisorBehavior.Unreachable => $"{RuntimeNamespace}::{stem}_{suffix}({left}, {right})",
            ZeroDivisorBehavior.Fail => $"{RuntimeNamespace}::{stem}_or_fail_{suffix}({left}, {right})",
            ZeroDivisorBehavior.Fallback =>
                $"{RuntimeNamespace}::{stem}_or_{suffix}({left}, {right}, {Deferred(Expression(division.OnZero!, placement))})",
            _ => throw new ArgumentOutOfRangeException(
                nameof(division), division.ZeroBehavior, "Unhandled zero-divisor behavior."),
        };
    }

    private static string EmitUnary(IrUnary unary, Placement placement)
    {
        var operand = Expression(unary.Operand, placement);

        if (unary.Operator == IrUnaryOperator.Negate)
        {
            return unary.OverflowingType is { } scalar
                ? $"{RuntimeNamespace}::{CppRuntime.Stem(unary.Behavior)}_neg_{HelperSuffix(scalar)}({operand})"
                : $"(-{operand})";
        }

        return unary.Operator == IrUnaryOperator.BitwiseNot ? $"(~{operand})" : $"(!{operand})";
    }

    private static string ArithmeticHelperName(IrBinaryOperator op) => op switch
    {
        IrBinaryOperator.Add => "add",
        IrBinaryOperator.Subtract => "sub",
        IrBinaryOperator.Multiply => "mul",
        IrBinaryOperator.Divide => "div",
        IrBinaryOperator.Modulo => "mod",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Not an arithmetic operator."),
    };

    private static string HelperSuffix(ScalarType scalar) => scalar.Kind switch
    {
        ScalarKind.Int32 => "i32",
        ScalarKind.Int64 => "i64",
        ScalarKind.UInt32 => "u32",
        ScalarKind.UInt64 => "u64",
        _ => throw new ArgumentOutOfRangeException(nameof(scalar), scalar.Kind, "Not an integer kind."),
    };

    /// <summary>
    /// Emits an explicit conversion (spec 10.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Integer narrowing needs no helper here, unlike <c>+</c>, <c>-</c>, and <c>*</c>: C++20
    /// defines the conversion to a signed type as two's complement (P0907R4), and the runtime
    /// header already asserts C++20, so <c>static_cast</c> is the explicit statement of the
    /// wrapping rule rather than a reliance on a default. The same goes for widening to a
    /// floating-point type.
    /// </para>
    /// <para>
    /// The two directions that lose range do need helpers, because C++ makes both undefined rather
    /// than merely implementation-defined: converting a <c>double</c> outside <c>float</c>'s range,
    /// and converting a floating-point value outside the target integer's range.
    /// </para>
    /// </remarks>
    private static string EmitConversion(IrConversion conversion, Placement placement)
    {
        if (conversion.Behavior != ConversionBehavior.WrapOrSaturate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(conversion), conversion.Behavior, "Unhandled conversion behavior.");
        }

        var operand = Expression(conversion.Operand, placement);
        var target = TypeName(conversion.TargetType);

        return conversion.Kind switch
        {
            ConversionKind.Identity => operand,
            ConversionKind.IntegerToInteger or ConversionKind.IntegerToFloat =>
                $"static_cast<{target}>({operand})",
            ConversionKind.FloatToFloat => conversion.TargetType.Kind == ScalarKind.Float
                ? $"{RuntimeNamespace}::narrow_f64_to_f32({operand})"
                : $"static_cast<{target}>({operand})",
            ConversionKind.FloatToInteger =>
                $"{RuntimeNamespace}::trunc_sat_f64_to_{HelperSuffix(conversion.TargetType)}"
                + $"(static_cast<double>({operand}))",
            _ => throw new ArgumentOutOfRangeException(
                nameof(conversion), conversion.Kind, "Unhandled conversion kind."),
        };
    }

    /// <summary>Emits an enum value made from a number (spec 12).</summary>
    /// <remarks>
    /// protoc declares every C++ enum with <c>int</c> as its underlying type, so a cast holds any
    /// number, and keeping one needs nothing more. A fallback and a failure ask protoc's
    /// <c>_IsValid</c> whether the schema names the number, passed to the runtime by address. That
    /// answer is the one the generated code would give, rather than a list of numbers this backend
    /// wrote out and could get wrong. A fallback is handed over as a lambda (<see cref="Deferred"/>),
    /// so it is evaluated only for a number the schema does not name (spec 12.1).
    /// </remarks>
    private static string EmitNumberToEnum(IrNumberToEnum conversion, Placement placement)
    {
        var enumType = QualifiedEnumName(conversion.EnumType.Descriptor);
        var value = $"static_cast<{enumType}>({Expression(conversion.Operand, placement)})";
        var isNamed = $"&{enumType}_IsValid";

        return conversion.OnUnnamed switch
        {
            UnnamedNumberBehavior.Keep => value,
            UnnamedNumberBehavior.Fallback =>
                $"{RuntimeNamespace}::named_or({value}, {isNamed}, {Deferred(FallbackOf(conversion, placement))})",
            UnnamedNumberBehavior.Fail =>
                $"{RuntimeNamespace}::named_or_fail({value}, {isNamed}, {FormatString(conversion.EnumType.DisplayName)})",
            _ => throw new ArgumentOutOfRangeException(
                nameof(conversion), conversion.OnUnnamed, "Unhandled unnamed-number behavior."),
        };
    }

    /// <summary>
    /// The value a conversion to an enum falls back to: the one its clause wrote, or the one the
    /// project's configuration names (spec 12.1).
    /// </summary>
    private static string FallbackOf(IrNumberToEnum conversion, Placement placement)
        => conversion.Fallback is { } written
            ? Expression(written, placement)
            : QualifiedEnumValueName(conversion.ConfiguredFallback!);

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
