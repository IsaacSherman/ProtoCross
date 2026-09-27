using ProtoCross.Diagnostics;
using ProtoCross.LanguageServer.Workspace;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// The files a document's project compiles, which its settings carry (#106, spec 10.4.1): what the
/// document is compiled with, and a project the build refuses refusing the document too.
/// </summary>
public partial class WorkspaceConfigurationTests
{
    /// <summary>A document's settings carry every file its project compiles, the document among them.</summary>
    [Fact]
    public void ADocumentsSettingsCarryTheFilesItsProjectCompiles()
    {
        var directory = TempDirectory();
        WriteProject(directory, "billing.pcproj", "<Sources Include=\"*.pcross\" />\n<Tests Include=\"tests/*.pcross\" />");
        var tests = Member(Subdirectory(directory, "tests"), "pricing_tests.pcross");

        var resolved = Workspace(WorkspaceFolder.FromPath(directory)).Resolve(Member(directory));

        Assert.Equal([Path.Combine(directory, "source.pcross")], resolved.ProjectFiles?.Sources);
        Assert.Equal([tests.Path!], resolved.ProjectFiles?.Tests);
    }

    /// <summary>
    /// A project whose <c>&lt;Sources&gt;</c> match nothing is refused as the build refuses it, and the
    /// document it claims through <c>&lt;Tests&gt;</c> is not compiled: the refusal names the project and
    /// quotes why, and the reason itself is placed in the project file.
    /// </summary>
    [Fact]
    public void AProjectThatCompilesNothingRefusesTheDocument()
    {
        var directory = TempDirectory();
        WriteProject(directory, "billing.pcproj", "<Sources Include=\"src/*.pcross\" />\n<Tests Include=\"*.pcross\" />");

        var resolved = Workspace(WorkspaceFolder.FromPath(directory)).Resolve(Member(directory));

        Assert.True(resolved.ProjectRefused, "a project no build may compile must refuse its documents");
        Assert.False(resolved.IsUsable);
        var refusal = Assert.Single(resolved.Diagnostics, diagnostic => diagnostic.Code == HostDiagnosticCodes.ProjectRefused.Code);
        Assert.Contains(DiagnosticCodes.ProjectCompilesNothing.Code, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(
            resolved.Diagnostics,
            diagnostic => diagnostic.Code == DiagnosticCodes.ProjectCompilesNothing.Code && diagnostic.Span.File == "billing.pcproj");
    }

    /// <summary>
    /// A project refused for compiling nothing is listed again at the next question, without being
    /// told: it is being fixed, and a refusal remembered until the project file changed would outlast
    /// the fix.
    /// </summary>
    [Fact]
    public void AProjectRefusedForCompilingNothingIsListedAgainOnceItHasASource()
    {
        var directory = TempDirectory();
        var project = WriteProject(directory, "billing.pcproj", "<Sources Include=\"src/*.pcross\" />\n<Tests Include=\"*.pcross\" />");
        File.SetLastWriteTimeUtc(project, DateTime.UtcNow.AddHours(-1));
        var workspace = Workspace(WorkspaceFolder.FromPath(directory));
        var document = Member(directory);
        Assert.True(workspace.Resolve(document).ProjectRefused);

        Member(Subdirectory(directory, "src"), "pricing.pcross");

        Assert.False(workspace.Resolve(document).ProjectRefused, "a project with a source again is no longer refused");
    }

    /// <summary>
    /// A source added to a project changes how its documents compile, once what the project compiles is
    /// listed again: the document is compiled with one more file.
    /// </summary>
    [Fact]
    public void ASourceAddedToTheProjectChangesHowItsDocumentsCompile()
    {
        var directory = TempDirectory();
        WriteProject(directory, "billing.pcproj", "<Sources Include=\"*.pcross\" />");
        var workspace = Workspace(WorkspaceFolder.FromPath(directory));
        var document = Member(directory);
        var before = workspace.Resolve(document);

        Member(directory, "pricing.pcross");
        workspace.Projects.Forget();
        var after = workspace.Resolve(document);

        Assert.False(before.CompilesTheSameWayAs(after), "a document compiled with one more file is compiled another way");
    }
}
