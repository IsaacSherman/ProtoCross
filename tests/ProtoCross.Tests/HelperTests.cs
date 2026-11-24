using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Syntax;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// A project's helpers (spec 16.4): <c>helper fn</c> written at the top level, which belongs to no message,
/// the bare calls and test targets that reach it, the names it may not share, and where each backend
/// generates it (spec 24).
/// </summary>
/// <remarks>
/// What a helper computes is pinned in the conformance corpus, <c>multi/helpers_project</c>, which both
/// backends compile and run. These cover what the corpus cannot: every refusal, and the shape of what is
/// generated.
/// </remarks>
public class HelperTests
{
    private const string Project = "acme.billing";

    private const string Schema =
        """
        syntax = "proto3";

        package acme.billing;

        message Range {
          double low = 1;
          double high = 2;
          repeated double values = 3;
        }

        message Problem {
          string reason = 1;
        }
        """;

    private const string Bounds =
        """
        helper fn bounds(left: double, right: double) -> repeated double {
            var products: repeated double = [left * right, left + right];
            var lowest = products[0];
            var highest = products[0];
            for product in products {
                if product < lowest {
                    lowest = product;
                }
                if product > highest {
                    highest = product;
                }
            }
            return [lowest, highest];
        }
        """;

    private static CompilationResult Compile(string body, string schema = Schema, string? project = Project)
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "ranges.proto"), schema);
        var path = Path.Combine(directory, "ranges.pcross");
        File.WriteAllText(path, "import proto \"ranges.proto\";\n\n" + body);

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
    public void AHelperAtTheTopLevelIsDeclaredAsOne()
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(Bounds, "h.pcross", diagnostics).Tokenize();
        var unit = new Parser(tokens, "h.pcross", diagnostics).ParseCompilationUnit();

        Assert.Empty(diagnostics);
        var helper = Assert.Single(unit.Helpers);
        Assert.Equal("bounds", helper.Name.Text);
        Assert.True(helper.IsHelper, "a top-level helper is written 'helper fn', as a factory's is");
    }

    [Fact]
    public void AHelperBelongsToNoMessage()
    {
        var result = Compile(Bounds);

        AssertOk(result);
        var helper = Assert.Single(result.Module!.Helpers);
        Assert.Null(helper.Receiver);
        Assert.Equal(IrMethodKind.Standalone, helper.Signature.Kind);
        Assert.False(helper.Signature.HasReceiver, "a helper is called on nothing");
        Assert.Equal("bounds", helper.Signature.QualifiedName);
        Assert.Equal("helper fn bounds(left: double, right: double) -> repeated double", helper.Signature.DisplayName);
    }

    [Fact]
    public void AHelperOutsideAProjectIsRefused()
    {
        var refused = TheOnly(Compile(Bounds, project: null), DiagnosticCodes.HelperOutsideAProject);
        Assert.Contains("'.pcproj'", refused.Help);
    }

    // ------- who calls it

    [Fact]
    public void EveryKindOfMethodAndATestCallsAHelperByItsName()
    {
        var result = Compile(
            Bounds
            + """

            helper fn widest(left: double, right: double) -> double {
                var found = bounds(left, right);
                if found.count() == 2 {
                    return found[1] - found[0];
                }
                return 0.0;
            }

            extend Range {
                fn width() -> double {
                    return widest(low, high);
                }
            }

            factory Range {
                fn of(left: double, right: double) -> Range {
                    return new Range { low: left, high: right, values: spread(left, right) };
                }

                helper fn spread(left: double, right: double) -> repeated double {
                    return bounds(left, right);
                }
            }

            test widest "spans the two" {
                arg left = 2.0;
                arg right = 3.0;
                expect return widest(2.0, 3.0);
            }
            """);

        AssertOk(result);
    }

    [Fact]
    public void ATestTargetsAHelperByItsNameAlone()
    {
        var result = Compile(Bounds + "\ntest bounds \"orders the two\" {\n    arg left = 2.0;\n    arg right = 3.0;\n    expect return [5.0, 6.0];\n}\n");

        AssertOk(result);
        var test = Assert.Single(result.Module!.Tests);
        Assert.Equal(IrMethodKind.Standalone, test.Target.Kind);
        Assert.Null(test.Receiver);
        Assert.Equal("bounds: orders the two", test.Identity);
    }

    [Fact]
    public void ATestOfAHelperDeclaresNoReceiver()
    {
        var result = Compile(Bounds + "\ntest bounds \"x\" {\n    receiver { low: 1.0 }\n    arg left = 2.0;\n    arg right = 3.0;\n    expect return [5.0, 6.0];\n}\n");

        var refused = TheOnly(result, DiagnosticCodes.FactoryTestDeclaresAReceiver);
        Assert.Contains("'bounds' is a helper", refused.Message);
        Assert.Contains("give the helper its arguments", refused.Help);
    }

    [Fact]
    public void ARefusedReceiverBlockKeepsItsExpressionsForEditorRequests()
    {
        var result = Compile("""
            helper fn doubled(n: int64) -> int64 { return n * 2; }

            test doubled "a receiver block written by mistake" {
                receiver { count: doubled(2) }
                arg n = 3;
                expect return 6;
            }
            """);

        TheOnly(result, DiagnosticCodes.FactoryTestDeclaresAReceiver);
        Assert.NotNull(result.Module);
        var test = Assert.Single(result.Module.Tests);
        Assert.Contains(IrWalk.DescendantsAndSelf(test), node => node is IrMethodCall { Target.Name: "doubled" });
    }

    [Fact]
    public void AnUnknownNameInAHelpersTestIsNotSaidToBeInAHelper()
    {
        var result = Compile(Bounds + "\ntest bounds \"x\" {\n    arg left = low;\n    arg right = 3.0;\n    expect fail;\n}\n");

        var refused = TheOnly(result, DiagnosticCodes.UnknownName);
        Assert.Equal("'low' is not a variable or parameter.", refused.Message);
    }

    [Fact]
    public void ATestOfOneNameThatNamesNoHelperIsRefused()
    {
        var refused = TheOnly(Compile(Bounds + "\ntest nothing \"x\" {\n    expect fail;\n}\n"), DiagnosticCodes.InvalidTestTarget);
        Assert.Contains("No helper is named 'nothing'", refused.Message);
        Assert.Contains("'test clamp'", refused.Help);
    }

    [Fact]
    public void ACallThatNamesAMethodAndAHelperIsRefused()
    {
        var result = Compile(
            Bounds
            + """

            extend Range {
                fn bounds(left: double, right: double) -> repeated double {
                    return [left, right];
                }

                fn both() -> repeated double {
                    return bounds(low, high);
                }
            }
            """);

        var refused = TheOnly(result, DiagnosticCodes.CallNamesAMethodAndAHelper);
        Assert.Contains("'bounds' names both a method of 'acme.billing.Range' and a helper", refused.Message);
    }

    [Fact]
    public void ACallInAFactoryThatNamesItsMemberAndAHelperIsRefused()
    {
        var result = Compile(
            Bounds
            + """

            factory Range {
                fn of(left: double, right: double) -> Range {
                    return new Range { values: bounds(left, right) };
                }

                helper fn bounds(left: double, right: double) -> repeated double {
                    return [left, right];
                }
            }
            """);

        var refused = TheOnly(result, DiagnosticCodes.CallNamesAMethodAndAHelper);
        Assert.Contains("a method of the factory of 'acme.billing.Range'", refused.Message);
    }

    [Fact]
    public void AMethodOfAnotherMessageDoesNotMeetAHelperOfItsName()
    {
        var result = Compile(
            Bounds
            + """

            extend Problem {
                fn bounds(left: double, right: double) -> repeated double {
                    return [left, right];
                }
            }

            extend Range {
                fn both() -> repeated double {
                    return bounds(low, high);
                }
            }
            """);

        AssertOk(result);
        var call = result.Module!.MethodsOn("acme.billing.Range").Single()
            .Body.Statements.OfType<IrReturn>().Single().Value as IrMethodCall;
        Assert.Equal(IrMethodKind.Standalone, call!.Target.Kind);
    }

    // ------- its body

    [Fact]
    public void AHelperHasNoFieldsToReadByName()
    {
        var refused = TheOnly(Compile("helper fn low_of() -> double {\n    return low;\n}\n"), DiagnosticCodes.UnknownName);
        Assert.Contains("A helper has no message of its own", refused.Message);
    }

    [Fact]
    public void ABareCallInAHelperReachesOnlyHelpers()
    {
        var result = Compile(
            """
            extend Range {
                fn width() -> double {
                    return high - low;
                }
            }

            helper fn of(range: Range) -> double {
                return width();
            }
            """);

        var refused = TheOnly(result, DiagnosticCodes.UnknownMethod);
        Assert.Contains("No helper is named 'width'", refused.Message);
        Assert.Contains("'value.width(…)'", refused.Help);
    }

    [Fact]
    public void AHelperCallsAMessagesMethodOnAMessageItHolds()
        => AssertOk(Compile(
            """
            extend Range {
                fn width() -> double {
                    return high - low;
                }
            }

            helper fn of(range: Range) -> double {
                return range.width();
            }
            """));

    [Fact]
    public void AHelperMayChangeAMessageItBuiltButNotOneItWasGiven()
    {
        const string Widen =
            """
            extend Range {
                mut fn widen() {
                    high = high + 1.0;
                }
            }

            """;

        AssertOk(Compile(Widen + "helper fn built() -> Range {\n    var range = new Range { low: 1.0 };\n    range.widen();\n    return range;\n}\n"));
        TheOnly(Compile(Widen + "helper fn given(range: Range) -> Range {\n    range.widen();\n    return range;\n}\n"), DiagnosticCodes.MessageIsReadOnly);
    }

    [Fact]
    public void AHelperMayRefuse()
        => AssertOk(Compile(
            """
            helper fn checked(value: double) -> double refuses Problem {
                if value < 0.0 {
                    refuse new Problem { reason: "negative" };
                }
                return value;
            }

            helper fn either(value: double) -> double {
                return checked(value) on_refused 0.0;
            }
            """));

    /// <summary>
    /// A test source's helper is generated with the tests, so a production method that called it would call
    /// something the production build leaves out (spec 25.3.1).
    /// </summary>
    [Fact]
    public void AProductionMethodMayNotCallATestSourcesHelper()
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "ranges.proto"), Schema);
        var production = Path.Combine(directory, "ranges.pcross");
        File.WriteAllText(
            production,
            "import proto \"ranges.proto\";\n\nextend Range {\n    fn ends() -> repeated double {\n        return bounds(low, high);\n    }\n}\n");
        var checks = Path.Combine(directory, "range_checks.pcross");
        File.WriteAllText(checks, "import proto \"ranges.proto\";\n\n" + Bounds);

        var result = new Compilation(
                [SourceDocument.ReadFrom(production), SourceDocument.ReadFrom(checks) with { Role = SourceRole.Test }],
                new CompilationOptions { IncludePaths = [directory], ProjectNamespace = new ProjectNamespace(Project) })
            .Compile();

        var refused = TheOnly(result, DiagnosticCodes.ProductionMethodCallsTestHelper);
        Assert.Contains("'bounds' is declared in 'range_checks.pcross', a test source", refused.Message);
    }

    // ------- names

    [Fact]
    public void TwoHelpersOfOneNameAreRefused()
    {
        var refused = TheOnly(Compile(Bounds + "\n" + Bounds), DiagnosticCodes.DuplicateMethod);
        Assert.Contains("A helper named 'bounds' is already declared", refused.Message);
    }

    [Fact]
    public void TwoHelpersThatMeetInCSharpAreRefused()
    {
        var refused = TheOnly(
            Compile("helper fn find_bounds() -> double {\n    return 1.0;\n}\n\nhelper fn FindBounds() -> double {\n    return 2.0;\n}\n"),
            DiagnosticCodes.DuplicateMethod);
        Assert.Contains("'Acme.Billing.Helpers.ProtoCrossHelpers.FindBounds' in C#, as the helper 'find_bounds' is", refused.Message);
    }

    [Fact]
    public void AHelperEscapedForCppIsOneFunctionWithTheOneItWouldBe()
    {
        var refused = TheOnly(
            Compile("helper fn delete() -> double {\n    return 1.0;\n}\n\nhelper fn delete_() -> double {\n    return 2.0;\n}\n"),
            DiagnosticCodes.DuplicateMethod);
        Assert.Contains("'delete_' would be generated as 'Acme.Billing.Helpers.ProtoCrossHelpers.Delete' in C#, as the helper 'delete' is", refused.Message);
    }

    [Theory]
    [InlineData("acme.billing.helpers", "message ProtoCrossHelpers { int64 n = 1; }", "'Acme.Billing.Helpers.ProtoCrossHelpers' in C#")]
    [InlineData("acme.billing", "message Helpers { int64 n = 1; }", "'Acme.Billing.Helpers' in C#")]
    [InlineData("acme.billing", "message helpers { int64 n = 1; }", "'acme::billing::helpers' in C++")]
    [InlineData("acme.billing.helpers", "message bounds { int64 n = 1; }", "'acme::billing::helpers::bounds' in C++")]
    public void AHelperCannotTakeANameASchemaDeclares(string package, string declaration, string taken)
    {
        var schema = $"syntax = \"proto3\";\n\npackage {package};\n\n{declaration}\n";
        var refused = TheOnly(Compile(Bounds, schema), DiagnosticCodes.FactoryClassNameCollides);
        Assert.Contains(taken, refused.Message);
    }

    [Theory]
    [InlineData("acme.billing.helpers", "READY", "READY", "acme::billing::helpers::READY")]
    [InlineData("acme.billing", "helpers", "start", "acme::billing::helpers")]
    public void AHelperAndItsNamespaceCannotTakeAnEnumValuesCppName(
        string package, string value, string helper, string taken)
    {
        var schema = $$"""
            syntax = "proto3";
            package {{package}};
            enum Kind { {{value}} = 0; }
            """;
        var result = Compile($"helper fn {helper}(kind: Kind) -> int64 {{ return 1; }}", schema);

        var refused = TheOnly(result, DiagnosticCodes.FactoryClassNameCollides);
        Assert.Contains($"'{taken}' in C++", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMethodNamedLikeTheHelpersNamespaceIsRefused()
    {
        var refused = TheOnly(
            Compile("extend Range {\n    fn helpers() -> double {\n        return low;\n    }\n}\n\n" + Bounds),
            DiagnosticCodes.FactoryClassNameCollides);
        Assert.Contains("'acme::billing::helpers' in C++, the name of the method 'acme.billing.Range.helpers'", refused.Message);
    }

    // ------- what is generated

    [Fact]
    public void CSharpDeclaresAHelperInTheProjectsHelpersClass()
    {
        var generated = Generated(
            Compile(Bounds + "\nextend Range {\n    fn spread() -> repeated double {\n        return bounds(low, high);\n    }\n}\n"),
            new CSharpBackend());

        Assert.Contains("namespace Acme.Billing.Helpers", generated);
        Assert.Contains("public static partial class ProtoCrossHelpers", generated);
        Assert.Contains("public static global::Google.Protobuf.Collections.RepeatedField<double> Bounds(double left, double right)", generated);
        Assert.Contains("global::Acme.Billing.Helpers.ProtoCrossHelpers.Bounds(self.Low, self.High)", generated);
    }

    [Fact]
    public void CSharpEscapesAHelperNamedLikeItsClass()
        => Assert.Contains(
            "public static double ProtoCrossHelpers_()",
            Generated(Compile("helper fn proto_cross_helpers() -> double {\n    return 1.0;\n}\n"), new CSharpBackend()));

    [Fact]
    public void CppDeclaresAHelperAsAFreeFunctionInTheHelpersNamespace()
    {
        var generated = Generated(
            Compile(Bounds + "\nextend Range {\n    fn spread() -> repeated double {\n        return bounds(low, high);\n    }\n}\n"),
            new CppBackend());

        Assert.Contains("namespace acme::billing::helpers", generated);
        Assert.Contains("inline ::google::protobuf::RepeatedField<double> bounds(double left, double right);", generated);
        Assert.Contains("::acme::billing::helpers::bounds(self.low(), self.high())", generated);
    }

    [Fact]
    public void BothBackendsTestAHelperWithNoReceiver()
    {
        var result = Compile(Bounds + "\ntest bounds \"orders the two\" {\n    arg left = 2.0;\n    arg right = 3.0;\n    expect return [5.0, 6.0];\n}\n");

        var csharp = Generated(result, new CSharpBackend(), tests: true);
        Assert.Contains("global::Acme.Billing.Helpers.ProtoCrossHelpers.Bounds(2d, 3d)", csharp);
        Assert.DoesNotContain("var receiver", csharp);

        var cpp = Generated(result, new CppBackend(), tests: true);
        Assert.Contains("::acme::billing::helpers::bounds(2.0, 3.0)", cpp);
    }
}
