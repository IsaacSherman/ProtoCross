using Google.Protobuf.Reflection;
using ProtoCross.Config;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Binding;

public sealed partial class Binder
{
    // --- an enum's number ---
    //
    // 'status as int32' and 'n as OrderStatus' (spec 12.1). An enum's number is an int32, as
    // protobuf's is, so each direction has exactly one other type. Coming in, a number may be one the
    // enum does not name, and what becomes of it is decided here and stamped on the node. Whether a
    // value is one its enum names, 'status in OrderStatus', is asked here too (spec 12.2).

    /// <summary>Binds <c>status as int32</c>, an enum's number.</summary>
    private IrExpression BindEnumToNumber(CastExpression cast, IrExpression operand, EnumPlType source, PlType target)
    {
        if (target is ScalarType { Kind: ScalarKind.Int32 })
        {
            return new IrEnumToNumber(operand, cast.Span);
        }

        _diagnostics.Report(
            DiagnosticCodes.InvalidConversion,
            $"Cannot convert '{source.DisplayName}' to '{target.DisplayName}'.",
            cast.Span,
            target is ScalarType { IsNumeric: true }
                ? $"An enum converts only to 'int32', the width of its number (spec 12.1). Convert that: 'x as int32 as {target.DisplayName}'."
                : "An enum converts only to 'int32', the width of its number (spec 12.1).");
        return new IrLiteral(null, ErrorType.Instance, cast.Span);
    }

    /// <summary>Binds <c>n as OrderStatus</c>, an enum value made from a number.</summary>
    /// <remarks>
    /// <para>
    /// A literal converted to an enum takes <c>int32</c>, the type of the number it names, so
    /// <c>7 as OrderStatus</c> needs no conversion in between. Spec 10.3 keeps a converted literal
    /// in its natural type so that <c>3000000000 as int32</c> wraps. A conversion to an enum has no
    /// wrapping to keep, and a literal too large for <c>int32</c> names no value any enum can have.
    /// It is rebound only once it has bound on its own, which reports nothing for a literal that
    /// fits <c>int64</c> or <c>uint64</c>. So rebinding cannot report one literal twice.
    /// </para>
    /// <para>
    /// The clause is bound before the operand is checked, so a fallback is resolved and its names are
    /// used even where the operand is wrong. Hover and go-to-definition keep working inside it while
    /// the operand is still being fixed.
    /// </para>
    /// </remarks>
    private IrExpression BindNumberToEnum(
        CastExpression cast,
        IrExpression operand,
        EnumPlType target,
        Scope scope,
        MethodContext context)
    {
        if (IntegerLiteralOf(cast.Operand) is { } written && operand.Type is not ErrorType)
        {
            operand = BindIntegerLiteral(written, cast.Operand.Span, ScalarType.Int32Type);
        }

        var onUnnamed = BindOnUnknown(cast, target, scope, context);

        if (operand.Type is ErrorType)
        {
            return new IrLiteral(null, ErrorType.Instance, cast.Span);
        }

        if (operand.Type is not ScalarType { Kind: ScalarKind.Int32 })
        {
            _diagnostics.Report(
                DiagnosticCodes.InvalidConversion,
                $"Cannot convert '{operand.Type.DisplayName}' to '{target.DisplayName}'.",
                cast.Span,
                operand.Type is ScalarType { IsInteger: true } or EnumPlType
                    ? $"Only an 'int32' converts to an enum, the width of its number (spec 12.1). Convert to that first: 'x as int32 as {cast.TargetType.Name.Text}'."
                    : "Only an 'int32' converts to an enum, the width of its number (spec 12.1).");
            return new IrLiteral(null, ErrorType.Instance, cast.Span);
        }

        if (onUnnamed.Source is UnnamedNumberSource.Default)
        {
            ReportUnstatedOnUnknown(cast, target);
        }

        return new IrNumberToEnum(
            operand,
            target,
            onUnnamed.Behavior,
            onUnnamed.Source,
            onUnnamed.Fallback,
            onUnnamed.ConfiguredFallback,
            cast.Span);
    }

    /// <summary>What a conversion to an enum makes of a number the enum does not name, and who said so.</summary>
    private readonly record struct OnUnnamed(
        UnnamedNumberBehavior Behavior,
        UnnamedNumberSource Source,
        IrExpression? Fallback = null,
        EnumValueDescriptor? ConfiguredFallback = null);

    /// <summary>
    /// What a conversion to <paramref name="target"/> makes of a number it does not name: what the
    /// clause says, or else what the project's configuration says, or else what protobuf does with
    /// one.
    /// </summary>
    /// <remarks>
    /// The default follows protobuf rather than choosing for it. An open enum keeps such a number when
    /// a message is parsed, so the conversion keeps it as well. A closed one refuses it, and a value
    /// protobuf cannot hold is not a value the conversion should invent, so it ends the program.
    /// </remarks>
    private OnUnnamed BindOnUnknown(CastExpression cast, EnumPlType target, Scope scope, MethodContext context)
    {
        switch (cast.OnUnknown)
        {
            case null when _configuredFallbacks.TryGetValue(target.Descriptor, out var configured):
                return configured is { } value
                    ? new(UnnamedNumberBehavior.Fallback, UnnamedNumberSource.Configuration, ConfiguredFallback: value)
                    : new(UnnamedNumberBehavior.Fail, UnnamedNumberSource.Configuration);

            case null:
                return new(
                    target.IsClosed ? UnnamedNumberBehavior.Fail : UnnamedNumberBehavior.Keep,
                    UnnamedNumberSource.Default);

            case { IsFail: true }:
                return new(UnnamedNumberBehavior.Fail, UnnamedNumberSource.Clause);
        }

        var clause = cast.OnUnknown;
        var fallback = BindExpression(clause.Fallback!, scope, context, target);

        if (fallback.Type is not ErrorType && !TypesMatch(target, fallback.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.OnUnknownTypeMismatch,
                $"The fallback has type '{fallback.Type.DisplayName}' but the conversion produces "
                + $"'{target.DisplayName}'.",
                clause.Span,
                $"Write one of its values, such as '{ExampleValueOf(cast, target)}'.");
        }

        return new(UnnamedNumberBehavior.Fallback, UnnamedNumberSource.Clause, fallback);
    }

    /// <summary>
    /// Says what a conversion does with a number its enum does not name where neither the conversion
    /// nor the project says: keeps it, as a note, or ends the program, as a warning.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each is reported because each is easy not to know. An open enum's conversion silently produces
    /// a value equal to none of the names a reader can see. A closed enum's silently becomes a way for
    /// the method to end the program. Neither is wrong, so neither is an error.
    /// </para>
    /// <para>
    /// The closed case is the warning because a closed enum is the one where protobuf has already
    /// decided such a number cannot be held. Code that makes one anyway should say what it wants
    /// instead, and its help names the clause that does.
    /// </para>
    /// </remarks>
    private void ReportUnstatedOnUnknown(CastExpression cast, EnumPlType target)
    {
        var example = ExampleValueOf(cast, target);

        if (target.IsClosed)
        {
            _diagnostics.Report(
                DiagnosticCodes.ClosedEnumConversionHasNoFallback,
                $"'{target.DisplayName}' is closed, and nothing says what becomes of a number it does "
                + "not name, so one ends the program.",
                cast.Span,
                $"Write 'on_unknown fail' to say so, or 'on_unknown {example}' to use a value instead. "
                + $"<Enums> in {ProjectConfig.FileName} says it once for every conversion (spec 12.1).");
            return;
        }

        _diagnostics.Report(
            DiagnosticCodes.UnnamedNumberIsKept,
            $"'{target.DisplayName}' is open, so a number it does not name is kept, as a value equal to "
            + "none of its names.",
            cast.Span,
            $"Write 'on_unknown {example}' to use a value instead, or 'on_unknown fail' to end the program. "
            + $"<Enums> in {ProjectConfig.FileName} says it once for every conversion (spec 12.1).");
    }

    /// <summary>
    /// A value of <paramref name="target"/> for a help line to show, spelled through the name the
    /// conversion wrote its type with, so that pasting it resolves to the same enum.
    /// </summary>
    private static string ExampleValueOf(CastExpression cast, EnumPlType target)
        => $"{cast.TargetType.Name.Text}.{target.Descriptor.Values[0].Name}";

    /// <summary>Binds <c>status in OrderStatus</c>, whether a value is one its enum names (spec 12.2).</summary>
    /// <remarks>
    /// The value has to be of the enum named, rather than of any enum or any number. The test is one
    /// a reader can check against the line it is on: the type in it is the type of the thing asked
    /// about. A number is converted first, <c>n as Level in Level</c>, which says what it is asking.
    /// </remarks>
    private IrExpression BindEnumMembership(EnumMembershipExpression membership, Scope scope, MethodContext context)
    {
        var value = BindExpression(membership.Value, scope, context, null);
        var named = ResolveTypeReference(membership.EnumType);

        if (value.Type is ErrorType || named is ErrorType)
        {
            return new IrLiteral(null, ErrorType.Instance, membership.Span);
        }

        if (named is not EnumPlType enumType)
        {
            _diagnostics.Report(
                DiagnosticCodes.MembershipNeedsAnEnum,
                $"'{named.DisplayName}' is not an enum, so it has no names for 'in' to look among.",
                membership.EnumType.Span,
                "'in' asks whether a value is one its enum names, as in 'status in OrderStatus' (spec 12.2).");
            return new IrLiteral(null, ErrorType.Instance, membership.Span);
        }

        if (!TypesMatch(enumType, value.Type))
        {
            var written = membership.EnumType.Name.Text;

            _diagnostics.Report(
                DiagnosticCodes.MembershipTypeMismatch,
                $"A '{value.Type.DisplayName}' is not a value of '{enumType.DisplayName}', so 'in' cannot look for it among its names.",
                membership.Value.Span,
                value.Type is ScalarType { Kind: ScalarKind.Int32 }
                    ? $"Ask about the value the number makes: 'x as {written} in {written}' (spec 12.2)."
                    : "'in' asks about a value of the enum it names (spec 12.2).");
            return new IrLiteral(null, ErrorType.Instance, membership.Span);
        }

        return new IrEnumMembership(value, enumType, membership.Span);
    }

    /// <summary>
    /// Reports an <c>on_unknown</c> clause on a conversion whose target is not an enum, and binds its
    /// fallback anyway, so a name inside it is still found and still checked.
    /// </summary>
    /// <remarks>
    /// Not reported where the target failed to resolve. That has been reported already, and the type
    /// the author meant may well have been an enum.
    /// </remarks>
    private void ReportStrayOnUnknown(CastExpression cast, PlType target, Scope scope, MethodContext context)
    {
        if (cast.OnUnknown is not { } clause || target is ErrorType)
        {
            return;
        }

        _diagnostics.Report(
            DiagnosticCodes.OnUnknownOutsideEnumConversion,
            $"'on_unknown' says what becomes of a number an enum does not name, and '{target.DisplayName}' "
            + "is not an enum.",
            clause.Span,
            "Delete the clause (spec 12.1).");

        if (clause.Fallback is { } fallback)
        {
            BindExpression(fallback, scope, context, null);
        }
    }

    // --- what the project says about an enum's numbers ---

    /// <summary>
    /// What <c>protocross.config.xml</c> says a conversion to each enum makes of a number the enum
    /// does not name: the value to use, or null for <c>fail</c> (spec 10.4, 12.1).
    /// </summary>
    /// <remarks>
    /// Resolved once, before anything is bound, against every schema the compilation loaded, so each
    /// setting is checked whether or not anything converts to its enum, and a problem with one is
    /// reported once rather than at each conversion.
    /// </remarks>
    private Dictionary<EnumDescriptor, EnumValueDescriptor?> _configuredFallbacks = [];

    /// <summary>Resolves the configuration's <c>&lt;UnknownFallback&gt;</c> settings to enums and values.</summary>
    /// <remarks>
    /// <para>
    /// A setting naming no enum this compilation loaded is passed over in silence. Every source under
    /// a configuration file shares it, and each imports its own schemas, so a setting about an enum
    /// one source never sees is not a mistake in the file. A setting naming a message, an ambiguous
    /// name, a value its enum does not declare, or an enum another setting already named is a
    /// mistake wherever it is read, and is reported at the setting.
    /// </para>
    /// <para>
    /// Simple names resolve as a type position resolves them, among every enum and message, so a
    /// setting means what the same name written in a conversion would.
    /// </para>
    /// </remarks>
    private Dictionary<EnumDescriptor, EnumValueDescriptor?> ResolveConfiguredFallbacks()
    {
        var resolved = new Dictionary<EnumDescriptor, EnumValueDescriptor?>();

        foreach (var setting in _config.EnumFallbacks)
        {
            if (ConfiguredEnum(setting) is not { } descriptor)
            {
                continue;
            }

            if (resolved.ContainsKey(descriptor))
            {
                _diagnostics.Report(
                    DiagnosticCodes.DuplicateConfigurationSetting,
                    $"'Enums/UnknownFallback' is stated more than once for '{descriptor.FullName}'.",
                    setting.TypeSpan,
                    "Two answers to one question is not a configuration, it is a coin toss. Keep one.");
                continue;
            }

            if (setting.IsFail)
            {
                resolved.Add(descriptor, null);
                continue;
            }

            if (descriptor.FindValueByName(setting.Value) is not { } value)
            {
                _diagnostics.Report(
                    DiagnosticCodes.InvalidEnumFallback,
                    $"'{descriptor.FullName}' has no value named '{setting.Value}'.",
                    setting.ValueSpan,
                    $"Write one of its names as the schema spells it, such as {descriptor.Values[0].Name}, "
                    + $"or {EnumUnknownFallback.Fail}.");
                continue;
            }

            resolved.Add(descriptor, value);
        }

        return resolved;
    }

    /// <summary>The enum a setting names, or null where it names none this compilation loaded.</summary>
    private EnumDescriptor? ConfiguredEnum(EnumUnknownFallback setting)
    {
        if (_types.FindEnum(setting.Type) is { } byFullName)
        {
            return byFullName;
        }

        var enums = _types.EnumsNamed(setting.Type);
        var messages = _types.MessagesNamed(setting.Type);

        if (_types.FindMessage(setting.Type) is not null || (enums.Count == 0 && messages.Count > 0))
        {
            _diagnostics.Report(
                DiagnosticCodes.InvalidEnumFallback,
                $"'{setting.Type}' is a message, and only an enum has numbers without names.",
                setting.TypeSpan,
                "Name an enum.");
            return null;
        }

        if (_types.IsAmbiguousAsATypeName(setting.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.InvalidEnumFallback,
                $"'{setting.Type}' matches more than one type: "
                + string.Join(", ", enums.Select(e => e.FullName).Concat(messages.Select(m => m.FullName)).Order(StringComparer.Ordinal))
                + ".",
                setting.TypeSpan,
                "Qualify the name with its protobuf package.");
            return null;
        }

        return enums is [var only] ? only : null;
    }
}
