using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// The command line building a project (#106, spec 5.4): the sources it names, the schema directories
/// it names, the configuration it settles, and the build <c>--test-out</c> asks for.
/// </summary>
public partial class CliTests
{
    /// <summary>
    /// A test source: a helper that calls into the program, and a test of it. It compiles only beside
    /// <see cref="Pricing"/> and <see cref="Discounts"/>.
    /// </summary>
    private const string Helpers = $$"""
        {{Import}}

        extend InvoiceItem {
            fn twice_net() -> int64 { return net() * 2; }
        }

        test InvoiceItem.twice_net "a helper calls into the program" {
            receiver {
                quantity = 2;
                unit_price_cents = 5;
            }

            expect return 18;
        }
        """;

    private const string SaturatingPolicy =
        "<ProtoCross><Arithmetic><Overflow>Saturating</Overflow></Arithmetic></ProtoCross>";

    /// <summary>A project file stating <paramref name="body"/>.</summary>
    private static string Project(string body) => $"<ProtoCrossProject>\n{body}\n</ProtoCrossProject>\n";

    /// <summary>
    /// Writes <c>billing.pcproj</c>, whose sources are under <c>src</c> and whose test sources are
    /// under <c>tests</c>, with <see cref="Pricing"/>, <see cref="Discounts"/> and
    /// <paramref name="helpers"/> in them.
    /// </summary>
    private static string WriteBillingProject(string helpers = Helpers)
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("billing.pcproj", Project("<Sources Include=\"src/**/*.pcross\" />\n<Tests Include=\"tests/**/*.pcross\" />")),
            ("src/pricing.pcross", Pricing),
            ("src/discounts.pcross", Discounts),
            ("tests/helpers.pcross", helpers));
        return directory;
    }

    /// <summary>Whether the build wrote <paramref name="relativePath"/> below <paramref name="directory"/>.</summary>
    private static bool Wrote(string directory, string relativePath)
        => File.Exists(Path.Combine(directory, relativePath));

    // ------- what a project compiles

    /// <summary>
    /// A project compiles the sources it names, and writes exactly what naming those sources on the
    /// command line writes: a project says what is compiled, not how.
    /// </summary>
    [Fact]
    public void AProjectBuildsWhatNamingItsSourcesBuilds()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("billing.pcproj", Project("<Sources Include=\"src/*.pcross\" />")),
            ("src/pricing.pcross", Pricing),
            ("src/discounts.pcross", Discounts));

        var fromProject = Run(directory, "billing.pcproj", "-o", "project");
        var fromSources = Run(directory, "src/discounts.pcross", "src/pricing.pcross", "-o", "sources");
        Assert.True(fromProject.ExitCode == 0, fromProject.Output);
        Assert.True(fromSources.ExitCode == 0, fromSources.Output);

        Assert.Equal(FilesUnder(Path.Combine(directory, "sources")), FilesUnder(Path.Combine(directory, "project")));
    }

    /// <summary>
    /// Without <c>--test-out</c>, a project's test sources are left out: one that does not compile
    /// does not stop the build, and nothing of it is written.
    /// </summary>
    [Fact]
    public void AProductionBuildLeavesTestSourcesOut()
    {
        var directory = WriteBillingProject(Helpers.Replace("net()", "missing()", StringComparison.Ordinal));

        var run = Run(directory, "billing.pcproj", "-o", "out");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.False(
            Directory.EnumerateFiles(Path.Combine(directory, "out"), "helpers*", SearchOption.AllDirectories).Any(),
            "a production build writes nothing of a test source");
    }

    /// <summary>
    /// With <c>--test-out</c>, a project's test sources compile with the program, and their behavior
    /// is written beside the tests rather than into the program (spec 25.3.1).
    /// </summary>
    [Fact]
    public void ATestBuildWritesTestSourcesBesideTheTests()
    {
        var directory = WriteBillingProject();

        var run = Run(directory, "billing.pcproj", "-t", "csharp", "-o", "out", "--test-out", "tests");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.True(Wrote(directory, "tests/csharp/helpers.g.cs"), "a test source's behavior goes beside the tests");
        Assert.True(Wrote(directory, "tests/csharp/helpers.tests.g.cs"), "a test source's tests go beside the others");
        Assert.False(Wrote(directory, "out/csharp/helpers.g.cs"), "a test source's behavior never goes into the program");
    }

    /// <summary>
    /// What a test build writes for the program is what a production build writes, file for file and
    /// byte for byte: building the tests never changes what ships.
    /// </summary>
    [Fact]
    public void ATestBuildWritesTheProgramAProductionBuildWrites()
    {
        var directory = WriteBillingProject();

        var production = Run(directory, "billing.pcproj", "-o", "production");
        var test = Run(directory, "billing.pcproj", "-o", "test", "--test-out", "tests");
        Assert.True(production.ExitCode == 0, production.Output);
        Assert.True(test.ExitCode == 0, test.Output);

        Assert.Equal(FilesUnder(Path.Combine(directory, "production")), FilesUnder(Path.Combine(directory, "test")));
    }

    /// <summary>
    /// A file both <c>&lt;Sources&gt;</c> and <c>&lt;Tests&gt;</c> name is compiled once, as part of
    /// the program, rather than twice or as a test source.
    /// </summary>
    [Fact]
    public void AFileBothGroupsNameIsPartOfTheProgram()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("billing.pcproj", Project("<Sources Include=\"src/*.pcross\" />\n<Tests Include=\"**/*.pcross\" />")),
            ("src/pricing.pcross", Pricing),
            ("src/discounts.pcross", Discounts),
            ("tests/helpers.pcross", Helpers));

        var run = Run(directory, "billing.pcproj", "-t", "csharp", "-o", "out", "--test-out", "tests");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.True(Wrote(directory, "out/csharp/pricing.g.cs"), "a source both groups name ships");
        Assert.False(Wrote(directory, "tests/csharp/pricing.g.cs"), "a source both groups name is not a test source");
    }

    // ------- schemas

    /// <summary>A schema only a project's <c>&lt;ProtoPath&gt;</c> holds, with a field of its own name.</summary>
    private static string Widget(string field)
        => $$"""
            syntax = "proto3";
            package widgets;
            message Widget { int64 {{field}} = 1; }
            """;

    private const string WidgetBehavior = """
        import proto "widget.proto";

        extend Widget {
            fn doubled() -> int64 { return size * 2; }
        }
        """;

    /// <summary>The schemas a project's sources import are searched for in its <c>&lt;ProtoPath&gt;</c> directories.</summary>
    [Fact]
    public void AProjectsProtoPathsAreSearched()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("billing.pcproj", Project("<Sources Include=\"src/*.pcross\" />\n<ProtoPath>schemas</ProtoPath>")),
            ("schemas/widget.proto", Widget("size")),
            ("src/widgets.pcross", WidgetBehavior));

        var run = Run(directory, "billing.pcproj", "-o", "out");

        Assert.True(run.ExitCode == 0, run.Output);
    }

    /// <summary>
    /// A project's <c>&lt;ProtoPath&gt;</c> directories are searched before <c>-I</c>'s, so a
    /// directory named for one run cannot change which schema the project's imports mean.
    /// </summary>
    [Fact]
    public void ProtoPathsAreSearchedBeforeIncludePaths()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("billing.pcproj", Project("<Sources Include=\"src/*.pcross\" />\n<ProtoPath>schemas</ProtoPath>")),
            ("schemas/widget.proto", Widget("size")),
            ("elsewhere/widget.proto", Widget("weight")),
            ("src/widgets.pcross", WidgetBehavior));

        var run = Run(directory, "billing.pcproj", "-I", "elsewhere", "-o", "out");

        Assert.True(run.ExitCode == 0, $"'size' is declared only by the project's copy of widget.proto:{Environment.NewLine}{run.Output}");
    }

    // ------- policy

    /// <summary>Whether <paramref name="source"/>'s generated C# was produced under checked overflow.</summary>
    private static bool GeneratedChecked(string directory, string source)
        => File.ReadAllText(Path.Combine(directory, "out", "csharp", source + ".g.cs"))
            .Contains("integer overflow = Checked", StringComparison.Ordinal);

    /// <summary>A project compiles under the configuration file its <c>&lt;Config&gt;</c> names.</summary>
    [Fact]
    public void AProjectCompilesUnderTheConfigItNames()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("billing.pcproj", Project("<Config>policy/strict.xml</Config>\n<Sources Include=\"src/*.pcross\" />")),
            ("policy/strict.xml", CheckedPolicy),
            ("src/pricing.pcross", Pricing));

        var run = Run(directory, "billing.pcproj", "-t", "csharp", "-o", "out");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.True(GeneratedChecked(directory, "pricing"), "pricing.pcross was not generated under the policy <Config> names");
    }

    /// <summary>
    /// Without <c>&lt;Config&gt;</c>, the search starts at the project's directory, so a source the
    /// project gathers from outside it compiles under the project's configuration file, which it
    /// would never find by searching from its own.
    /// </summary>
    [Fact]
    public void WithoutConfigTheSearchStartsAtTheProjectsDirectory()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("billing/billing.pcproj", Project("<Sources Include=\"../shared/*.pcross\" />")),
            ("billing/protocross.config.xml", CheckedPolicy),
            ("shared/pricing.pcross", Pricing));

        var run = Run(directory, "billing/billing.pcproj", "-t", "csharp", "-o", "out");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.True(GeneratedChecked(directory, "pricing"), "pricing.pcross was not generated under the project's configuration file");
    }

    /// <summary>
    /// Writes a project under a checked configuration file, one of whose sources is under a
    /// saturating one of its own, and builds it.
    /// </summary>
    private static (string Directory, ProcessResult Run) BuildWithAMemberUnderAnotherConfig()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("billing.pcproj", Project("<Sources Include=\"src/**/*.pcross\" />")),
            ("protocross.config.xml", CheckedPolicy),
            ("src/pricing.pcross", Pricing),
            ("src/legacy/protocross.config.xml", SaturatingPolicy),
            ("src/legacy/discounts.pcross", Discounts));

        return (directory, Run(directory, "billing.pcproj", "-t", "csharp", "-o", "out"));
    }

    /// <summary>
    /// A source whose own search finds another configuration file is warned about
    /// (<c>PC2011</c>), and the source beside it that finds the project's is not.
    /// </summary>
    [Fact]
    public void AMemberUnderAnotherConfigIsWarnedAbout()
    {
        var (_, run) = BuildWithAMemberUnderAnotherConfig();

        // A diagnostic renders its code on one line and the place it is reported at on the next.
        var lines = run.Output.ReplaceLineEndings("\n").Split('\n');
        var placed = lines
            .Select((line, index) => (line, index))
            .Where(entry => entry.line.StartsWith(DiagnosticCodes.MemberUnderAnotherConfig.Code + ":", StringComparison.Ordinal))
            .Select(entry => lines[entry.index + 1])
            .ToList();
        Assert.True(placed.Count == 1, run.Output);
        Assert.StartsWith("discounts.pcross", placed[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A source whose own search finds another configuration file still compiles under the
    /// project's: one compilation runs under one policy, and the project settles it.
    /// </summary>
    [Fact]
    public void AMemberUnderAnotherConfigCompilesUnderTheProjects()
    {
        var (directory, run) = BuildWithAMemberUnderAnotherConfig();

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.True(GeneratedChecked(directory, "discounts"), "discounts.pcross was generated under its own configuration file");
    }

    /// <summary>
    /// A source that finds no configuration file of its own is not warned about, although compiled on
    /// its own it would run under the defaults rather than the file the project names: it states no
    /// policy, so there is no other policy to have been passed over.
    /// </summary>
    [Fact]
    public void AMemberWithNoConfigOfItsOwnIsNotWarnedAbout()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("billing.pcproj", Project("<Config>policy/strict.xml</Config>\n<Sources Include=\"src/*.pcross\" />")),
            ("policy/strict.xml", CheckedPolicy),
            ("src/pricing.pcross", Pricing));

        var run = Run(directory, "billing.pcproj", "-t", "csharp", "-o", "out");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.DoesNotContain(DiagnosticCodes.MemberUnderAnotherConfig.Code, run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>&lt;Config&gt;</c> naming no file is refused (<c>PC2009</c>), rather than the project
    /// built under the defaults it did not ask for.
    /// </summary>
    [Fact]
    public void AConfigThatIsNotThereIsRefused()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("billing.pcproj", Project("<Config>policy/strict.xml</Config>\n<Sources Include=\"src/*.pcross\" />")),
            ("src/pricing.pcross", Pricing));

        var run = Run(directory, "billing.pcproj", "-o", "out");

        Assert.True(run.ExitCode == 2, run.Output);
        Assert.Contains(DiagnosticCodes.InvalidProjectSetting.Code, run.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(directory, "out")), "a refused project writes nothing");
    }

    /// <summary>
    /// A policy flag contradicting what a project's configuration file states is refused, as it is
    /// for any configuration file: the file wins (spec 10.4).
    /// </summary>
    [Fact]
    public void APolicyFlagCannotContradictAProjectsConfig()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("billing.pcproj", Project("<Sources Include=\"src/*.pcross\" />")),
            ("protocross.config.xml", CheckedPolicy),
            ("src/pricing.pcross", Pricing));

        var run = Run(directory, "billing.pcproj", "--arithmetic-overflow", "saturating", "-o", "out");

        Assert.True(run.ExitCode == 2, run.Output);
        Assert.Contains("--override-config", run.Output, StringComparison.Ordinal);
    }

    // ------- what the command line refuses

    /// <summary>
    /// <c>--config</c> and <c>--no-config</c> are refused beside a project, which settles its own
    /// configuration, rather than one of the two answers quietly winning.
    /// </summary>
    [Theory]
    [InlineData("--config", "protocross.config.xml")]
    [InlineData("--no-config", null)]
    public void AConfigFlagIsRefusedBesideAProject(string flag, string? value)
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("billing.pcproj", Project("<Sources Include=\"src/*.pcross\" />")),
            ("protocross.config.xml", CheckedPolicy),
            ("src/pricing.pcross", Pricing));

        var run = Run(directory, ["billing.pcproj", flag, .. value is null ? Array.Empty<string>() : [value], "-o", "out"]);

        Assert.True(run.ExitCode == 2, run.Output);
        Assert.Contains($"error: {flag} cannot be used with a project", run.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(directory, "out")), "a refused command writes nothing");
    }

    /// <summary>
    /// A source named beside a project is refused, rather than compiled with it or instead of it: a
    /// project names its own sources.
    /// </summary>
    [Fact]
    public void ASourceNamedBesideAProjectIsRefused()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("billing.pcproj", Project("<Sources Include=\"src/*.pcross\" />")),
            ("src/pricing.pcross", Pricing),
            ("discounts.pcross", Discounts));

        var run = Run(directory, "discounts.pcross", "billing.pcproj", "-o", "out");

        Assert.True(run.ExitCode == 2, run.Output);
        Assert.Contains("error: 'discounts.pcross' is named beside a project", run.Output, StringComparison.Ordinal);
    }

    /// <summary>Two projects are refused: one project is one compilation.</summary>
    [Fact]
    public void OneProjectIsBuiltAtATime()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("billing.pcproj", Project("<Sources Include=\"src/*.pcross\" />")),
            ("audit.pcproj", Project("<Sources Include=\"src/*.pcross\" />")),
            ("src/pricing.pcross", Pricing));

        var run = Run(directory, "billing.pcproj", "audit.pcproj", "-o", "out");

        Assert.True(run.ExitCode == 2, run.Output);
        Assert.Contains("error: a project is one compilation", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A project that states something it cannot mean writes nothing, rather than building what the
    /// rest of it says.
    /// </summary>
    [Fact]
    public void AProjectThatCannotBeReadWritesNothing()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("billing.pcproj", Project("<Sources Include=\"src/*.pcross\" />\n<Arithmetic />")),
            ("src/pricing.pcross", Pricing));

        var run = Run(directory, "billing.pcproj", "-o", "out");

        Assert.True(run.ExitCode == 2, run.Output);
        Assert.Contains(DiagnosticCodes.UnknownProjectElement.Code, run.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(directory, "out")), "a project that cannot be read writes nothing");
    }

    /// <summary>
    /// A project whose <c>&lt;Sources&gt;</c> find nothing is refused in either build, even with test
    /// sources to compile: a test build writes what the production build would, and the production
    /// build has nothing to write.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AProjectWithNoSourcesIsRefused(bool buildTests)
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("billing.pcproj", Project("<Sources Include=\"src/*.pcross\" />\n<Tests Include=\"tests/*.pcross\" />")),
            ("tests/pricing.pcross", Pricing));

        var run = Run(directory, ["billing.pcproj", "-o", "out", .. buildTests ? new[] { "--test-out", "tests-out" } : []]);

        Assert.True(run.ExitCode == 2, run.Output);
        Assert.Contains("error: billing.pcproj compiles nothing", run.Output, StringComparison.Ordinal);
    }
}
