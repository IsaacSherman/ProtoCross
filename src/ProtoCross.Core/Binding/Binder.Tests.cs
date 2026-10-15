using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Symbols;
using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Binding;

public sealed partial class Binder
{
    private List<IrTest> BindTests(CompilationUnit unit)
    {
        var tests = new List<IrTest>();

        foreach (var test in unit.Tests)
        {
            if (BindTest(test) is { } bound)
            {
                tests.Add(bound);
            }
        }

        return tests;
    }

    /// <summary>A scope holding nothing, for an expression with no locals or parameters to see.</summary>
    /// <remarks>
    /// Every expression inside a <c>test</c> binds against one of these -- a fixture value, an
    /// argument, an expectation -- which together with
    /// <see cref="MethodContext.HasImplicitReceiver"/> being false there is why a bare name
    /// in a test resolves to nothing at all. It reaches nowhere because nothing may be declared in
    /// it: it can contribute nothing to <see cref="IrModule.Scope"/> and hide nothing from a query.
    /// </remarks>
    private static Scope NoNames() => new(null, -1);

    private IrTest? BindTest(TestDeclaration test)
    {
        var signature = ResolveTestTarget(test.Target, test.Span);
        if (signature is null)
        {
            return null;
        }

        var context = new MethodContext(
            signature.Receiver,
            signature.ReturnType,
            HasImplicitReceiver: false);
        var receiver = BindTestFixture(test, signature, context);
        var arguments = BindTestArguments(test, signature, context);
        var expectation = BindTestExpectation(test.Expectation, signature, context);

        return new IrTest(signature, test.Name, receiver, arguments, expectation, test.Span)
        {
            Document = _document,
        };
    }

    /// <summary>The message a test calls its method on, built from its fixture (spec 25.3).</summary>
    /// <remarks>
    /// <para>
    /// A test of a factory's method calls it on nothing. It holds whatever fixture its test does, the
    /// stand-in for one never written or one written anyway, which is refused here; only the binder
    /// knows the target is a factory's.
    /// </para>
    /// <para>
    /// The parser reports a test of an extend method that writes no fixture, except where it read the
    /// target as a factory's and it turned out to be a method of a nested message named <c>factory</c>.
    /// That one is reported here, which is the first place it is known.
    /// </para>
    /// </remarks>
    private IrMessageLiteral BindTestFixture(TestDeclaration test, IrMethodSignature signature, MethodContext context)
    {
        var fixture = test.Receiver;

        if (signature.HasReceiver && !fixture.IsWritten && test.Target.Factory is not null)
        {
            Parser.ReportTestHasNoReceiver(_diagnostics, test.Span);
        }

        if (!signature.HasReceiver && fixture.IsWritten)
        {
            _diagnostics.Report(
                DiagnosticCodes.FactoryTestDeclaresAReceiver,
                $"'{signature.Name}' is a method of the factory of '{signature.Receiver.FullName}', which is called on "
                + "nothing, so its test has no receiver to declare.",
                fixture.Span,
                "Remove the 'receiver { … }' block, and give the method its arguments with 'arg' (spec 25.3).");
        }

        return BindTestReceiver(fixture, signature.Receiver, context);
    }

    /// <remarks>
    /// A target the author has not finished typing yields null in silence; the parser reported it
    /// as a syntax error where it is missing, and saying it again as an invalid test target adds
    /// nothing.
    /// </remarks>
    private IrMethodSignature? ResolveTestTarget(TestTarget target, SourceSpan span)
    {
        // Which half is missing is the whole of what the parser is saying here; see TestTarget. No
        // method means the name was never finished, and that has been reported where it stops.
        if (target.Method.IsMissing)
        {
            return null;
        }

        if (target.Receiver.IsMissing)
        {
            _diagnostics.Report(
                DiagnosticCodes.InvalidTestTarget,
                $"'{target.Method}' is not a method target.",
                span,
                "Write tests against a receiver method, for example 'test Invoice.total_cents'.");
            return null;
        }

        if (target.Factory is { } word && TryResolveFactoryTarget(target, word, span, out var factoryMethod))
        {
            return factoryMethod;
        }

        // A factory word that turned out to be a nested type's name is part of the message's.
        var receiverName = target.Factory is { } nested ? $"{target.Receiver.Text}.{nested.Text}" : target.Receiver.Text;
        var receiverSpan = target.Factory is { } spelled
            ? SourceSpan.Union(span.File, target.Receiver.Span, spelled.Span)
            : target.Receiver.Span;

        var receiver = ResolveMessage(receiverName, span);
        if (receiver is null)
        {
            return null;
        }

        // Recorded as soon as the message is in hand, ahead of the method lookup that may fail. The
        // receiver half is a use of the message the same way an extend header is, and `test
        // InvoiceItem.nope` still named InvoiceItem correctly -- what is wrong there is the method.
        // It is the day #41 can send a reader into the .proto that this matters most, and by then
        // the file will still be one someone is in the middle of fixing.
        Use(SymbolId.ForType(receiver), receiverSpan);

        if (_methods.TryGetValue((receiver.FullName, target.Method.Text), out var signature))
        {
            Use(signature.Id, target.Method.Span);
            if (signature.HasReceiver)
            {
                return signature;
            }

            _diagnostics.Report(
                DiagnosticCodes.UnknownTestTarget,
                $"'{target.Method}' is a method of the factory of '{receiver.FullName}', not of the message.",
                span,
                $"Target it through the factory: 'test {receiver.Name}.{ContextualKeywords.Factory}.{target.Method}' (spec 25.3).");
            return null;
        }

        _diagnostics.Report(
            DiagnosticCodes.UnknownTestTarget,
            $"'{receiver.FullName}' has no ProtoCross method named '{target.Method}'.",
            span,
            "Tests can only target methods declared in an extend block or a factory.");
        return null;
    }

    /// <summary>Resolves <c>Invoice.factory.create</c>, a test's target in a message's factory.</summary>
    /// <param name="signature">The method, or null when the target is refused, which has been reported.</param>
    /// <returns>
    /// False when the target is not a factory's after all: the message has no factory and a nested type
    /// named <c>factory</c>, which is what the name reaches, as it always has.
    /// </returns>
    private bool TryResolveFactoryTarget(TestTarget target, SyntaxName word, SourceSpan span, out IrMethodSignature? signature)
    {
        signature = null;

        var message = ResolveMessage(target.Receiver.Text, span);
        if (message is null)
        {
            return true;
        }

        if (!_factories.TryGetValue(message.FullName, out var factory))
        {
            if (NestedTypeNamedFactory(message) is not null)
            {
                return false;
            }

            Use(SymbolId.ForType(message), target.Receiver.Span);
            _diagnostics.Report(
                DiagnosticCodes.UnknownTestTarget,
                $"'{message.FullName}' has no factory, so it has no method named '{target.Method}' to test.",
                span,
                "Tests can only target methods declared in an extend block or a factory.");
            return true;
        }

        Use(SymbolId.ForType(message), target.Receiver.Span);
        Use(factory.Id, word.Span);

        if (!_methods.TryGetValue((message.FullName, target.Method.Text), out var found) || found.HasReceiver)
        {
            _diagnostics.Report(
                DiagnosticCodes.UnknownTestTarget,
                $"The factory of '{message.FullName}' has no method named '{target.Method}'.",
                span,
                found is not null
                    ? $"'{target.Method}' is a method of '{message.Name}' itself. Target it as '{message.Name}.{target.Method}'."
                    : "Tests can only target methods declared in an extend block or a factory.");
            return true;
        }

        Use(found.Id, target.Method.Span);

        if (found.Kind is IrMethodKind.Helper)
        {
            ReportHelperUsedOutsideItsFactory(found, span, "a test reaches only the methods a factory offers");
            return true;
        }

        signature = found;
        return true;
    }

    private IReadOnlyList<IrTestArgument> BindTestArguments(
        TestDeclaration test,
        IrMethodSignature signature,
        MethodContext context)
    {
        var declared = new Dictionary<string, TestArgumentDeclaration>(StringComparer.Ordinal);

        // An argument whose name is still being typed could be for any parameter, so nothing can be
        // said about which are missing until it is written. That silence is unavoidable and covers
        // the whole signature; a parameter with no name is a narrower problem, handled below.
        var argumentNamesAreComplete = true;

        foreach (var argument in test.Arguments)
        {
            if (argument.Name.IsMissing)
            {
                argumentNamesAreComplete = false;
                continue;
            }

            if (!declared.TryAdd(argument.Name.Text, argument))
            {
                _diagnostics.Report(
                    DiagnosticCodes.DuplicateTestArgument,
                    $"Argument '{argument.Name}' is supplied more than once.",
                    argument.Span);
            }
        }

        var arguments = new List<IrTestArgument>();

        // Which names have already been credited to a parameter. A signature can hold two parameters
        // of one name -- `fn f(a: int64, a: int64)` is PC0026 and is still bound -- and both would
        // otherwise claim the same `arg a` range, so one written name would answer with two symbols
        // and highlighting, go-to-definition and rename would each get a different one. The first
        // wins, which is the parameter BindMethod put in scope and therefore the one the body means.
        var credited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var parameter in signature.Parameters)
        {
            // A parameter nobody has named yet cannot be supplied and cannot be demanded: there is
            // no name for the test to write. Only this parameter goes unmentioned, though -- the
            // ones beside it that do have names are still checked.
            if (parameter.Declaration.Name.IsMissing)
            {
                continue;
            }

            var name = parameter.Name;
            if (!declared.TryGetValue(name, out var declaration))
            {
                if (argumentNamesAreComplete)
                {
                    _diagnostics.Report(
                        DiagnosticCodes.MissingTestArgument,
                        $"Test '{test.Name}' does not supply argument '{name}'.",
                        test.Span);
                }

                continue;
            }

            // An argument names a parameter of the method under test, which is the only place in the
            // language where a parameter is named from outside the method that declares it. Every
            // occurrence and not only `declared`'s, which holds the first of a name supplied twice:
            // the duplicate has been reported, but it is still that parameter's name written in that
            // place, and an index that skipped it would rename around it.
            if (credited.Add(name))
            {
                foreach (var written in test.Arguments)
                {
                    if (!written.Name.IsMissing && written.Name.Text == name)
                    {
                        Use(parameter.Id, written.Name.Span);
                    }
                }
            }

            var expectedType = parameter.Type;
            var value = BindExpression(declaration.Value, NoNames(), context, expectedType);
            if (value.Type is not ErrorType && !TypesMatch(expectedType, value.Type))
            {
                _diagnostics.Report(
                    DiagnosticCodes.TestArgumentTypeMismatch,
                    $"Argument '{name}' expects '{expectedType.DisplayName}' but got '{value.Type.DisplayName}'.",
                    declaration.Span);
            }

            arguments.Add(new IrTestArgument(name, value, declaration.Span));
        }

        foreach (var extra in declared.Keys.Except(NamedParametersOf(signature), StringComparer.Ordinal))
        {
            _diagnostics.Report(
                DiagnosticCodes.UnknownTestArgument,
                $"'{signature.Name}' has no parameter named '{extra}'.",
                declared[extra].Span);
        }

        return arguments;
    }

    /// <summary>The parameters a test could name, which is the ones the author has named.</summary>
    private static IEnumerable<string> NamedParametersOf(IrMethodSignature signature)
        => signature.Parameters
            .Where(parameter => !parameter.Declaration.Name.IsMissing)
            .Select(parameter => parameter.Name);

    private IrTestExpectation BindTestExpectation(
        TestExpectation expectation,
        IrMethodSignature signature,
        MethodContext context)
    {
        switch (expectation)
        {
            case TestFailExpectation fail:
                return new IrTestFailExpectation(fail.Span);

            case TestReturnExpectation returns:
                if (signature.ReturnType is VoidType)
                {
                    _diagnostics.Report(
                        DiagnosticCodes.VoidMethodCannotExpectAReturnValue,
                        $"'{signature.Name}' does not return a value.",
                        returns.Span);
                }

                return BindExpectedValue(returns.Value, returns.Span, Outcome.Result(signature), context);

            case TestRefusalExpectation refusal when signature.Refusal is null:
                _diagnostics.Report(
                    DiagnosticCodes.RefusalExpectedOfAMethodThatCannotRefuse,
                    $"'{signature.Name}' declares no refusal, so a test cannot expect one.",
                    refusal.Span,
                    "Expect what it returns, 'expect return …;', or that it ends the program, 'expect fail;' (spec 25.3).");
                return new IrTestRefusalExpectation(
                    new IrTestReturnExpectation(BindExpression(refusal.Value, NoNames(), context, null), refusal.Span),
                    refusal.Span);

            case TestRefusalExpectation refusal:
                return new IrTestRefusalExpectation(
                    BindExpectedValue(refusal.Value, refusal.Span, Outcome.Refusal(signature), context),
                    refusal.Span);

            default:
                throw new ArgumentOutOfRangeException(nameof(expectation), expectation, "Unhandled expectation.");
        }
    }

    /// <summary>
    /// What a test expects a value of: the result its method returns, or the refusal it refuses with, and the
    /// words its diagnostics use for that.
    /// </summary>
    /// <param name="Verb">How a message says the method gives this: "returns", or "refuses with".</param>
    /// <param name="Keyword">The word after <c>expect</c> that expects it.</param>
    private sealed record Outcome(IrMethodSignature Signature, PlType Type, string Verb, string Keyword)
    {
        public static Outcome Result(IrMethodSignature signature) => new(signature, signature.ReturnType, "returns", "return");

        public static Outcome Refusal(IrMethodSignature signature) => new(signature, signature.Refusal!, "refuses with", "refuse");
    }

    /// <summary>Binds the value an <c>expect return</c> or an <c>expect refuse</c> compares with what the method gives.</summary>
    /// <remarks>
    /// One comparison for both, because a refusal is a value as a result is: a literal of a message says which
    /// of its fields to compare, which is the one comparison two messages have until spec 13.3 decides what
    /// makes them equal, and any other value is compared under <c>==</c>.
    /// </remarks>
    private IrTestExpectation BindExpectedValue(Expression written, SourceSpan span, Outcome outcome, MethodContext context)
    {
        var signature = outcome.Signature;
        if (written is MessageLiteralExpression literal && outcome.Type is MessageType)
        {
            return BindExpectedFields(literal, span, outcome, context);
        }

        // Refused whatever the value is, because what is missing is not a value but a meaning
        // for '=='. The value is still bound, for the names in it, and not checked against the
        // return type: a second diagnostic would be about a comparison that cannot be made.
        var cannotCompare = !HasEquality(outcome.Type);
        if (cannotCompare && outcome.Type is MessageType returned)
        {
            _diagnostics.Report(
                DiagnosticCodes.MessageReturnCannotBeExpected,
                $"'{signature.Name}' {outcome.Verb} message '{returned.DisplayName}', and only a literal of one can be "
                + "expected, because what makes two messages equal is not decided yet.",
                span,
                $"Expect a literal, 'expect {outcome.Keyword} new {returned.Descriptor.Name} {{ … }};', which compares the "
                + "fields it sets (spec 25.3).");
        }
        else if (cannotCompare)
        {
            _diagnostics.Report(
                DiagnosticCodes.MessageReturnCannotBeExpected,
                $"'{signature.Name}' {outcome.Verb} message '{outcome.Type.DisplayName}', and what makes two "
                + "messages equal is not decided yet.",
                span,
                "Until spec 13.3 decides, expect a scalar instead: test a method that returns the field you want to check.");
        }

        var value = BindExpression(written, NoNames(), context, outcome.Type);
        if (outcome.Type is not VoidType
            && !cannotCompare
            && value.Type is not ErrorType
            && !TypesMatch(outcome.Type, value.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.TestExpectationTypeMismatch,
                $"'{signature.Name}' {outcome.Verb} '{outcome.Type.DisplayName}' but the expectation is '{value.Type.DisplayName}'.",
                span);
        }

        return new IrTestReturnExpectation(value, span);
    }

    /// <summary>
    /// Binds <c>expect return new T { … };</c> on a method that returns a message: the fields the literal
    /// sets are what the test compares (spec 25.3).
    /// </summary>
    /// <remarks>
    /// The literal is bound as one anywhere else is, against the message the method returns, so a field it
    /// names that the message lacks is the error it always is. Each field it sets is then asked whether a
    /// test can compare it, by <see cref="IrExpectedFields.CanCompare"/>, which is the rule the backends'
    /// checks are built by, so nothing reaches them unchecked.
    /// </remarks>
    private IrTestExpectation BindExpectedFields(
        MessageLiteralExpression literal,
        SourceSpan span,
        Outcome outcome,
        MethodContext context)
    {
        var value = BindExpression(literal, NoNames(), context, outcome.Type);
        if (value is not IrMessageLiteral expected)
        {
            return new IrTestReturnExpectation(value, span);
        }

        if (!TypesMatch(outcome.Type, expected.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.TestExpectationTypeMismatch,
                $"'{outcome.Signature.Name}' {outcome.Verb} '{outcome.Type.DisplayName}' but the expectation is '{expected.Type.DisplayName}'.",
                span);
        }

        ReportFieldsThatCannotBeCompared(expected);
        return new IrTestFieldsExpectation(expected, span);
    }

    /// <summary>Refuses each field of an expected literal that a test cannot compare (<c>PC0134</c>).</summary>
    private void ReportFieldsThatCannotBeCompared(IrMessageLiteral expected)
    {
        foreach (var initializer in expected.Fields)
        {
            var field = initializer.Field;
            if (IrExpectedFields.CanCompare(field, initializer.Value))
            {
                if (initializer.Value is IrMessageLiteral nested)
                {
                    ReportFieldsThatCannotBeCompared(nested);
                }

                continue;
            }

            var (why, help) = field switch
            {
                { IsMap: true } => (
                    $"'{field.Name}' is a map, and a test cannot compare one yet: the language has no way to list a "
                    + "map's keys (spec 14.2).",
                    "Leave it out of the expected literal, and test a method that returns what you want to check of it."),
                { IsRepeated: true } => (
                    $"'{field.Name}' is a repeated field, and a test cannot compare one yet: the language has no length "
                    + "or index to compare it by (spec 14.1).",
                    "Leave it out of the expected literal, and test a method that returns what you want to check of it."),
                _ => (
                    $"'{field.Name}' is given a '{initializer.Value.Type.DisplayName}' that is not a literal, and only a "
                    + "literal says which of a message's fields to compare (spec 13.3).",
                    "Write it as a literal, 'new T { … }', setting the fields that matter."),
            };

            if (initializer.Value.Type is not ErrorType)
            {
                _diagnostics.Report(DiagnosticCodes.ExpectedFieldCannotBeCompared, why, initializer.Span, help);
            }
        }
    }
}
