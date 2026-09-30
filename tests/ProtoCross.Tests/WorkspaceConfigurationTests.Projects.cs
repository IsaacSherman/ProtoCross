using ProtoCross.Config;
using ProtoCross.Diagnostics;
using ProtoCross.LanguageServer.Workspace;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// A document's settings resolved through its project (#106, spec 10.4.1): the project first in the
/// order, its <c>&lt;ProtoPath&gt;</c> directories ahead of every editor include path, its policy ahead
/// of <c>protocross.configPath</c>, and a project that cannot be read refusing the document.
/// </summary>
public partial class WorkspaceConfigurationTests
{
    private const string SaturatingOverflow = """
        <ProtoCross><Arithmetic><Overflow>Saturating</Overflow></Arithmetic></ProtoCross>
        """;

    /// <summary>Writes a project stating <paramref name="body"/> at <paramref name="name"/> below <paramref name="directory"/>.</summary>
    private static string WriteProject(string directory, string name, string body)
        => TestPaths.WriteSources(directory, (name, $"<ProtoCrossProject>\n{body}\n</ProtoCrossProject>\n"))[0];

    /// <summary>
    /// A source <paramref name="name"/> in <paramref name="directory"/>, written to disk as an empty file.
    /// </summary>
    /// <remarks>
    /// On disk, and not only named, because a project is built from what its patterns find there: a
    /// project whose only source has never been written compiles nothing, and is refused (spec 5.4).
    /// </remarks>
    private static DocumentUri Member(string directory, string name = "source.pcross")
    {
        TestPaths.WriteSources(directory, (name, string.Empty));
        return Document(directory, name);
    }

    /// <summary>A directory below <paramref name="directory"/>, created.</summary>
    private static string Subdirectory(string directory, string name)
        => Directory.CreateDirectory(Path.Combine(directory, name)).FullName;

    // ---------------------------------------------------------------- the project's place in the order

    /// <summary>
    /// A project's <c>&lt;ProtoPath&gt;</c> directories come before every editor include path, as they
    /// come before <c>-I</c> on the command line, and each says it came from the project.
    /// </summary>
    [Fact]
    public void AProjectsProtoPathsComeBeforeEveryEditorIncludePath()
    {
        var directory = TempDirectory();
        var projectSchemas = Subdirectory(directory, "schemas");
        var folderSchemas = Subdirectory(directory, "folder-schemas");
        var userSchemas = Subdirectory(directory, "user-schemas");
        WriteProject(directory, "billing.pcproj", "<Sources Include=\"*.pcross\" />\n<ProtoPath>schemas</ProtoPath>");

        var resolved = Workspace(WorkspaceFolder.FromPath(directory, settings: new ProtoCrossSettings { IncludePaths = [folderSchemas] }))
            .WithUserSettings(new ProtoCrossSettings { IncludePaths = [userSchemas] })
            .Resolve(Member(directory));

        Assert.Equal([projectSchemas, folderSchemas, userSchemas], resolved.IncludePaths.Select(include => include.Path));
        Assert.Equal(
            [ConfigurationSource.Project, ConfigurationSource.FolderSetting, ConfigurationSource.UserSetting],
            resolved.IncludePaths.Select(include => include.Source));
    }

    /// <summary>
    /// The policy a project settles beats <c>protocross.configPath</c>, so that the editor and a build
    /// of the project run one policy.
    /// </summary>
    [Fact]
    public void AProjectsPolicyBeatsTheConfigPathSetting()
    {
        var directory = TempDirectory();
        var projectPolicy = TempFile(directory, "strict.xml", CheckedOverflow);
        var settingPolicy = TempFile(directory, "loose.xml", SaturatingOverflow);
        WriteProject(directory, "billing.pcproj", "<Config>strict.xml</Config>\n<Sources Include=\"*.pcross\" />");

        var resolved = Workspace(WorkspaceFolder.FromPath(directory, settings: new ProtoCrossSettings { ConfigPath = settingPolicy }))
            .Resolve(Member(directory));

        Assert.Equal(OverflowPolicy.Checked, resolved.Config?.Overflow);
        Assert.Equal(projectPolicy, resolved.ConfigPath);
        Assert.Equal(ConfigurationSource.Project, resolved.ConfigSource);
    }

    /// <summary>
    /// A project that names no configuration file searches from its own directory, so a source it
    /// gathers from outside that directory compiles under the project's file, which its own search
    /// would never find.
    /// </summary>
    /// <remarks>
    /// The project is named by the setting because it is beside the source rather than above it, and
    /// the search for a document's project only ever goes up.
    /// </remarks>
    [Fact]
    public void AProjectWithoutConfigSearchesFromItsOwnDirectory()
    {
        var directory = TempDirectory();
        var billing = Subdirectory(directory, "billing");
        var shared = Subdirectory(directory, "shared");
        TempFile(billing, ProjectConfig.FileName, CheckedOverflow);
        WriteProject(billing, "billing.pcproj", "<Sources Include=\"../shared/*.pcross\" />");

        var resolved = Workspace(WorkspaceFolder.FromPath(directory, settings: new ProtoCrossSettings { ProjectPath = "billing/billing.pcproj" }))
            .Resolve(Member(shared));

        Assert.Equal(OverflowPolicy.Checked, resolved.Config?.Overflow);
        Assert.Empty(resolved.Diagnostics);
    }

    /// <summary>
    /// A member whose own search finds a different configuration file is warned about (<c>PC2011</c>),
    /// and still compiles under the project's.
    /// </summary>
    [Fact]
    public void AMemberUnderAnotherConfigIsWarnedAboutAndCompilesUnderTheProjects()
    {
        var directory = TempDirectory();
        var legacy = Subdirectory(directory, "legacy");
        TempFile(directory, ProjectConfig.FileName, CheckedOverflow);
        TempFile(legacy, ProjectConfig.FileName, SaturatingOverflow);
        WriteProject(directory, "billing.pcproj", "<Sources Include=\"**/*.pcross\" />");

        var resolved = Workspace(WorkspaceFolder.FromPath(directory)).Resolve(Member(legacy));

        Assert.Equal(OverflowPolicy.Checked, resolved.Config?.Overflow);
        Assert.Contains(resolved.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.MemberUnderAnotherConfig.Code);
    }

    /// <summary>
    /// A document no project includes resolves exactly as it did before projects existed, though a
    /// project sits beside it.
    /// </summary>
    [Fact]
    public void ADocumentNoProjectIncludesResolvesAsItAlwaysHas()
    {
        var directory = TempDirectory();
        Subdirectory(directory, "schemas");
        TempFile(directory, ProjectConfig.FileName, CheckedOverflow);
        WriteProject(directory, "billing.pcproj", "<Config>missing.xml</Config>\n<Sources Include=\"src/*.pcross\" />\n<ProtoPath>schemas</ProtoPath>");
        var workspace = Workspace(WorkspaceFolder.FromPath(directory));

        var resolved = workspace.Resolve(Member(directory, "scratch.pcross"));

        Assert.Null(resolved.ProjectPath);
        Assert.Empty(resolved.IncludePaths);
        Assert.Equal(ConfigurationSource.ConfigFile, resolved.ConfigSource);
        Assert.Equal(OverflowPolicy.Checked, resolved.Config?.Overflow);
        Assert.Empty(resolved.Diagnostics);
    }

    // ---------------------------------------------------------------- which project

    /// <summary>
    /// Two projects in one directory that include a document are warned about (<c>PC2108</c>), and the
    /// one whose name sorts first is the document's project.
    /// </summary>
    [Fact]
    public void TwoProjectsIncludingADocumentAreWarnedAboutAndTheFirstByNameWins()
    {
        var directory = TempDirectory();
        var first = WriteProject(directory, "audit.pcproj", "<Sources Include=\"*.pcross\" />");
        WriteProject(directory, "billing.pcproj", "<Sources Include=\"*.pcross\" />");

        var resolved = Workspace(WorkspaceFolder.FromPath(directory)).Resolve(Member(directory));

        Assert.Equal(first, resolved.ProjectPath);
        var warning = Assert.Single(resolved.Diagnostics, diagnostic => diagnostic.Code == HostDiagnosticCodes.ProjectsShareADocument.Code);
        Assert.Contains("billing.pcproj", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>The project <c>protocross.project</c> names is used instead of the nearest one.</summary>
    [Fact]
    public void ANamedProjectIsUsedInsteadOfTheNearest()
    {
        var directory = TempDirectory();
        var src = Subdirectory(directory, "src");
        var named = Path.GetFullPath(WriteProject(directory, "build/release.pcproj", "<Sources Include=\"../src/*.pcross\" />"));
        WriteProject(src, "local.pcproj", "<Sources Include=\"*.pcross\" />");

        var resolved = Workspace(WorkspaceFolder.FromPath(directory, settings: new ProtoCrossSettings { ProjectPath = "build/release.pcproj" }))
            .Resolve(Member(src));

        Assert.Equal(named, resolved.ProjectPath);
        Assert.Equal(ConfigurationSource.FolderSetting, resolved.ProjectSource);
    }

    /// <summary>
    /// A document the named project does not include has no project, although a nearer project
    /// includes it: the setting replaces the search rather than being preferred by it.
    /// </summary>
    [Fact]
    public void ADocumentTheNamedProjectDoesNotIncludeHasNoProject()
    {
        var directory = TempDirectory();
        var scratch = Subdirectory(directory, "scratch");
        WriteProject(directory, "build/release.pcproj", "<Sources Include=\"../src/*.pcross\" />");
        WriteProject(scratch, "local.pcproj", "<Sources Include=\"*.pcross\" />");

        var resolved = Workspace(WorkspaceFolder.FromPath(directory, settings: new ProtoCrossSettings { ProjectPath = "build/release.pcproj" }))
            .Resolve(Member(scratch));

        Assert.Null(resolved.ProjectPath);
        Assert.Contains(
            resolved.Describe(),
            fact => fact.Setting == "project" && fact.Value.Contains("does not include", StringComparison.Ordinal));
    }

    /// <summary>
    /// A named project file that is not there is warned about (<c>PC2110</c>), and the document's
    /// project is found by searching, as if nothing were named.
    /// </summary>
    [Fact]
    public void ANamedProjectThatIsNotThereIsWarnedAboutAndTheSearchRuns()
    {
        var directory = TempDirectory();
        var nearest = WriteProject(directory, "billing.pcproj", "<Sources Include=\"*.pcross\" />");

        var resolved = Workspace(WorkspaceFolder.FromPath(directory, settings: new ProtoCrossSettings { ProjectPath = "gone.pcproj" }))
            .Resolve(Member(directory));

        Assert.Equal(nearest, resolved.ProjectPath);
        Assert.Contains(resolved.Diagnostics, diagnostic => diagnostic.Code == HostDiagnosticCodes.ProjectNotFound.Code);
    }

    /// <summary>A buffer never saved has no path for a project's patterns to include, and so has no project.</summary>
    [Fact]
    public void AnUnsavedBufferHasNoProject()
    {
        var directory = TempDirectory();
        Subdirectory(directory, "schemas");
        WriteProject(directory, "billing.pcproj", "<Sources Include=\"**\" />\n<ProtoPath>schemas</ProtoPath>");

        var resolved = Workspace(WorkspaceFolder.FromPath(directory)).Resolve(DocumentUri.Parse("untitled:Untitled-1"));

        Assert.Null(resolved.ProjectPath);
        Assert.Empty(resolved.IncludePaths);
    }

    // ---------------------------------------------------------------- a project that cannot be read

    /// <summary>
    /// A project that cannot be read refuses the document (<c>PC2109</c>, an error), and the problem
    /// found in it is carried, placed in the project file, for the editor to publish there.
    /// </summary>
    [Fact]
    public void AProjectThatCannotBeReadRefusesTheDocument()
    {
        var directory = TempDirectory();
        WriteProject(directory, "billing.pcproj", "<Sources Include=\"*.pcross\" />\n<Arithmetic />");

        var resolved = Workspace(WorkspaceFolder.FromPath(directory)).Resolve(Member(directory));

        Assert.True(resolved.ProjectRefused);
        Assert.False(resolved.IsUsable, "a document whose project cannot be read must not be compiled");
        Assert.False(resolved.TryCreateCompilationOptions(loader: null, out _));
        var refusal = Assert.Single(resolved.Diagnostics, diagnostic => diagnostic.Code == HostDiagnosticCodes.ProjectRefused.Code);
        Assert.Equal(DiagnosticSeverity.Error, refusal.Severity);
        Assert.Contains(
            resolved.Diagnostics,
            diagnostic => diagnostic.Code == DiagnosticCodes.UnknownProjectElement.Code && diagnostic.Span.File == "billing.pcproj");
    }

    /// <summary>
    /// A configuration file the project names that is not there refuses the document, rather than
    /// letting it compile under the defaults the project did not ask for.
    /// </summary>
    [Fact]
    public void AConfigTheProjectNamesThatIsNotThereRefusesTheDocument()
    {
        var directory = TempDirectory();
        WriteProject(directory, "billing.pcproj", "<Config>strict.xml</Config>\n<Sources Include=\"*.pcross\" />");

        var resolved = Workspace(WorkspaceFolder.FromPath(directory)).Resolve(Member(directory));

        Assert.False(resolved.IsUsable);
        Assert.True(resolved.ConfigRefused);
        Assert.Contains(resolved.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.InvalidProjectSetting.Code);
        Assert.Contains(resolved.Diagnostics, diagnostic => diagnostic.Code == HostDiagnosticCodes.ConfigurationFileRefused.Code);
    }

    // ---------------------------------------------------------------- beyond compilation

    /// <summary>A project is honoured in a workspace nobody has trusted: it names directories to read and a policy file, and nothing to run.</summary>
    [Fact]
    public void AProjectIsHonouredInAnUntrustedWorkspace()
    {
        var directory = TempDirectory();
        var schemas = Subdirectory(directory, "schemas");
        TempFile(directory, "strict.xml", CheckedOverflow);
        WriteProject(directory, "billing.pcproj", "<Config>strict.xml</Config>\n<Sources Include=\"*.pcross\" />\n<ProtoPath>schemas</ProtoPath>");

        var resolved = Workspace(WorkspaceFolder.FromPath(directory)).WithTrust(WorkspaceTrust.Untrusted).Resolve(Member(directory));

        Assert.Equal([schemas], resolved.IncludeDirectories);
        Assert.Equal(OverflowPolicy.Checked, resolved.Config?.Overflow);
    }

    /// <summary>Where an import is looked for while it is being typed includes the project's directories, first.</summary>
    [Fact]
    public void ImportRootsBeginWithTheProjectsProtoPaths()
    {
        var directory = TempDirectory();
        var schemas = Subdirectory(directory, "schemas");
        var folderSchemas = Subdirectory(directory, "folder-schemas");
        WriteProject(directory, "billing.pcproj", "<Sources Include=\"*.pcross\" />\n<ProtoPath>schemas</ProtoPath>");

        var roots = Workspace(WorkspaceFolder.FromPath(directory, settings: new ProtoCrossSettings { IncludePaths = [folderSchemas] }))
            .ResolveImportRoots(Member(directory));

        Assert.Equal([schemas, folderSchemas], roots.IncludeDirectories);
    }

    /// <summary>The report of a document's configuration names its project first, and says it came from the search.</summary>
    [Fact]
    public void TheReportNamesTheProject()
    {
        var directory = TempDirectory();
        var project = WriteProject(directory, "billing.pcproj", "<Sources Include=\"*.pcross\" />");

        var facts = Workspace(WorkspaceFolder.FromPath(directory)).Resolve(Member(directory)).Describe();

        Assert.Equal(new ConfigurationFact("project", project, ConfigurationSource.Project), facts[0]);
    }
}
