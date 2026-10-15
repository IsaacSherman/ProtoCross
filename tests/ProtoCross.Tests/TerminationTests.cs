using ProtoCross.Config;
using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Whether generated code may end the program it runs in (spec 10.4, 19): <c>Failure/Termination</c> in
/// <c>protocross.config.xml</c>, and the error each construct that would end the program is where it says
/// <c>Forbidden</c> (<c>PC0144</c>).
/// </summary>
/// <remarks>
/// That a project forbidding termination builds and runs, refusing where it would have ended, is pinned in the
/// conformance corpus, <c>forbidden_termination/refusing_methods.pcross</c>. These cover what it cannot: every
/// construct the setting refuses, and the file contradicting itself.
/// </remarks>
public partial class TerminationTests
{
    private const string Schema =
        """
        syntax = "proto3";

        package acme.meters;

        import "legacy.proto";

        message Meter {
          int64 count = 1;
          int64 seconds = 2;
          int32 grade = 3;
          map<string, int64> prices = 4;
          string sku = 5;
        }

        message Refusal {
          string why = 1;
        }

        enum Grade {
          GRADE_NONE = 0;
          GRADE_HIGH = 1;
        }
        """;

    /// <summary>A proto2 schema, whose enum is closed, so a conversion to it with nothing said ends the program.</summary>
    private const string Legacy =
        """
        syntax = "proto2";

        package acme.meters;

        enum Level {
          LEVEL_LOW = 0;
          LEVEL_HIGH = 1;
        }
        """;

    private static readonly ProjectConfig Forbidden = ProjectConfig.Default with { Termination = TerminationPolicy.Forbidden };

    private static CompilationResult Compile(string body, ProjectConfig? config = null)
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "meters.proto"), Schema);
        File.WriteAllText(Path.Combine(directory, "legacy.proto"), Legacy);
        var path = Path.Combine(directory, "meters.pcross");
        File.WriteAllText(path, "import proto \"meters.proto\";\nimport proto \"legacy.proto\";\n\n" + body);

        return new Compilation(
            SourceDocument.ReadFrom(path),
            new CompilationOptions { IncludePaths = [directory], Config = config ?? Forbidden }).Compile();
    }

    private static string InMeter(string returnType, string statements, string refusal = "")
        => $"extend Meter {{\n    fn f() -> {returnType}{refusal} {{\n        {statements}\n    }}\n\n"
            + "    fn may_refuse() -> int64 refuses Refusal {\n        return 1;\n    }\n}\n";

    private static Diagnostic TheOnly(CompilationResult result, DiagnosticDescriptor expected)
    {
        var only = Assert.Single(result.Diagnostics);
        Assert.True(only.Code == expected.Code, $"expected {expected.Code}, got {only}");
        return only;
    }

    private static string FileWith(string body)
        => $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ProtoCross>\n{body}\n</ProtoCross>\n";

    private static (ProjectConfig? Config, DiagnosticBag Diagnostics) Load(string body)
    {
        var path = Path.Combine(TestPaths.CreateTempDirectory(), ProjectConfig.FileName);
        File.WriteAllText(path, FileWith(body));
        var diagnostics = new DiagnosticBag();
        return (ProjectConfig.Load(path, diagnostics), diagnostics);
    }

    // ------- the setting

    [Fact]
    public void TerminationIsAllowedUnlessTheFileSaysOtherwise()
    {
        Assert.Equal(TerminationPolicy.Allowed, ProjectConfig.Default.Termination);
        Assert.Equal(TerminationPolicy.Allowed, Load(string.Empty).Config!.Termination);
        Assert.Contains("Failure/Termination", ProjectConfig.Keys);
    }

    [Theory]
    [InlineData("Allowed", TerminationPolicy.Allowed)]
    [InlineData("Forbidden", TerminationPolicy.Forbidden)]
    public void EveryTerminationPolicyIsRead(string text, TerminationPolicy expected)
    {
        var (config, diagnostics) = Load($"<Failure><Termination>{text}</Termination></Failure>");

        Assert.Empty(diagnostics);
        Assert.Equal(expected, config!.Termination);
        Assert.Contains("Failure/Termination", config.ExplicitKeys);
    }

    [Fact]
    public void AnUnknownTerminationPolicyNamesTheLegalOnes()
    {
        var (config, diagnostics) = Load("<Failure><Termination>Never</Termination></Failure>");

        Assert.Null(config);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.UnknownConfigurationValue.Code, diagnostic.Code);
        Assert.Equal("Legal values: Allowed, Forbidden.", diagnostic.Help);
    }

    /// <summary>The setting governs what compiles, not what is written, so a generated file's header does not move.</summary>
    [Fact]
    public void TheHeaderDoesNotStateTheTerminationPolicy()
        => Assert.Equal(ProjectConfig.Default.DescribeForHeader(), Forbidden.DescribeForHeader());

    [Fact]
    public void CheckedOverflowContradictsForbiddenTermination()
    {
        const string Body = "<Arithmetic><Overflow>Checked</Overflow></Arithmetic><Failure><Termination>Forbidden</Termination></Failure>";
        var (config, diagnostics) = Load(Body);

        Assert.Null(config);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.SettingsContradictEachOther.Code, diagnostic.Code);
        Assert.Equal(
            "Arithmetic/Overflow = Checked ends the program on an overflow, which Failure/Termination = Forbidden forbids.",
            diagnostic.Message);
        Assert.Equal("Choose Wrapping or Saturating, or allow termination (spec 10.4).", diagnostic.Help);
        Assert.Equal(FileWith(Body).IndexOf("Overflow>Checked", StringComparison.Ordinal), diagnostic.Span.Start.Offset);
    }

    [Fact]
    public void AnEnumFallbackOfFailContradictsForbiddenTermination()
    {
        var (config, diagnostics) = Load(
            "<Failure><Termination>Forbidden</Termination></Failure>\n"
            + "<Enums><UnknownFallback Type=\"shop.Flower\">fail</UnknownFallback></Enums>");

        Assert.Null(config);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.SettingsContradictEachOther.Code, diagnostic.Code);
        Assert.Equal(
            "<UnknownFallback Type=\"shop.Flower\"> ends the program on a number the enum does not name, which "
            + "Failure/Termination = Forbidden forbids.",
            diagnostic.Message);
    }

    [Fact]
    public void AnEnumFallbackOfAValueDoesNotContradictForbiddenTermination()
    {
        var (config, diagnostics) = Load(
            "<Failure><Termination>Forbidden</Termination></Failure>\n"
            + "<Enums><UnknownFallback Type=\"shop.Flower\">PETUNIA</UnknownFallback></Enums>");

        Assert.Empty(diagnostics);
        Assert.Equal(TerminationPolicy.Forbidden, config!.Termination);
    }

    /// <summary>The override flag answers one setting in place of the file's, and would leave two that cannot both hold.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckedOverflowIsNotAppliedOverForbiddenTermination(bool allowOverride)
    {
        var refused = Forbidden with { Path = "protocross.config.xml" };

        Assert.False(refused.TryOverrideOverflow(OverflowPolicy.Checked, allowOverride, out var result, out var conflict));
        Assert.Same(refused, result);
        Assert.Equal(
            "--arithmetic-overflow checked ends the program on an overflow, which Failure/Termination = Forbidden in "
            + "protocross.config.xml forbids",
            conflict);
        Assert.True(refused.TryOverrideOverflow(OverflowPolicy.Saturating, allowOverride, out _, out _));
    }

    /// <summary>A configuration handed to a compilation rather than read from a file is held to the same rule.</summary>
    [Fact]
    public void ACompilationRefusesAConfigurationThatContradictsItself()
    {
        var result = Compile(InMeter("int64", "return count;"), Forbidden with { Overflow = OverflowPolicy.Checked });

        Assert.False(result.Success);
        Assert.Null(result.Module);
        Assert.Equal(DiagnosticCodes.SettingsContradictEachOther.Code, Assert.Single(result.Diagnostics).Code);
    }

    // ------- what the setting refuses

    [Theory]
    [InlineData("int64", "return count / seconds on_zero fail;", "'on_zero fail'")]
    [InlineData("Grade", "return grade as Grade on_unknown fail;", "'on_unknown fail'")]
    [InlineData("int64", "return prices[sku] on_missing fail;", "'on_missing fail'")]
    [InlineData("int64", "return may_refuse() on_refused fail;", "'on_refused fail'")]
    public void EveryFailClauseIsRefused(string returnType, string statements, string construct)
    {
        var diagnostic = TheOnly(Compile(InMeter(returnType, statements)), DiagnosticCodes.TerminationForbidden);

        Assert.Equal(
            $"{construct} ends the program, which Failure/Termination = Forbidden in protocross.config.xml forbids.",
            diagnostic.Message);
    }

    [Theory]
    [InlineData("return count / seconds on_zero fail;")]
    [InlineData("return grade as Grade on_unknown fail;")]
    [InlineData("return prices[sku] on_missing fail;")]
    [InlineData("return may_refuse() on_refused fail;")]
    public void AFailClauseIsNotRefusedWhereTerminationIsAllowed(string statements)
    {
        var returnType = statements.Contains("Grade", StringComparison.Ordinal) ? "Grade" : "int64";

        Assert.DoesNotContain(
            Compile(InMeter(returnType, statements), ProjectConfig.Default).Diagnostics,
            diagnostic => diagnostic.Code == DiagnosticCodes.TerminationForbidden.Code);
    }

    /// <summary>Help offers a fallback and a refusal, as the method may write one.</summary>
    [Theory]
    [InlineData("", "Write 'on_zero' and a 'int64' to use instead, or declare what 'f' refuses with and write 'on_zero refuse' and one (spec 10.4, 16.3).")]
    [InlineData(" refuses Refusal", "Write 'on_zero' and a 'int64' to use instead, or 'on_zero refuse' and a 'acme.meters.Refusal' to refuse (spec 10.4, 16.3).")]
    public void AFailClauseIsToldWhatToWriteInstead(string refusal, string help)
        => Assert.Equal(
            help,
            TheOnly(Compile(InMeter("int64", "return count / seconds on_zero fail;", refusal)), DiagnosticCodes.TerminationForbidden).Help);

    /// <summary>A call that gives nothing has no fallback, so passing the refusal on is the only answer left.</summary>
    [Fact]
    public void AVoidCallIsToldToPassItsRefusalOn()
    {
        var diagnostic = TheOnly(
            Compile("extend Meter {\n    fn f() -> int64 {\n        check() on_refused fail;\n        return 1;\n    }\n\n"
                + "    fn check() refuses Refusal {\n        return;\n    }\n}\n"),
            DiagnosticCodes.TerminationForbidden);

        Assert.Equal(
            "Write 'on_refused refuse', declaring that 'f' refuses with 'acme.meters.Refusal' (spec 10.4, 16.3).",
            diagnostic.Help);
    }

    /// <summary>Where nothing but passing the refusal on is left, the help says so rather than offering nothing.</summary>
    [Fact]
    public void AVoidCallInAMethodRefusingWithAnotherTypeIsToldToRefuseWithItsType()
    {
        var diagnostic = TheOnly(
            Compile("extend Meter {\n    fn f() -> int64 refuses string {\n        check() on_refused fail;\n        return 1;\n    }\n\n"
                + "    fn check() refuses Refusal {\n        return;\n    }\n}\n"),
            DiagnosticCodes.TerminationForbidden);

        Assert.Equal(
            "Write 'on_refused refuse', with 'f' refusing with 'acme.meters.Refusal' rather than 'string' (spec 10.4, 16.3).",
            diagnostic.Help);
    }

    /// <summary>A closed enum's default ends the program unseen, so where termination is forbidden it is refused in place of the warning.</summary>
    [Fact]
    public void AClosedEnumsDefaultIsRefused()
    {
        var diagnostic = TheOnly(Compile(InMeter("Level", "return grade as Level;")), DiagnosticCodes.TerminationForbidden);

        Assert.Equal(
            "A number 'acme.meters.Level' does not name ends the program, which Failure/Termination = Forbidden in "
            + "protocross.config.xml forbids.",
            diagnostic.Message);
        Assert.StartsWith("Write 'on_unknown Level.LEVEL_LOW' to use a value instead, or ", diagnostic.Help, StringComparison.Ordinal);
    }

    [Fact]
    public void AClosedEnumsDefaultIsAWarningWhereTerminationIsAllowed()
        => TheOnly(Compile(InMeter("Level", "return grade as Level;"), ProjectConfig.Default), DiagnosticCodes.ClosedEnumConversionHasNoFallback);

    /// <summary>Where termination is forbidden, a missing clause is not told to write the one that would be refused.</summary>
    [Theory]
    [InlineData("int64", "return count / seconds;", "Write 'on_zero' and a 'int64' to use instead, or declare what 'f' refuses with and write 'on_zero refuse' and one after '/ <divisor>'.")]
    [InlineData("int64", "return prices[sku];", "Write 'on_missing' and a 'int64' to use instead, or declare what 'f' refuses with and write 'on_missing refuse' and one: 'prices[…] on_missing …' (spec 14.2).")]
    public void AMissingClauseIsNotToldToFail(string returnType, string statements, string help)
        => Assert.Equal(help, Assert.Single(Compile(InMeter(returnType, statements)).Diagnostics).Help);

    [Fact]
    public void AMethodThatRefusesInsteadCompiles()
        => Assert.True(
            Compile(InMeter("int64", "return count / seconds on_zero refuse new Refusal { why: \"no time\" };", " refuses Refusal")).Success);
}
