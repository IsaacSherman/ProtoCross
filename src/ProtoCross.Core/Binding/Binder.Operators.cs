using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Binding;

public sealed partial class Binder
{
    /// <summary>
    /// Binds an explicit conversion, <c>x as int64</c> (spec 10.3). This is the only way to change
    /// the width or signedness of a value, and the reason mixed-width arithmetic is expressible at
    /// all. A conversion with an enum on either side is an enum's number (spec 12), and is bound in
    /// <c>Binder.Enums.cs</c>.
    /// </summary>
    /// <remarks>
    /// The operand is bound with no expected type. The cast already states the target, so the
    /// operand keeps whatever type it has on its own: an integer literal takes its natural
    /// <c>int64</c>, or <c>uint64</c> where only that holds it, and a float literal its natural
    /// <c>double</c>. That is what makes <c>3000000000 as int32</c> a narrowing conversion that wraps,
    /// rather than a literal that silently retypes itself and then reports PC0036 for not fitting.
    /// </remarks>
    private IrExpression BindCast(CastExpression cast, Scope scope, MethodContext context)
    {
        var operand = BindExpression(cast.Operand, scope, context, null);
        var target = ResolveTypeReference(cast.TargetType);

        if (target is EnumPlType enumType)
        {
            return BindNumberToEnum(cast, operand, enumType, scope, context);
        }

        ReportStrayOnUnknown(cast, target, scope, context);

        // ResolveTypeReference has already reported an unknown or ambiguous target, and a failed
        // operand has already reported whatever went wrong there.
        if (operand.Type is ErrorType || target is ErrorType)
        {
            return new IrLiteral(null, ErrorType.Instance, cast.Span);
        }

        if (operand.Type is EnumPlType enumOperand)
        {
            return BindEnumToNumber(cast, operand, enumOperand, target);
        }

        if (operand.Type is not ScalarType { IsNumeric: true } source
            || target is not ScalarType { IsNumeric: true } destination)
        {
            _diagnostics.Report(
                DiagnosticCodes.InvalidConversion,
                $"Cannot convert '{operand.Type.DisplayName}' to '{target.DisplayName}'.",
                cast.Span,
                "'as' converts between numeric scalar types only (spec 10.3).");
            return new IrLiteral(null, ErrorType.Instance, cast.Span);
        }

        return new IrConversion(
            operand,
            destination,
            ClassifyConversion(source, destination),
            _policy.ResolveConversion(source, destination),
            cast.Span);
    }

    private static ConversionKind ClassifyConversion(ScalarType source, ScalarType destination)
    {
        if (source.Kind == destination.Kind)
        {
            return ConversionKind.Identity;
        }

        if (source.IsInteger)
        {
            return destination.IsInteger ? ConversionKind.IntegerToInteger : ConversionKind.IntegerToFloat;
        }

        return destination.IsInteger ? ConversionKind.FloatToInteger : ConversionKind.FloatToFloat;
    }

    /// <param name="form">
    /// How the operator was written, which is what every diagnostic below names. See
    /// <see cref="OperatorForm"/>.
    /// </param>
    private IrExpression BindBinary(
        BinaryExpression binary,
        Scope scope,
        MethodContext context,
        PlType? expectedType,
        OperatorForm form = OperatorForm.Infix)
    {
        var symbol = Spell(binary.Operator, form);

        if (binary.Operator is BinaryOperatorKind.ShiftLeft or BinaryOperatorKind.ShiftRight)
        {
            return BindShift(binary, symbol, scope, context, expectedType);
        }

        var isComparison = IsComparison(binary.Operator);
        var isLogical = binary.Operator is BinaryOperatorKind.LogicalAnd or BinaryOperatorKind.LogicalOr;
        var isBitwise = binary.Operator
            is BinaryOperatorKind.BitwiseAnd or BinaryOperatorKind.BitwiseOr or BinaryOperatorKind.BitwiseXor;

        // Comparisons and logical operators produce bool, so the outer expectation says nothing
        // about the operands. A bitwise operator produces an integer, so only an integer expectation
        // says anything about its operands.
        var operandHint = isComparison || isLogical ? null
            : isBitwise ? IntegerOrNull(expectedType)
            : expectedType;

        var left = BindExpression(binary.Left, scope, context, operandHint);

        // 'has a and has a.b' has to type-check: 'and' short-circuits, so the right operand only
        // runs where the left one held, and asking about a.b means reading a.
        var rightContext = isLogical
            ? context with
            {
                Present = binary.Operator == BinaryOperatorKind.LogicalAnd
                    ? Union(context.Present, PresenceFacts(left).WhenTrue)
                    : Union(context.Present, PresenceFacts(left).WhenFalse),
            }
            : context;

        var right = BindExpression(
            binary.Right, scope, rightContext, left.Type is ErrorType ? operandHint : left.Type);

        // A literal on the left takes its type from the right operand, the way one on the right takes
        // it from the left (spec 10.3). One that failed to bind at all has already said why, and
        // binding it again would say so twice.
        if (IsNumericLiteral(binary.Left)
            && left.Type is not ErrorType
            && right.Type is ScalarType
            && !TypesMatch(left.Type, right.Type))
        {
            left = BindExpression(binary.Left, scope, context, right.Type);
        }

        if (left.Type is ErrorType || right.Type is ErrorType)
        {
            return new IrBinary(
                ToIrOperator(binary.Operator),
                left,
                right,
                ErrorType.Instance,
                ArithmeticBehavior.Wrap,
                binary.Span);
        }

        var op = ToIrOperator(binary.Operator);

        if (isLogical)
        {
            if (!TypesMatch(left.Type, ScalarType.BoolType) || !TypesMatch(right.Type, ScalarType.BoolType))
            {
                _diagnostics.Report(
                    DiagnosticCodes.LogicalOperatorRequiresBoolOperands,
                    $"Cannot apply '{symbol}' to "
                    + $"'{left.Type.DisplayName}' and '{right.Type.DisplayName}'.",
                    binary.Span);
            }

            return new IrBinary(op, left, right, ScalarType.BoolType, ArithmeticBehavior.Wrap, binary.Span);
        }

        // Asked before the operands are compared with each other, because the likeliest way to reach
        // here is an integer beside a bool, which is a question of precedence and not of conversion.
        if (isBitwise && (!IsInteger(left.Type) || !IsInteger(right.Type)))
        {
            _diagnostics.Report(
                DiagnosticCodes.BitwiseOperatorRequiresIntegerOperands,
                $"Cannot apply '{symbol}' to "
                + $"'{left.Type.DisplayName}' and '{right.Type.DisplayName}'.",
                binary.Span,
                BitwiseHelp(binary, form, left, right));
            return new IrBinary(op, left, right, ErrorType.Instance, ArithmeticBehavior.Wrap, binary.Span);
        }

        if (!TypesMatch(left.Type, right.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.OperandTypeMismatch,
                $"Cannot apply '{symbol}' to "
                + $"'{left.Type.DisplayName}' and '{right.Type.DisplayName}'.",
                binary.Span,
                "ProtoCross does not apply implicit numeric conversions; both operands must "
                + "already have the same type.");
            return new IrBinary(op, left, right, ErrorType.Instance, ArithmeticBehavior.Wrap, binary.Span);
        }

        if (isComparison)
        {
            var ordered = binary.Operator is not (BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual);
            if (ordered && left.Type is not ScalarType { IsNumeric: true })
            {
                _diagnostics.Report(
                    DiagnosticCodes.OperandsAreNotOrdered,
                    $"'{symbol}' requires numeric operands, "
                    + $"but both are '{left.Type.DisplayName}'.",
                    binary.Span);
            }

            if (!ordered && !HasEquality(left.Type))
            {
                ReportUndefinedEquality(binary, symbol, left.Type);
            }

            return new IrBinary(op, left, right, ScalarType.BoolType, ArithmeticBehavior.Wrap, binary.Span);
        }

        // The overflow policy governs none of the bitwise operators (spec 10.1), so each carries the
        // same placeholder behavior a comparison does, and the policy is never asked about one.
        if (isBitwise)
        {
            return new IrBinary(op, left, right, left.Type, ArithmeticBehavior.Wrap, binary.Span);
        }

        if (left.Type is not ScalarType { IsNumeric: true } resultType)
        {
            _diagnostics.Report(
                DiagnosticCodes.ArithmeticOnANonNumericType,
                $"Cannot apply '{symbol}' to '{left.Type.DisplayName}'.",
                binary.Span);
            return new IrBinary(op, left, right, ErrorType.Instance, ArithmeticBehavior.Wrap, binary.Span);
        }

        // Integer division is the only operation that can fail on a value rather than overflow, so
        // it takes a different path. Floating-point division follows IEEE 754 and yields inf or
        // NaN, which needs no declaration.
        if (op is (IrBinaryOperator.Divide or IrBinaryOperator.Modulo) && resultType.IsInteger)
        {
            return BindIntegerDivision(binary, symbol, op, left, right, resultType, scope, context);
        }

        // Only for a division, because the parser has already rejected 'on_zero' on anything that is
        // not one, and reporting it again here would say the same thing twice -- in a message about
        // IEEE 754 that is not even true of the operator the author wrote.
        if (binary.OnZero is not null && op is (IrBinaryOperator.Divide or IrBinaryOperator.Modulo))
        {
            // Named rather than called division, because '%' reaches here too and yields NaN rather
            // than an infinity: a message about division would describe an operator the author did
            // not write and an outcome theirs cannot produce.
            _diagnostics.Report(
                DiagnosticCodes.OnZeroOutsideIntegerDivision,
                $"'{symbol}' on '{resultType.DisplayName}' follows IEEE 754 and "
                + "yields infinity or NaN rather than failing.",
                binary.OnZero.Span);
        }

        return new IrBinary(
            op, left, right, resultType, _policy.ResolveArithmetic(op, resultType), binary.Span);
    }

    /// <summary>
    /// Whether <c>==</c> means anything for two values of this type. It does not for a message or a
    /// repeated value until spec 13.3 says what makes two of them equal, nor so for a map of messages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both places that compare two values ask this one question: an operator, and an
    /// <c>expect return</c> (spec 25.3). When 13.3 decides, they change together, so neither states
    /// the rule for itself.
    /// </para>
    /// <para>
    /// Two maps are equal when they hold the same keys, each with a value equal to the other's, in
    /// whatever order either holds them (spec 14.2). So a map has equality exactly when its values do.
    /// </para>
    /// </remarks>
    private static bool HasEquality(PlType type) => type switch
    {
        MessageType or RepeatedType => false,
        MapType map => HasEquality(map.ValueType),
        _ => true,
    };

    /// <summary>
    /// Reports <c>==</c> or <c>!=</c> on two messages or two repeated values, which compare nothing
    /// until spec 13.3 says what makes two of them equal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The backends had already given it two meanings. C# compares references, because protoc's
    /// classes override <c>Equals</c> but not <c>==</c>, and neither does <c>RepeatedField</c>; so a
    /// message built by a literal equals nothing, an equal one included, while a <c>Timestamp</c>,
    /// which does overload <c>==</c>, compares by value. C++ declares no <c>==</c> for either, and the
    /// comparison does not build.
    /// </para>
    /// <para>
    /// It is refused rather than given a meaning. Equality the compiler wrote field by field would
    /// compare fields the author may not count, an identifier or a timestamp, on a message the author
    /// does not own, so the help points at the comparison only the author can write. Accepting it
    /// later breaks nothing, where taking a meaning back would. The result is still a <c>bool</c>, as
    /// an unordered <c>&lt;</c>'s is, so the expression around it reports nothing further.
    /// </para>
    /// </remarks>
    private void ReportUndefinedEquality(BinaryExpression binary, string symbol, PlType operandType)
    {
        var (values, help) = operandType switch
        {
            MessageType => (
                "messages",
                "Compare the fields that decide it here, or declare a method that compares them and call that (spec 13.3)."),
            MapType => (
                "maps of messages",
                "Compare what matters about the values instead, looking each up by its key (spec 13.3, 14.2)."),
            _ => (
                "repeated values",
                "Compare what matters about the elements instead, such as a count or a total taken in a loop (spec 13.3)."),
        };

        _diagnostics.Report(
            DiagnosticCodes.OperandsHaveNoEquality,
            $"Cannot apply '{symbol}' to two '{operandType.DisplayName}' values: what makes two {values} equal is not defined.",
            binary.Span,
            help);
    }

    /// <summary>
    /// Binds <c>&lt;&lt;</c> or <c>&gt;&gt;</c>, whose operands are not alike: a value, and a count
    /// of how far to shift it (spec 10.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The count may have any integer type, because it is a count and not an operand of the
    /// arithmetic, so neither operand's type is offered to the other. A literal value takes the type
    /// expected of the shift, where that is an integer type, and a literal count takes its natural
    /// type. Handing the count's type to the value would make <c>1 &lt;&lt; bit</c> an
    /// <c>int32</c> wherever <c>bit</c> is one, whatever the shift was meant to produce; handing the
    /// value's type to the count would reject a count literal the value's type cannot hold, although
    /// only its low bits are ever used.
    /// </para>
    /// <para>
    /// The result has the value's type. Like the other bitwise operators it is governed by no
    /// overflow policy: a left shift discards what passes the width whatever the project chose.
    /// </para>
    /// </remarks>
    private IrExpression BindShift(
        BinaryExpression binary,
        string symbol,
        Scope scope,
        MethodContext context,
        PlType? expectedType)
    {
        var op = ToIrOperator(binary.Operator);
        var value = BindExpression(binary.Left, scope, context, IntegerOrNull(expectedType));
        var count = BindExpression(binary.Right, scope, context, null);

        if (value.Type is ErrorType || count.Type is ErrorType)
        {
            return new IrBinary(op, value, count, ErrorType.Instance, ArithmeticBehavior.Wrap, binary.Span);
        }

        if (value.Type is not ScalarType { IsInteger: true } shifted)
        {
            _diagnostics.Report(
                DiagnosticCodes.BitwiseOperatorRequiresIntegerOperands,
                $"'{symbol}' shifts an integer, but the value here is "
                + $"'{value.Type.DisplayName}'.",
                binary.Span);
            return new IrBinary(op, value, count, ErrorType.Instance, ArithmeticBehavior.Wrap, binary.Span);
        }

        if (!IsInteger(count.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.BitwiseOperatorRequiresIntegerOperands,
                $"'{symbol}' shifts by an integer count, but the count here is "
                + $"'{count.Type.DisplayName}'.",
                binary.Span,
                "The count can be any integer type, whatever the value's is. Convert it with 'as'.");
            return new IrBinary(op, value, count, ErrorType.Instance, ArithmeticBehavior.Wrap, binary.Span);
        }

        return new IrBinary(op, value, count, shifted, ArithmeticBehavior.Wrap, binary.Span);
    }

    /// <summary>What to do about a bitwise operator given something other than two integers.</summary>
    /// <remarks>
    /// Two cases have a better answer than the message's. An integer beside a comparison is nearly
    /// always <c>x &amp; mask == 0</c>, which C-family precedence groups as <c>x &amp; (mask == 0)</c>
    /// (spec 9.2), so what is wanted is parentheses. Two bools are an author reaching for C#'s
    /// <c>&amp;</c> and <c>|</c> on bools, which ProtoCross does not have, so what is wanted is the
    /// logical operator -- written out in full after a compound assignment, since no logical operator
    /// has a compound form.
    /// </remarks>
    private static string? BitwiseHelp(
        BinaryExpression binary,
        OperatorForm form,
        IrExpression left,
        IrExpression right)
    {
        var symbol = Spell(binary.Operator, form);

        if (TypesMatch(left.Type, ScalarType.BoolType) && TypesMatch(right.Type, ScalarType.BoolType))
        {
            var logical = LogicalCounterpart(binary.Operator);
            return form == OperatorForm.Compound
                ? $"'{symbol}' works on the bits of integers. For two bools, write the assignment out "
                    + $"with '{logical}': 'a = a {logical} b'."
                : $"'{symbol}' works on the bits of integers. For two bools, write '{logical}'.";
        }

        // The right side of a compound assignment is one operand whatever it holds, so a comparison
        // there is not one precedence put there.
        if (form == OperatorForm.Infix
            && (ComparisonIn(binary.Left) ?? ComparisonIn(binary.Right)) is { } comparison)
        {
            var compared = Describe(comparison);
            return $"'{compared}' binds tighter than '{symbol}', as it does in C# and C++, so the "
                + $"comparison is an operand of '{symbol}'. To compare the result of '{symbol}', "
                + $"parenthesize it: '(a {symbol} b) {compared} c'.";
        }

        return null;
    }

    private static BinaryOperatorKind? ComparisonIn(Expression expression)
        => expression is BinaryExpression { Operator: var op } && IsComparison(op) ? op : null;

    private static string LogicalCounterpart(BinaryOperatorKind kind) => kind switch
    {
        BinaryOperatorKind.BitwiseAnd => "and",
        BinaryOperatorKind.BitwiseOr => "or",
        BinaryOperatorKind.BitwiseXor => "!=",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a bitwise operator."),
    };

    private static bool IsComparison(BinaryOperatorKind kind)
        => kind is BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual
            or BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual
            or BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual;

    private static bool IsInteger(PlType? type) => type is ScalarType { IsInteger: true };

    /// <summary>An expectation, kept only where it is an integer type.</summary>
    /// <remarks>
    /// For an operator that makes an integer from integers, a <c>double</c> expected of the result
    /// says nothing its operands can use: a literal handed it would become a <c>double</c> and be
    /// refused by the operator, where left alone it stays an integer and the mismatch is reported
    /// where it really is, between the result and what was expected of it.
    /// </remarks>
    private static PlType? IntegerOrNull(PlType? expectedType)
        => IsInteger(expectedType) ? expectedType : null;

    /// <summary>
    /// Binds integer <c>/</c> or <c>%</c>. The divisor must either be a literal that is provably
    /// non-zero, or be accompanied by an <c>on_zero</c> clause naming the result to use instead.
    /// There is no third option: leaving it to the target would mean an exception in C#, a crash on
    /// x86 in C++, and a silent zero on ARM, all from one source file.
    /// </summary>
    private IrExpression BindIntegerDivision(
        BinaryExpression binary,
        string symbol,
        IrBinaryOperator op,
        IrExpression left,
        IrExpression right,
        ScalarType resultType,
        Scope scope,
        MethodContext context)
    {
        var divisorIsProvenNonZero = right is IrLiteral { Value: (long and not 0L) or (ulong and not 0UL) };

        if (divisorIsProvenNonZero)
        {
            if (binary.OnZero is not null)
            {
                _diagnostics.Report(
                    DiagnosticCodes.UnnecessaryOnZeroClause,
                    "The divisor is a non-zero literal, so this clause is unreachable.",
                    binary.OnZero.Span);
            }

            return new IrIntegerDivision(
                op, left, right, ZeroDivisorBehavior.Unreachable, null, resultType,
                _policy.ResolveDivision(op, resultType), binary.Span);
        }

        if (binary.OnZero is null)
        {
            _diagnostics.Report(
                DiagnosticCodes.MissingOnZeroClause,
                $"'{symbol}' on '{resultType.DisplayName}' must state what to "
                + "produce when the divisor is zero.",
                binary.Span,
                $"Write '{symbol} <divisor> on_zero <fallback>', or "
                + $"'{symbol} <divisor> on_zero fail' if no value is correct.");

            return new IrIntegerDivision(
                op, left, right, ZeroDivisorBehavior.Fallback, null, ErrorType.Instance,
                ArithmeticBehavior.Wrap, binary.Span);
        }

        if (binary.OnZero.IsFail)
        {
            return new IrIntegerDivision(
                op, left, right, ZeroDivisorBehavior.Fail, null, resultType,
                _policy.ResolveDivision(op, resultType), binary.Span);
        }

        var onZero = BindExpression(binary.OnZero.Fallback!, scope, context, resultType);

        if (onZero.Type is not ErrorType && !TypesMatch(resultType, onZero.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.OnZeroTypeMismatch,
                $"The fallback has type '{onZero.Type.DisplayName}' but the division produces "
                + $"'{resultType.DisplayName}'.",
                binary.OnZero.Span,
                "ProtoCross does not apply implicit numeric conversions.");
        }

        return new IrIntegerDivision(
            op, left, right, ZeroDivisorBehavior.Fallback, onZero, resultType,
            _policy.ResolveDivision(op, resultType), binary.Span);
    }

    private IrExpression BindUnary(
        UnaryExpression unary,
        Scope scope,
        MethodContext context,
        PlType? expectedType)
    {
        var operand = BindExpression(
            unary.Operand,
            scope,
            context,
            unary.Operator == UnaryOperatorKind.BitwiseNot ? IntegerOrNull(expectedType) : expectedType);

        if (operand.Type is ErrorType)
        {
            return new IrUnary(
                ToIrOperator(unary.Operator), operand, ErrorType.Instance, ArithmeticBehavior.Wrap, unary.Span);
        }

        if (unary.Operator == UnaryOperatorKind.BitwiseNot)
        {
            return BindComplement(unary, operand);
        }

        if (unary.Operator == UnaryOperatorKind.Negate)
        {
            if (operand.Type is not ScalarType { IsNumeric: true } scalar)
            {
                _diagnostics.Report(
                    DiagnosticCodes.NegationRequiresANumericOperand,
                    $"Cannot negate a value of type '{operand.Type.DisplayName}'.",
                    unary.Span);
                return new IrUnary(
                    IrUnaryOperator.Negate, operand, ErrorType.Instance, ArithmeticBehavior.Wrap, unary.Span);
            }

            if (scalar.IsInteger && !scalar.IsSigned)
            {
                _diagnostics.Report(
                    DiagnosticCodes.NegationOfAnUnsignedType,
                    $"'{scalar.DisplayName}' is unsigned and cannot be negated.",
                    unary.Span);
            }

            return new IrUnary(
                IrUnaryOperator.Negate, operand, scalar, _policy.ResolveNegation(scalar), unary.Span);
        }

        if (!TypesMatch(operand.Type, ScalarType.BoolType))
        {
            _diagnostics.Report(
                DiagnosticCodes.LogicalNotRequiresABoolOperand,
                $"Cannot apply 'not' to a value of type '{operand.Type.DisplayName}'.",
                unary.Span);
        }

        return new IrUnary(
            IrUnaryOperator.LogicalNot, operand, ScalarType.BoolType, ArithmeticBehavior.Wrap, unary.Span);
    }

    /// <summary>Binds <c>~</c>, which flips every bit of an integer and is governed by no policy.</summary>
    private IrUnary BindComplement(UnaryExpression unary, IrExpression operand)
    {
        if (operand.Type is not ScalarType { IsInteger: true } scalar)
        {
            _diagnostics.Report(
                DiagnosticCodes.BitwiseNotRequiresAnIntegerOperand,
                $"Cannot apply '~' to a value of type '{operand.Type.DisplayName}'.",
                unary.Span,
                TypesMatch(operand.Type, ScalarType.BoolType)
                    ? "'~' flips the bits of an integer. For a bool, write 'not'."
                    : null);
            return new IrUnary(
                IrUnaryOperator.BitwiseNot, operand, ErrorType.Instance, ArithmeticBehavior.Wrap, unary.Span);
        }

        return new IrUnary(IrUnaryOperator.BitwiseNot, operand, scalar, ArithmeticBehavior.Wrap, unary.Span);
    }

    private static IrUnaryOperator ToIrOperator(UnaryOperatorKind kind) => kind switch
    {
        UnaryOperatorKind.Negate => IrUnaryOperator.Negate,
        UnaryOperatorKind.LogicalNot => IrUnaryOperator.LogicalNot,
        UnaryOperatorKind.BitwiseNot => IrUnaryOperator.BitwiseNot,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled operator."),
    };

    private static IrBinaryOperator ToIrOperator(BinaryOperatorKind kind) => kind switch
    {
        BinaryOperatorKind.Add => IrBinaryOperator.Add,
        BinaryOperatorKind.Subtract => IrBinaryOperator.Subtract,
        BinaryOperatorKind.Multiply => IrBinaryOperator.Multiply,
        BinaryOperatorKind.Divide => IrBinaryOperator.Divide,
        BinaryOperatorKind.Modulo => IrBinaryOperator.Modulo,
        BinaryOperatorKind.Equal => IrBinaryOperator.Equal,
        BinaryOperatorKind.NotEqual => IrBinaryOperator.NotEqual,
        BinaryOperatorKind.LessThan => IrBinaryOperator.LessThan,
        BinaryOperatorKind.LessThanOrEqual => IrBinaryOperator.LessThanOrEqual,
        BinaryOperatorKind.GreaterThan => IrBinaryOperator.GreaterThan,
        BinaryOperatorKind.GreaterThanOrEqual => IrBinaryOperator.GreaterThanOrEqual,
        BinaryOperatorKind.LogicalAnd => IrBinaryOperator.LogicalAnd,
        BinaryOperatorKind.LogicalOr => IrBinaryOperator.LogicalOr,
        BinaryOperatorKind.BitwiseAnd => IrBinaryOperator.BitwiseAnd,
        BinaryOperatorKind.BitwiseOr => IrBinaryOperator.BitwiseOr,
        BinaryOperatorKind.BitwiseXor => IrBinaryOperator.BitwiseXor,
        BinaryOperatorKind.ShiftLeft => IrBinaryOperator.ShiftLeft,
        BinaryOperatorKind.ShiftRight => IrBinaryOperator.ShiftRight,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled operator."),
    };

    private static string Describe(BinaryOperatorKind kind) => kind switch
    {
        BinaryOperatorKind.Add => "+",
        BinaryOperatorKind.Subtract => "-",
        BinaryOperatorKind.Multiply => "*",
        BinaryOperatorKind.Divide => "/",
        BinaryOperatorKind.Modulo => "%",
        BinaryOperatorKind.Equal => "==",
        BinaryOperatorKind.NotEqual => "!=",
        BinaryOperatorKind.LessThan => "<",
        BinaryOperatorKind.LessThanOrEqual => "<=",
        BinaryOperatorKind.GreaterThan => ">",
        BinaryOperatorKind.GreaterThanOrEqual => ">=",
        BinaryOperatorKind.LogicalAnd => "and",
        BinaryOperatorKind.LogicalOr => "or",
        BinaryOperatorKind.BitwiseAnd => "&",
        BinaryOperatorKind.BitwiseOr => "|",
        BinaryOperatorKind.BitwiseXor => "^",
        BinaryOperatorKind.ShiftLeft => "<<",
        BinaryOperatorKind.ShiftRight => ">>",
        _ => kind.ToString(),
    };

    /// <summary>A binary operator as the author wrote it: a compound assignment's is followed by '='.</summary>
    private static string Spell(BinaryOperatorKind kind, OperatorForm form)
        => form == OperatorForm.Compound ? $"{Describe(kind)}=" : Describe(kind);

    /// <summary>How a binary operator was written, which the operation it binds to does not say.</summary>
    /// <remarks>
    /// A compound assignment is bound as the operation it abbreviates, so the binder holds the same
    /// syntax whichever the author wrote, and this is what tells the two apart. A diagnostic names the
    /// operator as it was written -- <c>+=</c> where that is what was typed -- and precedence is blamed
    /// only where precedence did the grouping, which it never does for the right side of a compound
    /// assignment.
    /// </remarks>
    private enum OperatorForm
    {
        Infix,
        Compound,
    }
}
