using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Syntax;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// A message's factory (spec 16.3): a <c>factory</c> block whose methods build the message and are
/// called on nothing, its helpers, the rules that keep it apart from the message's own methods, and the
/// class each backend generates it into (spec 24).
/// </summary>
/// <remarks>
/// What a factory computes is pinned in the conformance corpus, <c>factories.pcross</c> and
/// <c>multi/factory_project</c>, which both backends compile and run. These cover what the corpus
/// cannot: every refusal, and the shape of what is generated.
/// </remarks>
public class FactoryTests
{
    private const string Schema =
        """
        syntax = "proto3";

        package acme.billing;

        message TicketOptions {
          string code = 1;
          int64 seats = 2;
        }

        message Ticket {
          message Stub {
            int64 n = 1;
          }

          string code = 1;
          int64 seats = 2;
          optional int64 discount = 3;
          repeated int64 tags = 4;
          map<string, int64> notes = 5;
          Holder holder = 6;
        }

        message Holder {
          string name = 1;
          int64 factory = 2;
        }

        message Clash {
          enum factory {
            FACTORY_NONE = 0;
            FACTORY_SOME = 1;
          }

          int64 n = 1;
        }
        """;

    private const string TicketFactory =
        """
        factory Ticket {
            fn create(options: TicketOptions) -> Ticket {
                return new Ticket { code: options.code, seats: doubled(options.seats) };
            }

            fn empty() -> Ticket {
                return create(new TicketOptions { code: "none" });
            }

            helper fn doubled(seats: int64) -> int64 {
                return seats * 2;
            }
        }
        """;

    private static CompilationResult Compile(string body, string schema = Schema, string? project = null)
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "tickets.proto"), schema);
        var path = Path.Combine(directory, "tickets.pcross");
        File.WriteAllText(path, "import proto \"tickets.proto\";\n\n" + body);

        return new Compilation(
                SourceDocument.ReadFrom(path),
                new CompilationOptions
                {
                    IncludePaths = [directory],
                    ProjectNamespace = project is null ? null : new ProjectNamespace(project),
                })
            .Compile();
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

    // ------- the declaration

    [Fact]
    public void AFactoryBlockDeclaresItsMethodsAndHelpers()
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(TicketFactory, "f.pcross", diagnostics).Tokenize();
        var unit = new Parser(tokens, "f.pcross", diagnostics).ParseCompilationUnit();

        Assert.Empty(diagnostics);
        var factory = Assert.Single(unit.Factories);
        Assert.Equal("Ticket", factory.MessageName.Text);
        Assert.Equal("factory", factory.Keyword.Text);
        Assert.Equal(["create", "empty", "doubled"], factory.Methods.Select(method => method.Name.Text));
        Assert.Equal([false, false, true], factory.Methods.Select(method => method.IsHelper));
    }

    /// <summary>Both words are names everywhere a declaration or a method does not begin (spec 6.4).</summary>
    [Fact]
    public void FactoryAndHelperAreNamesWhereNothingBegins()
        => AssertOk(Compile(
            """
            extend Ticket {
                fn f(factory: int64, helper: int64) -> int64 {
                    var total: int64 = factory + helper;
                    return total;
                }
            }
            """));

    [Fact]
    public void SomethingThatIsNotAMethodInAFactoryIsReportedAndSkipped()
    {
        var result = Compile(
            """
            factory Ticket {
                seats: int64;
                fn empty() -> Ticket { return new Ticket { }; }
            }
            """);

        Assert.Equal(DiagnosticCodes.UnexpectedFactoryMember.Code, Assert.Single(result.Diagnostics).Code);
        Assert.Single(result.Module!.FactoryOf("acme.billing.Ticket"));
    }

    [Fact]
    public void ATopLevelDeclarationListsTheFactory()
    {
        var result = Compile("fn stray() { }");

        var reported = Assert.Single(result.Diagnostics, d => d.Code == DiagnosticCodes.UnexpectedTopLevelDeclaration.Code);
        Assert.Contains("'factory'", reported.Message, StringComparison.Ordinal);
    }

    // ------- what a factory's methods are

    [Fact]
    public void AFactoryCompilesAndItsMethodsHaveNoReceiver()
    {
        var result = Compile(TicketFactory);

        AssertOk(result);
        var module = result.Module!;
        Assert.Empty(module.MethodsOn("acme.billing.Ticket"));
        Assert.Equal(
            [IrMethodKind.Factory, IrMethodKind.Factory, IrMethodKind.Helper],
            module.FactoryOf("acme.billing.Ticket").Select(method => method.Signature.Kind));
        Assert.Equal("acme.billing.Ticket", Assert.Single(module.Factories).Message.FullName);
        Assert.All(
            IrWalk().OfType<IrMethodCall>(),
            call => Assert.Null(call.Receiver));

        IEnumerable<IrNode> IrWalk() => ProtoCross.Semantics.IrWalk.DescendantsAndSelf(module);
    }

    [Theory]
    [InlineData("fn create() -> Holder { return new Holder { }; }", "returns 'acme.billing.Holder'")]
    [InlineData("fn create() { }", "returns nothing")]
    [InlineData("fn create() -> int64 { return 1; }", "returns 'int64'")]
    public void EveryFactoryMethodReturnsItsMessage(string method, string said)
    {
        var refused = TheOnly(Compile($"factory Ticket {{\n    {method}\n}}\n"), DiagnosticCodes.FactoryMethodMustReturnItsMessage);

        Assert.Contains(said, refused.Message, StringComparison.Ordinal);
        Assert.Contains("helper fn", refused.Help, StringComparison.Ordinal);
    }

    [Fact]
    public void AHelperNeedNotReturnTheMessage()
        => AssertOk(Compile(TicketFactory));

    [Fact]
    public void AMessageHasOneFactory()
        => TheOnly(
            Compile(TicketFactory + "\nfactory Ticket {\n    fn other() -> Ticket { return new Ticket { }; }\n}\n"),
            DiagnosticCodes.DuplicateFactory);

    [Fact]
    public void AFactoryMethodSharesTheMessagesNamesWithItsFields()
        => TheOnly(
            Compile("factory Ticket {\n    fn seats() -> Ticket { return new Ticket { }; }\n}\n"),
            DiagnosticCodes.MethodNameCollidesWithField);

    [Fact]
    public void AFactoryMethodSharesTheMessagesNamesWithItsMethods()
        => TheOnly(
            Compile("extend Ticket {\n    fn total() -> int64 { return seats; }\n}\n"
                + "factory Ticket {\n    fn total() -> Ticket { return new Ticket { }; }\n}\n"),
            DiagnosticCodes.DuplicateMethod);

    [Fact]
    public void AFactoryMethodHasNoReceiverToChange()
        => TheOnly(
            Compile("factory Ticket {\n    mut fn create() -> Ticket { return new Ticket { }; }\n}\n"),
            DiagnosticCodes.MutatingFactoryMethod);

    [Fact]
    public void AFactoryMayChangeTheMessageItBuilt()
        => AssertOk(Compile(
            """
            extend Ticket {
                mut fn add_seat() { seats += 1; }
            }

            factory Ticket {
                fn create() -> Ticket {
                    var built: Ticket = new Ticket { seats: 1 };
                    built.add_seat();
                    built.code = "made";
                    return built;
                }
            }
            """));

    [Fact]
    public void AFactoryMayNotChangeAMessageItWasGiven()
        => TheOnly(
            Compile(
                """
                extend Ticket {
                    mut fn add_seat() { seats += 1; }
                }

                factory Ticket {
                    fn from(given: Ticket) -> Ticket {
                        given.add_seat();
                        return given;
                    }
                }
                """),
            DiagnosticCodes.MessageIsReadOnly);

    [Fact]
    public void ABareNameInAFactoryReachesNoField()
    {
        var refused = TheOnly(
            Compile("factory Ticket {\n    fn create() -> Ticket { return new Ticket { seats: seats }; }\n}\n"),
            DiagnosticCodes.UnknownName);

        Assert.Contains("factory method", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMessagesOwnMethodCalledBareInAFactoryHasNothingToBeCalledOn()
        => TheOnly(
            Compile("extend Ticket {\n    fn total() -> int64 { return seats; }\n}\n"
                + "factory Ticket {\n    fn create() -> Ticket { return new Ticket { seats: total() }; }\n}\n"),
            DiagnosticCodes.CallWithoutAReceiver);

    [Fact]
    public void AFactoryMethodIsCalledThroughTheFactoryFromAnExtendMethod()
        => AssertOk(Compile(TicketFactory
            + "\nextend TicketOptions {\n    fn seats_built() -> int64 { return Ticket.factory.create(this_options()).seats; }\n"
            + "    fn this_options() -> TicketOptions { return new TicketOptions { code: code, seats: seats }; }\n}\n"));

    [Theory]
    [InlineData("return Ticket.factory.empty().seats;", null)]
    [InlineData("return empty().seats;", "FactoryMethodCalledOnAValue")]
    [InlineData("return new Ticket { }.empty().seats;", "FactoryMethodCalledOnAValue")]
    [InlineData("return Ticket.factory.doubled(1);", "HelperUsedOutsideItsFactory")]
    [InlineData("var f = Ticket.factory;\n        return 0;", "FactoryUsedAsAValue")]
    [InlineData("var f = Ticket.factory.empty;\n        return 0;", "MethodUsedAsAValue")]
    [InlineData("return Ticket.factory.nope().seats;", "UnknownMethod")]
    [InlineData("return Holder.factory.make().seats;", "UnknownMethod")]
    public void AFactoryIsReachedOnlyThroughItsName(string body, string? refusal)
    {
        var result = Compile(TicketFactory + "\nextend Ticket {\n    fn f() -> int64 {\n        " + body + "\n    }\n}\n");

        if (refusal is null)
        {
            AssertOk(result);
            return;
        }

        var expected = (DiagnosticDescriptor)typeof(DiagnosticCodes).GetField(refusal)!.GetValue(null)!;
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expected.Code);
    }

    /// <summary>
    /// A value named like a message wins over the message, as it does over an enum (spec 16.3), so its
    /// field named <c>factory</c> is read.
    /// </summary>
    [Fact]
    public void AValueNamedLikeTheMessageIsReadAsTheValue()
        => AssertOk(Compile(TicketFactory
            + "\nextend Holder {\n    fn f(Ticket: Holder) -> int64 { return Ticket.factory; }\n}\n"));

    [Fact]
    public void AHelperMayBeWrittenOnlyInAFactory()
        => TheOnly(
            Compile("extend Ticket {\n    helper fn total() -> int64 { return seats; }\n}\n"),
            DiagnosticCodes.HelperOutsideAFactory);

    // ------- a nested type named factory

    [Fact]
    public void AFactoryForAMessageWithANestedTypeNamedFactoryIsRefused()
        => TheOnly(
            Compile("factory Clash {\n    fn make() -> Clash { return new Clash { }; }\n}\n"),
            DiagnosticCodes.FactoryNameIsTaken);

    [Fact]
    public void ANestedTypeNamedFactoryKeepsItsNameWithoutAFactory()
        => AssertOk(Compile(
            "extend Clash {\n    fn some() -> bool {\n"
            + "        return acme.billing.Clash.factory.FACTORY_SOME == acme.billing.Clash.factory.FACTORY_NONE;\n    }\n}\n"));

    // ------- the generated class's name

    [Fact]
    public void AFactoryWhoseClassASchemaMessageAlreadyNamesIsRefused()
        => TheOnly(
            Compile(TicketFactory, Schema + "\nmessage TicketFactory {\n  int64 n = 1;\n}\n"),
            DiagnosticCodes.FactoryClassNameCollides);

    [Fact]
    public void TwoMessagesOfOneNameShareAProjectsNamespace()
    {
        const string twoTickets =
            """
            syntax = "proto3";

            package acme.billing;

            import "shipping.proto";

            message Ticket {
              int64 seats = 1;
            }
            """;

        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(
            Path.Combine(directory, "shipping.proto"),
            "syntax = \"proto3\";\n\npackage acme.shipping;\n\nmessage Ticket {\n  int64 crates = 1;\n}\n");
        File.WriteAllText(Path.Combine(directory, "tickets.proto"), twoTickets);
        var path = Path.Combine(directory, "tickets.pcross");
        File.WriteAllText(
            path,
            "import proto \"tickets.proto\";\n\n"
            + "factory acme.billing.Ticket {\n    fn one() -> acme.billing.Ticket { return new acme.billing.Ticket { }; }\n}\n\n"
            + "factory acme.shipping.Ticket {\n    fn one() -> acme.shipping.Ticket { return new acme.shipping.Ticket { }; }\n}\n");

        CompilationResult CompileAs(string? project)
            => new Compilation(
                    SourceDocument.ReadFrom(path),
                    new CompilationOptions
                    {
                        IncludePaths = [directory],
                        ProjectNamespace = project is null ? null : new ProjectNamespace(project),
                    })
                .Compile();

        AssertOk(CompileAs(null));
        TheOnly(CompileAs("acme.app"), DiagnosticCodes.FactoryClassNameCollides);
    }

    [Fact]
    public void AFactoryForAWellKnownTypeIsWarnedAbout()
    {
        var result = Compile(
            "import proto \"google/protobuf/timestamp.proto\";\n\n"
            + "factory google.protobuf.Timestamp {\n"
            + "    fn at(seconds: int64) -> google.protobuf.Timestamp { return new google.protobuf.Timestamp { seconds: seconds }; }\n}\n");

        var warned = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.FactoryForAWellKnownType.Code, warned.Code);
        Assert.Equal(DiagnosticSeverity.Warning, warned.Severity);
    }

    // ------- tests of a factory

    [Fact]
    public void ATestOfAFactoryMethodHasNoReceiver()
    {
        var result = Compile(TicketFactory
            + "\ntest Ticket.factory.create \"builds one\" {\n"
            + "    arg options = new TicketOptions { code: \"A1\", seats: 2 };\n"
            + "    expect return new Ticket { code: \"A1\", seats: 4 };\n}\n");

        AssertOk(result);
        var test = Assert.Single(result.Module!.Tests);
        Assert.False(test.Target.HasReceiver);
        Assert.Equal("acme.billing.Ticket.factory.create: builds one", test.Identity);
        Assert.IsType<IrTestFieldsExpectation>(test.Expectation);
    }

    [Theory]
    [InlineData("test Ticket.factory.create \"x\" {\n    receiver { }\n    arg options = new TicketOptions { };\n    expect fail;\n}\n", "FactoryTestDeclaresAReceiver")]
    [InlineData("test Ticket.factory.doubled \"x\" {\n    arg seats = 1;\n    expect return 2;\n}\n", "HelperUsedOutsideItsFactory")]
    [InlineData("test Ticket.create \"x\" {\n    receiver { }\n    arg options = new TicketOptions { };\n    expect fail;\n}\n", "UnknownTestTarget")]
    [InlineData("test Holder.factory.make \"x\" {\n    expect fail;\n}\n", "UnknownTestTarget")]
    public void ATestReachesOnlyAFactorysPublicMethodsThroughIt(string test, string refusal)
    {
        var expected = (DiagnosticDescriptor)typeof(DiagnosticCodes).GetField(refusal)!.GetValue(null)!;

        TheOnly(Compile(TicketFactory + "\n" + test), expected);
    }

    // ------- an expected message

    [Theory]
    [InlineData("tags: [1]")]
    [InlineData("notes: [{ key: \"a\", value: 1 }]")]
    [InlineData("holder: Holder.factory.none()")]
    public void AFieldAnExpectedLiteralCannotCompareIsRefused(string field)
        => TheOnly(
            Compile(TicketFactory
                + "\nfactory Holder {\n    fn none() -> Holder { return new Holder { }; }\n}\n"
                + "\ntest Ticket.factory.empty \"x\" {\n    expect return new Ticket { " + field + " };\n}\n"),
            DiagnosticCodes.ExpectedFieldCannotBeCompared);

    [Fact]
    public void AnExpectedLiteralChecksPresenceWhereAFieldHasItAndValuesInTheOrderWritten()
    {
        var result = Compile(TicketFactory
            + "\ntest Ticket.factory.empty \"x\" {\n"
            + "    expect return new Ticket { seats: 0, discount: 0, holder: new Holder { name: \"h\" } };\n}\n");

        AssertOk(result);
        var expectation = Assert.IsType<IrTestFieldsExpectation>(Assert.Single(result.Module!.Tests).Expectation);
        var checks = ProtoCross.Semantics.IrExpectedFields.Of(expectation).Checks;

        Assert.Equal(
            ["value seats", "set discount", "value discount", "set holder", "value holder.name"],
            checks.Select(check => (check is ProtoCross.Semantics.IrPresenceCheck ? "set " : "value ") + check.Path));
    }

    // ------- what is generated

    [Fact]
    public void CSharpDeclaresAStaticClassBesideTheMessageWithItsHelpersPrivate()
    {
        var generated = Generated(Compile(TicketFactory), new CSharpBackend());

        Assert.Contains("namespace Acme.Billing", generated, StringComparison.Ordinal);
        Assert.Contains("public static class TicketFactory", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("partial class TicketFactory", generated, StringComparison.Ordinal);
        Assert.Contains("public static global::Acme.Billing.Ticket Create(global::Acme.Billing.TicketOptions options)", generated, StringComparison.Ordinal);
        Assert.Contains("private static long Doubled(long seats)", generated, StringComparison.Ordinal);
        Assert.Contains("global::Acme.Billing.TicketFactory.Create(", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpDeclaresAProjectsFactoryInTheProjectsNamespace()
    {
        var generated = Generated(Compile(TicketFactory, project: "acme.app"), new CSharpBackend());

        Assert.Contains("namespace Acme.App", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("namespace Acme.Billing", generated, StringComparison.Ordinal);
        Assert.Contains("global::Acme.App.TicketFactory.Create(", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void CppDeclaresAStructOfStaticMembersWithItsHelpersPrivate()
    {
        var generated = Generated(Compile(TicketFactory), new CppBackend());

        Assert.Contains("struct TicketFactory\n  {\n", generated, StringComparison.Ordinal);
        Assert.Contains("    static ::acme::billing::Ticket create(const ::acme::billing::TicketOptions& options);", generated, StringComparison.Ordinal);
        Assert.Contains("  private:\n    static ::std::int64_t doubled(::std::int64_t seats);", generated, StringComparison.Ordinal);
        Assert.Contains("inline ::acme::billing::Ticket TicketFactory::create(const ::acme::billing::TicketOptions& options)", generated, StringComparison.Ordinal);
        Assert.Contains("::acme::billing::TicketFactory::create(", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void ANestedMessagesFactoryIsNamedAfterItsEnclosingMessageInBothBackends()
    {
        var result = Compile("factory Stub {\n    fn of(n: int64) -> Stub { return new Stub { n: n }; }\n}\n");

        Assert.Contains("public static class Ticket_StubFactory", Generated(result, new CSharpBackend()), StringComparison.Ordinal);
        Assert.Contains("struct Ticket_StubFactory", Generated(result, new CppBackend()), StringComparison.Ordinal);
    }

    /// <summary>A member named after its class would be a constructor in C++ and is CS0542 in C#, so both escape it.</summary>
    [Fact]
    public void AMethodNamedAfterItsFactorysClassIsEscapedInBothBackends()
    {
        var result = Compile("factory Ticket {\n    fn TicketFactory() -> Ticket { return new Ticket { }; }\n}\n");

        Assert.Contains("public static global::Acme.Billing.Ticket TicketFactory_()", Generated(result, new CSharpBackend()), StringComparison.Ordinal);
        Assert.Contains("static ::acme::billing::Ticket TicketFactory_();", Generated(result, new CppBackend()), StringComparison.Ordinal);
    }

    [Fact]
    public void ATestOfAFactoryMethodCallsItWithNoReceiverInBothBackends()
    {
        var result = Compile(TicketFactory
            + "\ntest Ticket.factory.create \"builds one\" {\n"
            + "    arg options = new TicketOptions { code: \"A1\", seats: 2 };\n"
            + "    expect return new Ticket { code: \"A1\", seats: 4 };\n}\n");

        var csharp = Generated(result, new CSharpBackend(), tests: true);
        Assert.DoesNotContain("var receiver", csharp, StringComparison.Ordinal);
        Assert.Contains("var actual = global::Acme.Billing.TicketFactory.Create(", csharp, StringComparison.Ordinal);
        Assert.Contains("ExpectField(\"A1\", actual.Code, ", csharp, StringComparison.Ordinal);
        Assert.Contains("TicketFactoryCreateBuildsOne", csharp, StringComparison.Ordinal);

        var cpp = Generated(result, new CppBackend(), tests: true);
        Assert.DoesNotContain("receiver", cpp, StringComparison.Ordinal);
        Assert.Contains("const auto actual = ::acme::billing::TicketFactory::create(", cpp, StringComparison.Ordinal);
        Assert.Contains("const auto found = actual.seats();", cpp, StringComparison.Ordinal);
    }
}
