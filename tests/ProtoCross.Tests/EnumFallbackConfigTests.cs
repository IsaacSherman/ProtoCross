using ProtoCross.Config;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// <c>&lt;Enums&gt;</c> in <c>protocross.config.xml</c>: what a conversion to an enum makes of a
/// number the enum does not name, said once for the project (spec 10.4, 12.1).
/// </summary>
/// <remarks>
/// The file alone can tell whether a setting names an enum and a value, and the binder, which has the
/// schemas, whether the enum and the value exist. Both halves are here, and so is what a resolved
/// setting changes: the conversion's behavior, and what is no longer said about it.
/// </remarks>
public class EnumFallbackConfigTests
{
    private const string Wrapper =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ProtoCross>\n{0}\n</ProtoCross>\n";

    private const string Prelude =
        "import proto \"fixtures.proto\";\nimport proto \"enum_openness_proto2.proto\";\n";

    /// <summary>A directory holding a configuration file with <paramref name="enums"/> as its <c>&lt;Enums&gt;</c>.</summary>
    private static string DirectoryWith(string enums)
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(
            Path.Combine(directory, ProjectConfig.FileName),
            string.Format(Wrapper, "<Enums>\n" + enums + "\n</Enums>"));
        return directory;
    }

    private static (ProjectConfig? Config, DiagnosticBag Diagnostics) Load(string enums)
    {
        var diagnostics = new DiagnosticBag();
        var config = ProjectConfig.Load(Path.Combine(DirectoryWith(enums), ProjectConfig.FileName), diagnostics);
        return (config, diagnostics);
    }

    /// <summary>Compiles <paramref name="extend"/> beside a configuration file whose <c>&lt;Enums&gt;</c> is <paramref name="enums"/>.</summary>
    private static CompilationResult CompileUnder(string enums, string extend)
    {
        var source = Path.Combine(DirectoryWith(enums), "test.pcross");
        File.WriteAllText(source, Prelude + extend);
        return Compilation.Compile(source, [TestPaths.FixtureProtoDirectory]);
    }

    private static string Describe(CompilationResult result)
        => string.Join("\n", result.Diagnostics.Select(d => d.ToString()));

    private static IrNumberToEnum SingleConversion(CompilationResult result)
        => Assert.Single(IrWalk.DescendantsAndSelf(result.Module!).OfType<IrNumberToEnum>());

    private const string ConvertsStatus =
        "extend Outer { fn f() -> TopLevelStatus { return small_count as TopLevelStatus; } }";

    // ------- reading the file

    [Fact]
    public void EachSettingIsReadAsWrittenInTheOrderWritten()
    {
        var (config, diagnostics) = Load(
            "<UnknownFallback Type=\"protocross.tests.TopLevelStatus\">TOP_LEVEL_STATUS_OK</UnknownFallback>\n"
            + "<UnknownFallback Type=\"Proto2Status\"> fail </UnknownFallback>");

        Assert.Empty(diagnostics);
        Assert.Equal(
            [("protocross.tests.TopLevelStatus", "TOP_LEVEL_STATUS_OK"), ("Proto2Status", "fail")],
            config!.EnumFallbacks.Select(setting => (setting.Type, setting.Value)));
    }

    [Theory]
    [InlineData("<UnknownFallback>TOP_LEVEL_STATUS_OK</UnknownFallback>")]
    [InlineData("<UnknownFallback Type=\" \">TOP_LEVEL_STATUS_OK</UnknownFallback>")]
    [InlineData("<UnknownFallback Type=\"TopLevelStatus\"></UnknownFallback>")]
    [InlineData("<UnknownFallback Type=\"TopLevelStatus\" Value=\"TOP_LEVEL_STATUS_OK\"/>")]
    [InlineData("<UnknownFallback Type=\"shop..Flower\">PETUNIA</UnknownFallback>")]
    [InlineData("<UnknownFallback Type=\"Top Level\">PETUNIA</UnknownFallback>")]
    [InlineData("<UnknownFallback Type=\"TopLevelStatus\">TOP LEVEL</UnknownFallback>")]
    [InlineData("<UnknownFallback Type=\"TopLevelStatus\">a.b</UnknownFallback>")]
    public void ASettingThatIsNotAnEnumAndAValueIsRefusedAndTheFileWithIt(string setting)
    {
        var (config, diagnostics) = Load(setting);

        Assert.Null(config);
        Assert.Equal(DiagnosticCodes.InvalidEnumFallback.Code, Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void OneEnumNamedTwiceIsAQuestionAnsweredTwice()
    {
        var (config, diagnostics) = Load(
            "<UnknownFallback Type=\"TopLevelStatus\">fail</UnknownFallback>\n"
            + "<UnknownFallback Type=\"TopLevelStatus\">TOP_LEVEL_STATUS_OK</UnknownFallback>");

        Assert.Null(config);
        Assert.Equal(DiagnosticCodes.DuplicateConfigurationSetting.Code, Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void AnythingElseInEnumsIsAnUnknownElementWhoseHelpNamesTheSetting()
    {
        var (config, diagnostics) = Load("<Fallback Type=\"TopLevelStatus\">fail</Fallback>");

        Assert.Null(config);
        var error = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.UnknownConfigurationElement.Code, error.Code);
        Assert.Contains("UnknownFallback", error.Help, StringComparison.Ordinal);
    }

    /// <summary>
    /// A name goes into every generated file's header as written, so a line break written as a
    /// character reference, which XML keeps where it would turn a literal one into a space, is refused
    /// before it can end a comment early. The enum need not be one any compilation loads.
    /// </summary>
    [Theory]
    [InlineData("<UnknownFallback Type=\"elsewhere.Flower&#10;int x;\">PETUNIA</UnknownFallback>")]
    [InlineData("<UnknownFallback Type=\"elsewhere.Flower\">PETUNIA&#10;int x;</UnknownFallback>")]
    public void ALineBreakInANameIsRefusedBeforeItReachesAHeader(string setting)
    {
        var (config, diagnostics) = Load(setting);

        Assert.Null(config);
        Assert.Equal(DiagnosticCodes.InvalidEnumFallback.Code, Assert.Single(diagnostics).Code);
    }

    /// <summary>
    /// A language server recompiles only when a configuration changes, and asks by equality, so two
    /// reads of one file must be equal and an edited setting must not be.
    /// </summary>
    [Fact]
    public void TwoReadsOfOneFileAreEqualAndAnEditedSettingIsNot()
    {
        var path = Path.Combine(
            DirectoryWith("<UnknownFallback Type=\"TopLevelStatus\">fail</UnknownFallback>"),
            ProjectConfig.FileName);

        var first = ProjectConfig.Load(path, new DiagnosticBag());
        var second = ProjectConfig.Load(path, new DiagnosticBag());
        File.WriteAllText(
            path,
            string.Format(Wrapper, "<Enums><UnknownFallback Type=\"TopLevelStatus\">TOP_LEVEL_STATUS_OK</UnknownFallback></Enums>"));
        var edited = ProjectConfig.Load(path, new DiagnosticBag());

        Assert.Equal(first, second);
        Assert.NotEqual(first, edited);
    }

    /// <summary>
    /// A generated file says what produced it, so each setting is a line of its header, and a project
    /// with none keeps the header it had.
    /// </summary>
    [Fact]
    public void TheHeaderGainsALineForEachSettingAndNoneWithoutThem()
    {
        var (config, _) = Load(
            "<UnknownFallback Type=\"TopLevelStatus\">TOP_LEVEL_STATUS_OK</UnknownFallback>\n"
            + "<UnknownFallback Type=\"Proto2Status\">fail</UnknownFallback>");

        var header = config!.DescribeForHeader();

        Assert.Equal(ProjectConfig.Default.DescribeForHeader(), header.Take(ProjectConfig.Default.DescribeForHeader().Count));
        Assert.Equal(
            [
                "A number TopLevelStatus does not name becomes TOP_LEVEL_STATUS_OK (spec 12.1).",
                "A number Proto2Status does not name ends the program (spec 12.1).",
            ],
            header.Skip(ProjectConfig.Default.DescribeForHeader().Count));
    }

    // ------- what a setting does

    [Fact]
    public void ANamedValueIsWhatANumberWithNoNameBecomesAndNothingIsSaid()
    {
        var result = CompileUnder(
            "<UnknownFallback Type=\"TopLevelStatus\">TOP_LEVEL_STATUS_OK</UnknownFallback>",
            ConvertsStatus);

        Assert.Empty(result.Diagnostics);
        var conversion = SingleConversion(result);
        Assert.Equal(UnnamedNumberBehavior.Fallback, conversion.OnUnnamed);
        Assert.Equal(UnnamedNumberSource.Configuration, conversion.Source);
        Assert.Equal("TOP_LEVEL_STATUS_OK", conversion.ConfiguredFallback?.Name);
        Assert.Null(conversion.Fallback);
    }

    /// <summary>A closed enum that the project speaks for is no longer warned about.</summary>
    [Fact]
    public void FailForAClosedEnumIsSaidOnceAndNotWarnedAbout()
    {
        var result = CompileUnder(
            "<UnknownFallback Type=\"protocross.tests.openness.Proto2Status\">fail</UnknownFallback>",
            "extend Proto2Holder { fn f() -> Proto2Status { return number as Proto2Status; } }");

        Assert.Empty(result.Diagnostics);
        var conversion = SingleConversion(result);
        Assert.Equal(UnnamedNumberBehavior.Fail, conversion.OnUnnamed);
        Assert.Equal(UnnamedNumberSource.Configuration, conversion.Source);
    }

    /// <summary>The line is nearer than the project, so a clause written on it wins.</summary>
    [Fact]
    public void AClauseOnTheConversionStillDecides()
    {
        var result = CompileUnder(
            "<UnknownFallback Type=\"TopLevelStatus\">TOP_LEVEL_STATUS_OK</UnknownFallback>",
            "extend Outer { fn f() -> TopLevelStatus { return small_count as TopLevelStatus on_unknown fail; } }");

        Assert.Empty(result.Diagnostics);
        var conversion = SingleConversion(result);
        Assert.Equal(UnnamedNumberBehavior.Fail, conversion.OnUnnamed);
        Assert.Equal(UnnamedNumberSource.Clause, conversion.Source);
    }

    /// <summary>
    /// Every source under a configuration file shares it and imports its own schemas, so a setting
    /// about an enum this compilation never loaded is not a mistake in the file.
    /// </summary>
    [Fact]
    public void ASettingAboutAnEnumNotLoadedIsPassedOver()
    {
        var result = CompileUnder(
            "<UnknownFallback Type=\"elsewhere.Flower\">PETUNIA</UnknownFallback>",
            ConvertsStatus);

        Assert.Equal(DiagnosticCodes.UnnamedNumberIsKept.Code, Assert.Single(result.Diagnostics).Code);
    }

    // ------- what is wrong with a setting, reported where it is written

    [Theory]
    [InlineData("<UnknownFallback Type=\"TopLevelStatus\">TOP_LEVEL_STATUS_SOMETIMES</UnknownFallback>")]
    [InlineData("<UnknownFallback Type=\"TopLevelStatus\">top_level_status_ok</UnknownFallback>")]
    [InlineData("<UnknownFallback Type=\"Outer\">fail</UnknownFallback>")]
    [InlineData("<UnknownFallback Type=\"Kind\">fail</UnknownFallback>")]
    public void ASettingThatResolvesToNoEnumAndValueIsRefusedInTheFile(string setting)
    {
        var result = CompileUnder(setting, "import proto \"ambiguous_enums.proto\";\n" + ConvertsStatus);

        var error = Assert.Single(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(DiagnosticCodes.InvalidEnumFallback.Code, error.Code);
        Assert.Equal(ProjectConfig.FileName, error.Span.File);
    }

    /// <summary>
    /// Two spellings of one enum cannot be told apart in the file, which is read before the schemas
    /// are, so they are caught where both resolve to the one enum.
    /// </summary>
    [Fact]
    public void OneEnumNamedTwoWaysIsAQuestionAnsweredTwice()
    {
        var result = CompileUnder(
            "<UnknownFallback Type=\"TopLevelStatus\">fail</UnknownFallback>\n"
            + "<UnknownFallback Type=\"protocross.tests.TopLevelStatus\">TOP_LEVEL_STATUS_OK</UnknownFallback>",
            ConvertsStatus);

        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.DuplicateConfigurationSetting.Code, error.Code);
        Assert.Equal(ProjectConfig.FileName, error.Span.File);
    }

    /// <summary>The value's span is where the value is written, not the start of the element.</summary>
    [Fact]
    public void AValueTheEnumLacksIsReportedWhereTheValueIsWritten()
    {
        const string setting = "<UnknownFallback Type=\"TopLevelStatus\">TOP_LEVEL_STATUS_SOMETIMES</UnknownFallback>";
        var directory = DirectoryWith(setting);
        var text = File.ReadAllText(Path.Combine(directory, ProjectConfig.FileName));
        var source = Path.Combine(directory, "test.pcross");
        File.WriteAllText(source, Prelude + ConvertsStatus);

        var error = Assert.Single(
            Compilation.Compile(source, [TestPaths.FixtureProtoDirectory]).Diagnostics,
            d => d.Code == DiagnosticCodes.InvalidEnumFallback.Code);

        Assert.Equal(text.IndexOf("TOP_LEVEL_STATUS_SOMETIMES", StringComparison.Ordinal), error.Span.Start.Offset);
    }
}
