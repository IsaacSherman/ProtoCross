using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Syntax;
using ProtoCross.Types;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// How a factory refuses (spec 16.3.1): a method that declares what it refuses with, <c>refuse</c>, which
/// ends it with a refusal, and the <c>on_refused</c> clause every call to one writes, with the shape each
/// backend generates for them (spec 24).
/// </summary>
/// <remarks>
/// What a refusal does at run time is pinned in the conformance corpus, <c>refusals.pcross</c>, which both
/// backends compile and run. These cover what the corpus cannot: every refusal of a program, and the
/// shape of what is generated.
/// </remarks>
public class RefusalTests
{
    private const string Schema =
        """
        syntax = "proto3";

        package acme.billing;

        import "google/protobuf/wrappers.proto";

        message TicketOptions {
          string code = 1;
          int64 seats = 2;
          Holder holder = 3;
        }

        message Ticket {
          string code = 1;
          int64 seats = 2;
          google.protobuf.Int64Value spare = 3;
        }

        message Holder {
          string name = 1;
          int64 refuses = 2;
        }

        message Refusal {
          string why = 1;
        }

        enum Reason {
          REASON_NONE = 0;
          REASON_NO_SEATS = 1;
        }
        """;

    /// <summary>A factory that may refuse, through a helper that returns nothing and one that returns a value.</summary>
    private const string Refusing =
        """
        factory Ticket {
            fn create(options: TicketOptions) -> Ticket refuses Refusal {
                require_seats(options) on_refused refuse;
                var code: string = checked_code(options) on_refused refuse;
                return new Ticket { code: code, seats: options.seats };
            }

            helper fn require_seats(options: TicketOptions) refuses Refusal {
                if options.seats <= 0 {
                    refuse new Refusal { why: "no seats" };
                }
            }

            helper fn checked_code(options: TicketOptions) -> string refuses Refusal {
                if options.code == "" {
                    refuse new Refusal { why: "no code" };
                }

                return options.code;
            }
        }
        """;

    private static CompilationResult Compile(string body)
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "tickets.proto"), Schema);
        var path = Path.Combine(directory, "tickets.pcross");
        File.WriteAllText(path, "import proto \"tickets.proto\";\n\n" + body);

        return new Compilation(SourceDocument.ReadFrom(path), new CompilationOptions { IncludePaths = [directory] }).Compile();
    }

    private static void AssertOk(CompilationResult result)
        => Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

    private static Diagnostic TheOnly(CompilationResult result, DiagnosticDescriptor expected)
    {
        var only = Assert.Single(result.Diagnostics);
        Assert.True(only.Code == expected.Code, $"expected {expected.Code}, got {only}");
        return only;
    }

    private static string Generated(CompilationResult result, ITestBackend backend, bool tests = false)
    {
        AssertOk(result);
        var diagnostics = new DiagnosticBag();
        var files = tests ? SourceEmission.EmitTests(result, backend, diagnostics) : SourceEmission.Emit(result, backend, diagnostics);
        Assert.Empty(diagnostics);

        return string.Join(
            "\n",
            files.Where(file => !NameConventions.FixedNames.Contains(Path.GetFileName(file.RelativePath).Split('.')[0]))
                .Select(file => file.Contents));
    }

    private static CompilationUnit Parse(string text, DiagnosticBag diagnostics)
        => new Parser(new Lexer(text, "f.pcross", diagnostics).Tokenize(), "f.pcross", diagnostics).ParseCompilationUnit();

    /// <summary>A factory of <c>Ticket</c> holding <paramref name="methods"/>, and <see cref="Refusing"/>'s beside it in one of its own.</summary>
    private static string WithCreate(string methods)
        => Refusing.Replace("factory Ticket {", "factory Ticket {\n" + methods, StringComparison.Ordinal);

    // ------- the syntax

    [Fact]
    public void AMethodDeclaresWhatItRefusesWithAfterItsReturnType()
    {
        var diagnostics = new DiagnosticBag();
        var factory = Assert.Single(Parse(Refusing, diagnostics).Factories);

        Assert.Empty(diagnostics);
        Assert.Equal(["Refusal", "Refusal", "Refusal"], factory.Methods.Select(method => method.Refusal?.Name.Text));
        Assert.Null(factory.Methods[1].ReturnType);
    }

    [Fact]
    public void RefuseAndOnRefusedAreReservedAndRefusesIsNot()
    {
        Assert.Equal(TokenKind.Refuse, Lexer.Keywords["refuse"]);
        Assert.Equal(TokenKind.OnRefused, Lexer.Keywords["on_refused"]);
        Assert.False(Lexer.Keywords.ContainsKey(ContextualKeywords.Refuses));
    }

    /// <summary>A field, a local and a parameter named <c>refuses</c> keep the name, since no type follows it there.</summary>
    [Fact]
    public void RefusesIsANameWhereNoTypeFollowsIt()
        => AssertOk(Compile(
            """
            extend Holder {
                fn counted(refuses: int64) -> int64 {
                    var total: int64 = refuses;
                    return total;
                }

                fn field() -> int64 {
                    return refuses;
                }
            }
            """));

    [Theory]
    [InlineData("Refusal", true)]
    [InlineData("string", true)]
    [InlineData("{", false)]
    [InlineData("=", false)]
    public void RefusesIsAKeywordWhereATypeFollowsIt(string next, bool keyword)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer($"refuses {next}", "f.pcross", diagnostics).Tokenize();

        Assert.Equal(keyword, ContextualKeywords.BeginsARefusal(tokens[0], tokens[1]));
        Assert.Equal(keyword, ContextualKeywords.IsAKeywordHere(tokens[0], tokens[1]));
    }

    [Fact]
    public void AnOnRefusedClauseBelongsToTheCallAndEndsItsChain()
    {
        var diagnostics = new DiagnosticBag();
        var unit = Parse(
            """
            extend Holder {
                fn f() -> int64 {
                    return (Ticket.factory.create(o) on_refused fail).seats + Ticket.factory.make(o) on_refused blank().seats;
                }
            }
            """,
            diagnostics);

        Assert.Empty(diagnostics);
        var handled = SyntaxWalk.DescendantsAndSelf(unit).OfType<OnRefusedExpression>().ToList();
        Assert.Equal(2, handled.Count);
        Assert.True(handled[0].Clause.IsFail);

        // A fallback is a unary expression, so the member after it is the fallback's, not the call's.
        Assert.IsType<MemberAccessExpression>(handled[1].Clause.Fallback);
    }

    [Theory]
    [InlineData("on_refused fail", true, false)]
    [InlineData("on_refused refuse", false, true)]
    [InlineData("on_refused blank()", false, false)]
    public void AClauseIsAFallbackFailOrRefuse(string clause, bool fails, bool passesOn)
    {
        var diagnostics = new DiagnosticBag();
        var unit = Parse($"factory Ticket {{\n    fn f() -> Ticket refuses Refusal {{\n        return create() {clause};\n    }}\n}}\n", diagnostics);

        Assert.Empty(diagnostics);
        var handled = Assert.Single(SyntaxWalk.DescendantsAndSelf(unit).OfType<OnRefusedExpression>());
        Assert.Equal(fails, handled.Clause.IsFail);
        Assert.Equal(passesOn, handled.Clause.PassesItOn);
        Assert.Equal(!fails && !passesOn, handled.Clause.Fallback is not null);
    }

    [Fact]
    public void ATestExpectsARefusalWithExpectRefuse()
    {
        var diagnostics = new DiagnosticBag();
        var unit = Parse("test Ticket.factory.create \"t\" {\n    expect refuse \"no\";\n}\n", diagnostics);

        Assert.Empty(diagnostics);
        var expectation = Assert.IsType<TestRefusalExpectation>(Assert.Single(unit.Tests).Expectation);
        Assert.IsType<StringLiteralExpression>(expectation.Value);
    }

    [Fact]
    public void AnUnknownExpectationNamesRefuseAmongTheExpectations()
    {
        var diagnostics = new DiagnosticBag();
        Parse("test Ticket.factory.create \"t\" {\n    expect 5;\n}\n", diagnostics);

        var reported = Assert.Single(diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.ExpectedTestExpectation.Code);
        Assert.Equal("Expected 'return', 'refuse' or 'fail' but found integer literal.", reported.Message);
    }

    // ------- declaring and refusing

    [Fact]
    public void AFactoryThatRefusesCompiles()
    {
        var result = Compile(Refusing);

        AssertOk(result);
        var create = result.Module!.Methods.Single(method => method.Name == "create");
        Assert.Equal("acme.billing.Refusal", create.Signature.Refusal!.DisplayName);
        Assert.Equal("fn create(options: acme.billing.TicketOptions) -> acme.billing.Ticket refuses acme.billing.Refusal", create.Signature.DisplayName);

        var calls = IrWalk.DescendantsAndSelf(create).OfType<IrRefusableCall>().ToList();
        Assert.Equal([RefusalBehavior.Refuse, RefusalBehavior.Refuse], calls.Select(call => call.OnRefused));
        Assert.All(calls, call => Assert.True(call.Call.Target.CanRefuse));
    }

    [Fact]
    public void AMethodOfAnExtendBlockCannotRefuse()
    {
        var diagnostic = TheOnly(
            Compile("extend Ticket {\n    fn f() -> int64 refuses Refusal {\n        refuse new Refusal {};\n    }\n}\n"),
            DiagnosticCodes.RefusalOutsideAFactory);

        Assert.Equal("'f' is a method of an extend block, and only a factory's methods may refuse.", diagnostic.Message);
    }

    [Fact]
    public void ARefusalOfVoidIsRefused()
        => TheOnly(
            Compile("factory Ticket {\n    fn f() -> Ticket refuses void {\n        return new Ticket {};\n    }\n}\n"),
            DiagnosticCodes.VoidIsNotAValueType);

    [Theory]
    [InlineData("factory Ticket {\n    fn f() -> Ticket {\n        refuse \"no\";\n    }\n}\n", "'f' declares no refusal, so it cannot refuse.")]
    [InlineData("extend Ticket {\n    fn f() -> int64 {\n        refuse \"no\";\n    }\n}\n", "'f' is a method of an extend block, and only a factory's methods may refuse.")]
    public void RefuseInAMethodThatDeclaresNoRefusalIsRefused(string body, string message)
        => Assert.Equal(message, TheOnly(Compile(body), DiagnosticCodes.RefuseInAMethodThatCannotRefuse).Message);

    [Fact]
    public void ARefusalOfAnotherTypeIsRefused()
        => TheOnly(
            Compile("factory Ticket {\n    fn f() -> Ticket refuses Refusal {\n        refuse \"no\";\n    }\n}\n"),
            DiagnosticCodes.RefusalTypeMismatch);

    /// <summary>A path that refuses needs no return, and a guard that refuses leaves what it tested shown to be set.</summary>
    [Fact]
    public void ARefusalEndsItsPath()
        => AssertOk(Compile(
            """
            factory Ticket {
                fn held(options: TicketOptions) -> Ticket refuses Refusal {
                    if not has options.holder {
                        refuse new Refusal { why: "no holder" };
                    }

                    return new Ticket { code: options.holder.name };
                }

                fn never(options: TicketOptions) -> Ticket refuses Refusal {
                    refuse new Refusal {};
                }
            }
            """));

    [Fact]
    public void ARefusalTypeMayBeAnEnum()
        => AssertOk(Compile("factory Ticket {\n    fn f(n: int64) -> Ticket refuses Reason {\n        if n < 0 {\n            refuse Reason.REASON_NO_SEATS;\n        }\n\n        return new Ticket { seats: n };\n    }\n}\n"));

    // ------- answering

    [Fact]
    public void ACallToAMethodThatMayRefuseSaysWhatARefusalMakesOfIt()
    {
        var diagnostic = TheOnly(
            Compile(Refusing + "\nextend Holder {\n    fn f(o: TicketOptions) -> int64 {\n        return Ticket.factory.create(o).seats;\n    }\n}\n"),
            DiagnosticCodes.RefusalNotAnswered);

        Assert.Equal("'create' may refuse with 'acme.billing.Refusal', so a call to it has to say what a refusal makes of it.", diagnostic.Message);
        Assert.Equal(
            "Write 'on_refused' and a 'acme.billing.Ticket' after the call, or 'on_refused fail' to end the program where it refuses (spec 16.3).",
            diagnostic.Help);
    }

    /// <summary>Where the caller refuses with the callee's type, passing the refusal on is one of the ways offered.</summary>
    [Fact]
    public void TheHelpOffersToPassARefusalOnWhereTheCallerCan()
    {
        var diagnostic = TheOnly(
            Compile(WithCreate("    fn again(o: TicketOptions) -> Ticket refuses Refusal {\n        return create(o);\n    }\n")),
            DiagnosticCodes.RefusalNotAnswered);

        Assert.Equal(
            "Write 'on_refused' and a 'acme.billing.Ticket' after the call, 'on_refused fail' to end the program where it refuses, "
            + "or 'on_refused refuse' to refuse with it in turn (spec 16.3).",
            diagnostic.Help);
    }

    [Fact]
    public void AClauseOnACallThatCannotRefuseIsWarnedAboutAndChangesNothing()
    {
        var result = Compile(
            "factory Ticket {\n    fn plain() -> Ticket {\n        return new Ticket {};\n    }\n}\n\n"
            + "extend Holder {\n    fn f() -> int64 {\n        return (Ticket.factory.plain() on_refused fail).seats;\n    }\n}\n");

        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnnecessaryOnRefusedClause.Code, warning.Code);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("'plain' cannot refuse, so this clause cannot run.", warning.Message);
        Assert.Contains("global::Acme.Billing.TicketFactory.Plain().Seats", Generated(result, new CSharpBackend()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("return create(o) on_refused 5;", "The fallback is a 'int64', but 'create' returns 'acme.billing.Ticket'.")]
    [InlineData("require_seats(o) on_refused new Ticket {};\n        return new Ticket {};", "'require_seats' returns nothing, so there is no value for a refusal to fall back to.")]
    public void AFallbackMustBeWhatTheCallGives(string statement, string message)
        => Assert.Equal(
            message,
            TheOnly(
                Compile(WithCreate($"    fn f(o: TicketOptions) -> Ticket {{\n        {statement}\n    }}\n")),
                DiagnosticCodes.OnRefusedTypeMismatch).Message);

    [Theory]
    [InlineData(
        "factory Ticket {\n    fn f(o: TicketOptions) -> Ticket {\n        return create(o) on_refused refuse;\n    }\n",
        "'f' declares no refusal, so it cannot pass on a refusal from 'create'.")]
    [InlineData(
        "factory Ticket {\n    fn f(o: TicketOptions) -> Ticket refuses string {\n        return create(o) on_refused refuse;\n    }\n",
        "'create' refuses with 'acme.billing.Refusal', and 'f' refuses with 'string'.")]
    public void ARefusalIsPassedOnOnlyAsTheSameType(string opening, string message)
        => Assert.Equal(
            message,
            TheOnly(Compile(Refusing.Replace("factory Ticket {\n", opening, StringComparison.Ordinal)), DiagnosticCodes.RefusalCannotBePassedOn).Message);

    [Fact]
    public void AMethodOfAnExtendBlockCannotPassARefusalOn()
        => Assert.Equal(
            "'f' cannot refuse, so it cannot pass on a refusal from 'create': only a factory's methods may refuse.",
            TheOnly(
                Compile(Refusing + "\nextend Holder {\n    fn f(o: TicketOptions) -> Ticket {\n        return Ticket.factory.create(o) on_refused refuse;\n    }\n}\n"),
                DiagnosticCodes.RefusalCannotBePassedOn).Message);

    [Theory]
    [InlineData("create(o) on_refused refuse;\n        return new Ticket {};")]
    [InlineData("var t: Ticket = create(o) on_refused refuse;\n        return t;")]
    [InlineData("var t: Ticket = new Ticket {};\n        t = create(o) on_refused refuse;\n        return t;")]
    [InlineData("return create(o) on_refused refuse;")]
    public void ACallThatStandsOnItsOwnMayPassItsRefusalOn(string statements)
        => AssertOk(Compile(WithCreate($"    fn f(o: TicketOptions) -> Ticket refuses Refusal {{\n        {statements}\n    }}\n")));

    [Theory]
    [InlineData("return new Ticket { seats: (create(o) on_refused refuse).seats };")]
    [InlineData("if (create(o) on_refused refuse).seats > 0 {\n            return new Ticket {};\n        }\n\n        return new Ticket {};")]
    public void ARefusalIsNotPassedOnFromInsideAnExpression(string statements)
        => TheOnly(
            Compile(WithCreate($"    fn f(o: TicketOptions) -> Ticket refuses Refusal {{\n        {statements}\n    }}\n")),
            DiagnosticCodes.RefusalPassedOnInsideAnExpression);

    [Theory]
    [InlineData("reason() on_refused refuse")]
    [InlineData("1 + (reason() on_refused refuse)")]
    public void ARefusalCannotBePassedOnFromARefuseValue(string value)
        => TheOnly(
            Compile($$"""
                factory Ticket {
                    fn create() -> Ticket refuses int64 {
                        refuse {{value}};
                    }

                    helper fn reason() -> int64 refuses int64 {
                        refuse 7;
                    }
                }
                """),
            DiagnosticCodes.RefusalPassedOnInsideAnExpression);

    [Theory]
    [InlineData("holder.rename()")]
    [InlineData("1 + holder.rename()")]
    public void AMutatingCallCannotBeNestedInARefuseValue(string value)
        => TheOnly(
            Compile($$"""
                extend Holder {
                    mut fn rename() -> int64 {
                        name = "changed";
                        return 7;
                    }
                }

                factory Ticket {
                    fn create() -> Ticket refuses int64 {
                        var holder: Holder = new Holder {};
                        refuse {{value}};
                    }
                }
                """),
            DiagnosticCodes.MutatingCallInsideAnExpression);

    /// <summary>A call that has no value is told so once, and not also asked what a refusal makes of it.</summary>
    [Fact]
    public void ACallThatReturnsNothingUsedAsAValueIsOneDiagnostic()
        => TheOnly(
            Compile(WithCreate("    fn f(o: TicketOptions) -> Ticket {\n        var x: int64 = require_seats(o);\n        return new Ticket {};\n    }\n")),
            DiagnosticCodes.CallHasNoValue);

    // ------- tests

    [Fact]
    public void ATestExpectsARefusalOfAMethodThatMayRefuse()
    {
        var result = Compile(Refusing + "\ntest Ticket.factory.create \"refuses\" {\n    arg options = new TicketOptions { code: \"A1\" };\n    expect refuse new Refusal { why: \"no seats\" };\n}\n");

        AssertOk(result);
        var expectation = Assert.IsType<IrTestRefusalExpectation>(Assert.Single(result.Module!.Tests).Expectation);
        Assert.IsType<IrTestFieldsExpectation>(expectation.Expected);
    }

    [Fact]
    public void ATestCannotExpectARefusalOfAMethodThatCannotRefuse()
        => TheOnly(
            Compile("factory Ticket {\n    fn f() -> Ticket {\n        return new Ticket {};\n    }\n}\n\ntest Ticket.factory.f \"t\" {\n    expect refuse \"no\";\n}\n"),
            DiagnosticCodes.RefusalExpectedOfAMethodThatCannotRefuse);

    [Fact]
    public void AnExpectedRefusalOfAnotherTypeIsRefused()
        => Assert.Equal(
            "'issued' refuses with 'string' but the expectation is 'int64'.",
            TheOnly(
                Compile(
                    "factory Ticket {\n    fn issued(code: string) -> Ticket refuses string {\n        return new Ticket { code: code };\n    }\n}\n"
                    + "\ntest Ticket.factory.issued \"t\" {\n    arg code = \"\";\n    expect refuse 5;\n}\n"),
                DiagnosticCodes.TestExpectationTypeMismatch).Message);

    /// <summary>A refused message is compared as a returned one is: by a literal's fields, until spec 13.3 says what makes two equal.</summary>
    [Fact]
    public void ARefusedMessageIsExpectedOnlyAsALiteral()
    {
        var diagnostic = TheOnly(
            Compile(Refusing + "\ntest Ticket.factory.create \"t\" {\n    arg options = new TicketOptions {};\n    expect refuse \"no\";\n}\n"),
            DiagnosticCodes.MessageReturnCannotBeExpected);

        Assert.StartsWith("'create' refuses with message 'acme.billing.Refusal', and only a literal of one can be expected", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(
            "Expect a literal, 'expect refuse new Refusal { … };', which compares the fields it sets (spec 25.3).",
            diagnostic.Help);
    }

    // ------- what is generated

    [Fact]
    public void CSharpGivesAResultAndARefusalThroughOutParameters()
    {
        var generated = Generated(Compile(Refusing), new CSharpBackend());

        Assert.Contains(
            "public static bool Create(global::Acme.Billing.TicketOptions options, "
            + "[global::System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out global::Acme.Billing.Ticket? result, "
            + "[global::System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out global::Acme.Billing.Refusal? refusal)",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "private static bool RequireSeats(global::Acme.Billing.TicketOptions options, "
            + "[global::System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out global::Acme.Billing.Refusal? refusal)",
            generated,
            StringComparison.Ordinal);
        Assert.Contains("if (!global::Acme.Billing.TicketFactory.RequireSeats(options, out refusal))", generated, StringComparison.Ordinal);
        Assert.Contains("if (!global::Acme.Billing.TicketFactory.CheckedCode(options, out var built, out refusal))", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpWritesAValueTypesOutParameterAsItIs()
        => Assert.Contains(
            "out global::Acme.Billing.Reason refusal)",
            Generated(Compile("factory Ticket {\n    fn f() -> Ticket refuses Reason {\n        return new Ticket {};\n    }\n}\n"), new CSharpBackend()),
            StringComparison.Ordinal);

    [Fact]
    public void CppGivesAResultAndARefusalThroughReferences()
    {
        var generated = Generated(Compile(Refusing), new CppBackend());

        Assert.Contains(
            "static bool create(const ::acme::billing::TicketOptions& options, ::acme::billing::Ticket& result, ::acme::billing::Refusal& refusal);",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "static bool require_seats(const ::acme::billing::TicketOptions& options, ::acme::billing::Refusal& refusal);",
            generated,
            StringComparison.Ordinal);
        Assert.Contains("if (!::acme::billing::TicketFactory::require_seats(options, refusal))", generated, StringComparison.Ordinal);
    }

    /// <summary>A name the method already uses is numbered, as every name generated code declares is.</summary>
    [Fact]
    public void TheParametersAreNumberedWhereTheMethodUsesTheirNames()
    {
        var result = Compile("factory Ticket {\n    fn f(result: string) -> Ticket refuses Refusal {\n        return new Ticket { code: result };\n    }\n}\n");

        Assert.Contains("out global::Acme.Billing.Ticket? result1,", Generated(result, new CSharpBackend()), StringComparison.Ordinal);
        Assert.Contains("const ::std::string& result, ::acme::billing::Ticket& result1,", Generated(result, new CppBackend()), StringComparison.Ordinal);
    }

    /// <summary>
    /// Each call's local is a name of its own in the method, since C# keeps an <c>out var</c> in scope in the whole
    /// block around its statement, and none is a name the method declares.
    /// </summary>
    [Fact]
    public void CSharpGivesEachCallsLocalANameOfItsOwn()
    {
        var generated = Generated(
            Compile(WithCreate("    helper fn f(o: TicketOptions) -> int64 {\n        var built: int64 = (create(o) on_refused new Ticket {}).seats;\n        return built + (create(o) on_refused fail).seats;\n    }\n")),
            new CSharpBackend());

        Assert.Contains("out var built1, out _) ? built1 : new global::Acme.Billing.Ticket()", generated, StringComparison.Ordinal);
        Assert.Contains(
            "out var built2, out _) ? built2 : global::ProtoCross.Runtime.ProtoCrossRefusals.Fail<global::Acme.Billing.Ticket>(\"acme.billing.Ticket.factory.create\")",
            generated,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ATestOfAMethodThatMayRefuseChecksHowItEndedInBothBackends()
    {
        var result = Compile(Refusing
            + "\ntest Ticket.factory.create \"builds\" {\n    arg options = new TicketOptions { code: \"A1\", seats: 2 };\n    expect return new Ticket { code: \"A1\" };\n}\n"
            + "\ntest Ticket.factory.create \"refuses\" {\n    arg options = new TicketOptions { code: \"A1\" };\n    expect refuse new Refusal { why: \"no seats\" };\n}\n");

        var csharp = Generated(result, new CSharpBackend(), tests: true);
        Assert.Contains("ExpectBuilt(global::Acme.Billing.TicketFactory.Create(", csharp, StringComparison.Ordinal);
        Assert.Contains("}, out var actual, out var refusal), refusal, \"acme.billing.Ticket.factory.create: builds\");", csharp, StringComparison.Ordinal);
        Assert.Contains("}, out _, out var actual), \"acme.billing.Ticket.factory.create: refuses\");", csharp, StringComparison.Ordinal);
        Assert.Contains("ExpectField(\"no seats\", actual.Why, ", csharp, StringComparison.Ordinal);

        var cpp = Generated(result, new CppBackend(), tests: true);
        Assert.Contains("::acme::billing::Ticket built{};", cpp, StringComparison.Ordinal);
        Assert.Contains("::acme::billing::Refusal actual{};", cpp, StringComparison.Ordinal);
        Assert.Contains("(expected a refusal, but the method built its result)", cpp, StringComparison.Ordinal);
        Assert.Contains("(expected a result, but the method refused)", cpp, StringComparison.Ordinal);
    }

    /// <summary>
    /// A test's argument may call a factory that refuses, and the C# test, an <c>expect fail</c>'s included, names
    /// what the call built as a method would.
    /// </summary>
    [Fact]
    public void ATestsArgumentMayCallAMethodThatMayRefuse()
    {
        const string argument = "    arg t = Ticket.factory.create(new TicketOptions { code: \"A\", seats: 1 }) on_refused new Ticket {};\n";
        var result = Compile(Refusing
            + "\nfactory Holder {\n    fn of(t: Ticket) -> Holder {\n        return new Holder { name: t.code };\n    }\n}\n"
            + "\ntest Holder.factory.of \"builds\" {\n" + argument + "    expect return new Holder { name: \"A\" };\n}\n"
            + "\ntest Holder.factory.of \"never ends\" {\n" + argument + "    expect fail;\n}\n");

        var csharp = Generated(result, new CSharpBackend(), tests: true);
        Assert.Equal(2, csharp.Split("out var built, out _) ? built : new global::Acme.Billing.Ticket()").Length - 1);
    }

    /// <summary>
    /// A change to a wrapper C# holds as its value is written through a copy, and returned from a method that may
    /// refuse, it is the method's result: given through its parameter, with the method saying it built it.
    /// </summary>
    [Fact]
    public void CSharpReturnsAHeldWrappersChangeThroughTheResultParameter()
    {
        var generated = Generated(
            Compile(
                "extend google.protobuf.Int64Value {\n    mut fn bump() -> int64 {\n        value += 1;\n        return value;\n    }\n}\n\n"
                + "factory Ticket {\n    helper fn bumped(seats: int64) -> int64 refuses Refusal {\n"
                + "        var held: Ticket = new Ticket { spare: new google.protobuf.Int64Value { value: seats } };\n"
                + "        if not has held.spare {\n            refuse new Refusal { why: \"none\" };\n        }\n\n"
                + "        return held.spare.bump();\n    }\n}\n"),
            new CSharpBackend());

        Assert.DoesNotContain("return result", generated, StringComparison.Ordinal);
        Assert.Matches(@"result = [^;]*Bump\(wrapper\);\s+held\.Spare = wrapper\.Value;\s+refusal = default;\s+return true;", generated);
    }

    /// <summary>
    /// C# folds a constant condition and the language does not, so the end of a method that returns nothing, which
    /// says there that it did not refuse, is written with the warning C# may give it suspended, as a section's break is.
    /// </summary>
    [Fact]
    public void CSharpGuardsTheEndOfAMethodThatMayRefuseWhereAConstantConditionMayEndItFirst()
    {
        var generated = Generated(
            Compile(WithCreate("    helper fn settled(n: int64) refuses Refusal {\n        if true {\n            return;\n        }\n    }\n")),
            new CSharpBackend());

        Assert.Matches(
            @"#pragma warning disable CS0162 // A constant condition above may end the method first\.\s+refusal = default;\s+return true;\s+#pragma warning restore CS0162",
            generated);
        Assert.Single(generated.Split('\n'), line => line.Contains("#pragma warning disable CS0162", StringComparison.Ordinal));
    }

    /// <summary>A backend never meets a call to a method that may refuse without the node that says what a refusal makes of it.</summary>
    [Fact]
    public void EveryCallToAMethodThatMayRefuseIsHeldByWhatAnswersIt()
    {
        var result = Compile(Refusing + "\nextend Holder {\n    fn f(o: TicketOptions) -> int64 {\n        return Ticket.factory.create(o).seats;\n    }\n}\n");
        var nodes = result.Module!.Methods.SelectMany(method => IrWalk.DescendantsAndSelf(method)).ToList();
        var answered = nodes.OfType<IrRefusableCall>().Select(refusable => refusable.Call).ToHashSet(ReferenceEqualityComparer.Instance);

        Assert.All(nodes.OfType<IrMethodCall>().Where(call => call.Target.CanRefuse), call => Assert.Contains(call, answered));
        Assert.Contains(nodes.OfType<IrRefusableCall>(), refusable => refusable.OnRefused is RefusalBehavior.Unstated);
    }
}
