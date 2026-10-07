using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Binding;

public sealed partial class Binder
{
    /// <summary>Binds a <c>switch</c> and every arm it holds (spec 15.3).</summary>
    /// <remarks>
    /// <para>
    /// Each arm is bound with the facts that held before the switch and no more: the subject is a
    /// number rather than a condition, so choosing an arm proves nothing about any field. What an arm
    /// changes is forgotten after the switch by the block holding it, as it is after any statement.
    /// </para>
    /// <para>
    /// A mistake in one arm's values does not stop the others being bound. The arm is kept with the
    /// values that were wrong in it, since an editor still has to answer questions about them.
    /// </para>
    /// </remarks>
    private IrStatement BindSwitch(SwitchStatement statement, Scope scope, MethodContext context)
    {
        var subject = BindSwitchSubject(statement.Subject, scope, context);
        var listed = new CaseValues(subject.Type);
        var armContext = context with { InsideASwitch = true };

        var arms = statement.Arms
            .Select(arm => new IrSwitchArm(
                [.. arm.Values.Select(value => BindCaseValue(value, listed, scope, context))],
                BindBlock(arm.Body, scope, armContext),
                arm.Span))
            .ToList();

        ReportDefaultsBeforeTheLastArm(statement);
        ReportIfNoArmIsACase(statement);
        return new IrSwitch(subject, arms, statement.Span);
    }

    /// <summary>Reports a switch that lists no case, whose value therefore decides nothing.</summary>
    /// <remarks>
    /// Refused rather than allowed, on the owner's decision. With no arms it does nothing, and with only
    /// a default it is that arm's block written a longer way. Either one is nearly always a switch
    /// still being written, and neither target can write one quietly: C# warns that an empty switch is
    /// empty (CS1522), and MSVC that a switch with a default has no case (C4065). Reported at the
    /// keyword, since what is missing has nowhere else to be pointed at. A switch the parser found
    /// unfinished -- missing a brace, or holding something it stepped over -- is one still being typed,
    /// and has been reported already.
    /// </remarks>
    private void ReportIfNoArmIsACase(SwitchStatement statement)
    {
        var unfinished = !statement.IsClosed || !statement.IsWellFormed;
        if (unfinished || statement.Arms.Any(arm => !arm.IsDefault))
        {
            return;
        }

        _diagnostics.Report(
            DiagnosticCodes.SwitchListsNoCase,
            statement.Arms.Count == 0
                ? "This switch has no arms, so it does nothing whatever the value."
                : "This switch lists no case, so its default arm runs whatever the value.",
            statement.Keyword,
            "Add a 'case' for the values the switch chooses between, or write the statements without "
            + "the switch (spec 15.3).");
    }

    /// <summary>Binds what a switch chooses by, which has to be an integer or an enum, and not a constant.</summary>
    /// <remarks>
    /// A constant is refused on the owner's decision. Such a switch runs one arm every time, and C#
    /// says so: it folds the subject and warns that every other arm, and anything after one that
    /// returns, cannot be reached (CS0162), which a build with warnings as errors refuses.
    /// </remarks>
    private IrExpression BindSwitchSubject(Expression subject, Scope scope, MethodContext context)
    {
        var bound = BindExpression(subject, scope, context, null);

        if (bound.Type is ErrorType)
        {
            return bound;
        }

        if (!CanBeSwitchedOn(bound.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.SubjectCannotBeSwitchedOn,
                $"A switch chooses by an integer or an enum, and this is a '{bound.Type.DisplayName}'.",
                subject.Span,
                "Choose by anything else with 'if' and 'else if' (spec 15.3).");
        }
        else if (IrConstants.IsConstant(bound))
        {
            _diagnostics.Report(
                DiagnosticCodes.SubjectIsAConstant,
                "This switch is on a value known before the method runs, so it runs the same arm "
                + "every time.",
                subject.Span,
                "Switch on a field, a parameter or a local, or write the statements of the arm that runs "
                + "without the switch (spec 15.3).");
        }

        return bound;
    }

    /// <summary>Whether a value of <paramref name="type"/> can be switched on: an integer or an enum.</summary>
    /// <remarks>
    /// Those are the types whose values a case can list as constants, and compare by number alone. A
    /// string would need one of two answers to what makes two strings equal, and a <c>bool</c> has two
    /// values, which an <c>if</c> already chooses between.
    /// </remarks>
    private static bool CanBeSwitchedOn(PlType type) => IsInteger(type) || type is EnumPlType;

    /// <summary>
    /// Binds one value a <c>case</c> lists, which must be a constant of the subject's type that no
    /// earlier case has listed.
    /// </summary>
    /// <remarks>
    /// The value is bound as the subject's type, so an integer literal takes the subject's width and
    /// is checked against its range there, as a literal assigned to a local of that type is. The
    /// checks after that stop at the first that fails, since each assumes the one before it held.
    /// </remarks>
    private IrExpression BindCaseValue(Expression value, CaseValues listed, Scope scope, MethodContext context)
    {
        var expected = CanBeSwitchedOn(listed.SubjectType) ? listed.SubjectType : null;
        var bound = BindExpression(value, scope, context, expected);

        if (bound.Type is ErrorType || expected is null)
        {
            return bound;
        }

        if (bound is not (IrLiteral or IrEnumValue))
        {
            _diagnostics.Report(
                DiagnosticCodes.CaseValueIsNotAConstant,
                "A case lists constants, which this is not.",
                value.Span,
                CaseValueHelp(expected));
            return bound;
        }

        if (!TypesMatch(expected, bound.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.CaseValueTypeMismatch,
                $"This switch is on a '{expected.DisplayName}', and this case value is a '{bound.Type.DisplayName}'.",
                value.Span,
                CaseValueHelp(expected));
            return bound;
        }

        if (listed.TryAdd(bound) is { } earlier)
        {
            _diagnostics.Report(
                DiagnosticCodes.CaseValueListedTwice,
                ListedTwiceMessage(bound, earlier),
                value.Span,
                "A value runs one arm at most. List it once, in the arm that should run for it.");
        }

        return bound;
    }

    /// <summary>What a case on a switch of <paramref name="subjectType"/> can list, for a help line.</summary>
    private static string CaseValueHelp(PlType subjectType) => subjectType is EnumPlType enumType
        ? $"A case lists values of '{enumType.DisplayName}', such as "
            + $"'{enumType.Descriptor.Name}.{ExampleValueName(enumType.Descriptor)}'. A number the enum "
            + "does not name runs the 'default' arm (spec 15.3)."
        : "A case lists integer literals, such as '0' or '-1' (spec 15.3).";

    /// <summary>Says that <paramref name="value"/> was listed already, by <paramref name="earlier"/>.</summary>
    /// <remarks>
    /// Two names for one number are told apart from one name written twice, because only the first
    /// is surprising: an enum may give a number more than one name (<c>allow_alias</c>), and either
    /// name matches the same values.
    /// </remarks>
    private static string ListedTwiceMessage(IrExpression value, IrExpression earlier) => (value, earlier) switch
    {
        (IrEnumValue alias, IrEnumValue named) when alias.Value.Name != named.Value.Name
            => $"'{alias.Value.Name}' is another name for {alias.Value.Number}, which this switch "
                + $"already lists as '{named.Value.Name}'.",
        (IrEnumValue named, _) => $"This switch already lists '{named.Value.Name}'.",
        (IrLiteral literal, _) => $"This switch already lists {literal.Value}.",
        _ => "This switch already lists this value.",
    };

    /// <summary>
    /// Reports every <c>default</c> arm with another arm after it, which is how a second default is
    /// reported too.
    /// </summary>
    /// <remarks>
    /// Nothing falls from one arm into the next, so where the default stands changes nothing it does.
    /// It goes last because that is where a reader looks for it, as for an <c>else</c>, and one rule
    /// then covers the second default as well: the first of two always has an arm after it.
    /// </remarks>
    private void ReportDefaultsBeforeTheLastArm(SwitchStatement statement)
    {
        foreach (var arm in statement.Arms.SkipLast(1).Where(arm => arm.IsDefault))
        {
            _diagnostics.Report(
                DiagnosticCodes.DefaultArmIsNotLast,
                "The 'default' arm has to be the switch's last arm, and its only default.",
                arm.Keyword,
                "Move it after the other arms. A switch has one default arm, which runs for every "
                + "value no case lists (spec 15.3).");
        }
    }

    /// <summary>The numbers one switch has listed so far, and the value that listed each first.</summary>
    /// <remarks>
    /// Keyed by number rather than by how it was written, because two names an enum gives one number
    /// match the same values, and neither target compiles a switch that lists one number twice.
    /// Every value here is a constant of the subject's type by the time it is added, so a number is
    /// always the same kind of key: an <see cref="long"/> for a signed type, a <see cref="ulong"/> for
    /// an unsigned one, and the enum number for an enum.
    /// </remarks>
    private sealed class CaseValues(PlType subjectType)
    {
        private readonly Dictionary<object, IrExpression> _first = [];

        public PlType SubjectType { get; } = subjectType;

        /// <summary>Lists <paramref name="value"/>, or returns the value that listed its number first.</summary>
        public IrExpression? TryAdd(IrExpression value)
        {
            if (NumberOf(value) is not { } number)
            {
                return null;
            }

            if (_first.TryGetValue(number, out var earlier))
            {
                return earlier;
            }

            _first.Add(number, value);
            return null;
        }

        private static object? NumberOf(IrExpression value) => value switch
        {
            IrEnumValue named => named.Value.Number,
            IrLiteral { Value: long or ulong } literal => literal.Value,
            _ => null,
        };
    }
}
