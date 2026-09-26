using ProtoCross.Diagnostics;
using ProtoCross.Projects;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Reading a <c>.pcproj</c> (#106, spec 5.4): what it states, resolved against its own directory,
/// and every way it can fail, reported at the element or attribute that failed.
/// </summary>
public class ProjectFileTests
{
    private const string Wrapper = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ProtoCrossProject>\n{0}\n</ProtoCrossProject>\n";

    /// <summary>Writes <paramref name="xml"/> as <c>billing.pcproj</c> in a directory of its own.</summary>
    private static string WriteProject(string xml)
    {
        var path = Path.Combine(TestPaths.CreateTempDirectory(), "billing" + ProtoCrossProject.Extension);
        File.WriteAllText(path, xml);
        return path;
    }

    private static (ProtoCrossProject? Project, DiagnosticBag Diagnostics) Load(string xml)
    {
        var diagnostics = new DiagnosticBag();
        return (ProtoCrossProject.Load(WriteProject(xml), diagnostics), diagnostics);
    }

    private static (ProtoCrossProject? Project, DiagnosticBag Diagnostics) LoadBody(string body)
        => Load(string.Format(Wrapper, body));

    // ------- what a project states

    [Fact]
    public void AProjectStatingNothingIsAProjectOfNothing()
    {
        var (project, diagnostics) = LoadBody("");

        Assert.Empty(diagnostics);
        Assert.NotNull(project);
        Assert.Null(project.Config);
        Assert.Empty(project.Sources);
        Assert.Empty(project.Tests);
        Assert.Empty(project.ProtoPaths);
    }

    /// <summary>
    /// A path a project names is resolved against the project's own directory, not the directory
    /// whoever reads it happens to be in, so the project means the same thing wherever it is built from.
    /// </summary>
    [Fact]
    public void PathsAreResolvedAgainstTheProjectsDirectory()
    {
        var path = WriteProject(string.Format(Wrapper, "<Config>../shared/protocross.config.xml</Config>\n<ProtoPath>protos</ProtoPath>"));
        var directory = Path.GetDirectoryName(path)!;

        var project = ProtoCrossProject.Load(path, new DiagnosticBag());

        Assert.NotNull(project);
        Assert.Equal(Path.GetFullPath(Path.Combine(directory, "..", "shared", "protocross.config.xml")), project.Config?.Path);
        Assert.Equal(Path.Combine(directory, "protos"), Assert.Single(project.ProtoPaths).Path);
    }

    /// <summary>Schema directories keep the order they are written in, because the first that holds an import answers it.</summary>
    [Fact]
    public void ProtoPathsKeepTheOrderTheyAreWrittenIn()
    {
        string[] written = ["zeta", "alpha", "mid"];
        var (project, diagnostics) = LoadBody(string.Concat(written.Select(name => $"<ProtoPath>{name}</ProtoPath>\n")));

        Assert.Empty(diagnostics);
        Assert.Equal(written, project!.ProtoPaths.Select(protoPath => Path.GetFileName(protoPath.Path)));
    }

    /// <summary>An attribute lists several patterns the way MSBuild does, separated by semicolons, with space around them ignored.</summary>
    [Fact]
    public void PatternsAreSeparatedBySemicolons()
    {
        var (project, diagnostics) = LoadBody("<Sources Include=\" src/*.pcross ; lib/**/*.pcross;\" Exclude=\"src/old.pcross\" />");

        Assert.Empty(diagnostics);
        var item = Assert.Single(project!.Sources);
        Assert.Equal(["src/*.pcross", "lib/**/*.pcross"], item.Include);
        Assert.Equal(["src/old.pcross"], item.Exclude);
    }

    /// <summary>Several elements of a group are kept apart, each with the excludes written beside it.</summary>
    [Fact]
    public void EachElementOfAGroupKeepsItsOwnExcludes()
    {
        var (project, diagnostics) = LoadBody(
            "<Tests Include=\"tests/**/*.pcross\" Exclude=\"tests/slow/**\" />\n<Tests Include=\"tests/slow/smoke.pcross\" />");

        Assert.Empty(diagnostics);
        Assert.Equal([["tests/slow/**"], []], project!.Tests.Select(item => item.Exclude));
    }

    /// <summary>
    /// A project read twice from one unchanged file is equal to itself, which is what a caller asking
    /// whether anything changed needs to be true.
    /// </summary>
    [Fact]
    public void OneFileReadTwiceIsOneProject()
    {
        var path = WriteProject(string.Format(
            Wrapper,
            "<Config>protocross.config.xml</Config>\n<Sources Include=\"src/**/*.pcross\" Exclude=\"src/old/**\" />\n"
                + "<Tests Include=\"tests/**/*.pcross\" />\n<ProtoPath>protos</ProtoPath>"));

        Assert.Equal(ProtoCrossProject.Load(path, new DiagnosticBag()), ProtoCrossProject.Load(path, new DiagnosticBag()));
    }

    // ------- refusals

    /// <summary>
    /// Every way a project can state something it cannot mean is refused with its own code, at the
    /// element or attribute concerned, and the project is not returned: a project missing one of its
    /// lines would compile a program nobody wrote.
    /// </summary>
    /// <param name="body">What goes inside the root element.</param>
    /// <param name="code">The code the problem is reported under.</param>
    /// <param name="at">The text the diagnostic must point at: the name of the element or attribute concerned.</param>
    [Theory]
    [InlineData("<Output>generated</Output>", "PC2008", "Output")]
    [InlineData("<Sources Include=\"src/*.pcross\" Remove=\"src/old.pcross\" />", "PC2008", "Remove")]
    [InlineData("<Sources Include=\"src/*.pcross\"><Item /></Sources>", "PC2008", "Item")]
    [InlineData("<Config Path=\"x\">protocross.config.xml</Config>", "PC2008", "Path=")]
    [InlineData("<Sources />", "PC2009", "Sources")]
    [InlineData("<Tests Include=\" ; \" />", "PC2009", "Tests")]
    [InlineData("<Sources>src/*.pcross</Sources>", "PC2009", "Sources")]
    [InlineData("<Sources Include=\"/abs/*.pcross\" />", "PC2009", "Include")]
    [InlineData("<Config></Config>", "PC2009", "Config")]
    [InlineData("<ProtoPath>  </ProtoPath>", "PC2009", "ProtoPath")]
    [InlineData("<Config>a.xml</Config>\n<Config>b.xml</Config>", "PC2009", "Config>b")]
    public void ARefusedElementIsReportedWhereItStandsAndNoProjectIsReturned(string body, string code, string at)
    {
        var xml = string.Format(Wrapper, body);
        var diagnostics = new DiagnosticBag();

        var project = ProtoCrossProject.Load(WriteProject(xml), diagnostics);

        Assert.Null(project);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.True(
            diagnostic.Span.Start.Offset == xml.IndexOf(at, StringComparison.Ordinal),
            $"the diagnostic is at {diagnostic.Span.Line}:{diagnostic.Span.Column}, not at '{at}'");
    }

    /// <summary>
    /// <c>&lt;ProtocPath&gt;</c> is refused with the reason rather than as merely unknown: a project
    /// comes with a repository, and a repository does not get to choose what the machine runs.
    /// </summary>
    [Fact]
    public void ProtocIsNamedOutsideTheProjectAndTheRefusalSaysWhere()
    {
        var (_, diagnostics) = LoadBody("<ProtocPath>tools/protoc.exe</ProtocPath>");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.UnknownProjectElement.Code, diagnostic.Code);
        Assert.Contains("10.4.1", diagnostic.Help, StringComparison.Ordinal);
        Assert.Contains("protocross.protocPath", diagnostic.Help, StringComparison.Ordinal);
    }

    /// <summary>Policy written into a project is pointed at the file it belongs in, and the element that names that file.</summary>
    [Theory]
    [InlineData("Arithmetic")]
    [InlineData("Presence")]
    public void PolicyInAProjectPointsAtTheConfigurationFile(string section)
    {
        var (_, diagnostics) = LoadBody($"<{section} />");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.UnknownProjectElement.Code, diagnostic.Code);
        Assert.Contains("<Config>", diagnostic.Help, StringComparison.Ordinal);
    }

    /// <summary>A group written with its patterns as text is told the attribute to write them in, with them in it.</summary>
    [Fact]
    public void PatternsWrittenAsTextAreRefusedWithTheAttributeThatHoldsThem()
    {
        var (_, diagnostics) = LoadBody("<Sources>src/*.pcross</Sources>");

        Assert.Contains("<Sources Include=\"src/*.pcross\" />", Assert.Single(diagnostics).Help, StringComparison.Ordinal);
    }

    /// <summary>Every problem in a project is reported, not only the first, so one read lists everything to fix.</summary>
    [Fact]
    public void EveryRefusalInAProjectIsReported()
    {
        var (project, diagnostics) = LoadBody("<Output>x</Output>\n<Sources />\n<Tests Include=\"/abs/*.pcross\" />");

        Assert.Null(project);
        Assert.Equal(["PC2008", "PC2009", "PC2009"], diagnostics.Select(diagnostic => diagnostic.Code));
    }

    // ------- files that are not projects

    [Fact]
    public void AMissingFileIsReportedAsUnreadable()
    {
        var diagnostics = new DiagnosticBag();

        var project = ProtoCrossProject.Load(Path.Combine(TestPaths.CreateTempDirectory(), "missing.pcproj"), diagnostics);

        Assert.Null(project);
        Assert.Equal(DiagnosticCodes.ProjectCouldNotBeRead.Code, Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void MalformedXmlIsReportedWhereTheParserStopped()
    {
        var (project, diagnostics) = Load("<ProtoCrossProject>\n<Sources Include=\"x\">\n</ProtoCrossProject>\n");

        Assert.Null(project);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.ProjectCouldNotBeRead.Code, diagnostic.Code);
        Assert.True(diagnostic.Span.Line > 1, "a malformed project should report the line it went wrong on");
    }

    /// <summary>A path that cannot name a file is reported, not thrown: it came from a command line or an editor.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("bad\0name.pcproj")]
    public void APathThatCannotBeAFileIsReportedRatherThanThrown(string path)
    {
        var diagnostics = new DiagnosticBag();

        var project = ProtoCrossProject.Load(path, diagnostics);

        Assert.Null(project);
        Assert.Equal(DiagnosticCodes.ProjectCouldNotBeRead.Code, Assert.Single(diagnostics).Code);
    }

    /// <summary>
    /// An attribute in another vocabulary's namespace, such as the schema location an editor reads for
    /// completion, is not ProtoCross's to refuse.
    /// </summary>
    [Fact]
    public void AnAttributeInAnotherNamespaceIsLeftAlone()
    {
        var (project, diagnostics) = Load(
            "<ProtoCrossProject xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" "
                + "xsi:schemaLocation=\"urn:protocross pcproj.xsd\">\n<Sources Include=\"src/*.pcross\" />\n</ProtoCrossProject>\n");

        Assert.Empty(diagnostics);
        Assert.NotNull(project);
    }

    /// <summary>A configuration file handed over as a project is refused at its root, which names both.</summary>
    [Fact]
    public void AFileWithAnotherRootIsNotAProject()
    {
        var (project, diagnostics) = Load("<ProtoCross><Arithmetic /></ProtoCross>");

        Assert.Null(project);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.ProjectCouldNotBeRead.Code, diagnostic.Code);
        Assert.Contains("<ProtoCrossProject>", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("<ProtoCross>", diagnostic.Message, StringComparison.Ordinal);
    }
}
