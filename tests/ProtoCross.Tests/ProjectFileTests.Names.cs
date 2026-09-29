using ProtoCross.Diagnostics;
using ProtoCross.Projects;
using Xunit;

namespace ProtoCross.Tests;

public partial class ProjectFileTests
{
    // ------- a project's name, which is the namespace its behavior is declared in (#29, spec 5.4)

    /// <summary>
    /// Writes an empty project as <paramref name="fileName"/> in a directory of its own, and loads it
    /// by <paramref name="loadedAs"/>, or by the name it was written as.
    /// </summary>
    private static (ProtoCrossProject? Project, DiagnosticBag Diagnostics) LoadNamed(string fileName, string? loadedAs = null)
    {
        var directory = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, fileName), string.Format(Wrapper, ""));

        var diagnostics = new DiagnosticBag();
        return (ProtoCrossProject.Load(Path.Combine(directory, loadedAs ?? fileName), diagnostics), diagnostics);
    }

    [Theory]
    [InlineData("billing.pcproj", "billing")]
    [InlineData("acme.billing.pcproj", "acme.billing")]
    [InlineData("Acme.Billing2.pcproj", "Acme.Billing2")]
    public void AProjectsNamespaceIsItsName(string fileName, string package)
    {
        var (project, diagnostics) = LoadNamed(fileName);

        Assert.Empty(diagnostics);
        Assert.NotNull(project);
        Assert.Equal(package, project.Namespace.Package);
    }

    /// <summary>
    /// A project whose name no namespace can have is refused whole, with one error at the start of
    /// the file, because every name its consumers spell would be one nobody chose.
    /// </summary>
    [Theory]
    [InlineData("billing-service.pcproj")]
    [InlineData("1billing.pcproj")]
    [InlineData("_billing.pcproj")]
    [InlineData("acme..billing.pcproj")]
    public void AProjectWhoseNameIsNotANamespaceIsRefused(string fileName)
    {
        var (project, diagnostics) = LoadNamed(fileName);

        Assert.Null(project);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.ProjectNameIsNotANamespace.Code, diagnostic.Code);
        Assert.Equal(fileName, diagnostic.Span.File);
        Assert.Equal((1, 1), (diagnostic.Span.Start.Line, diagnostic.Span.Start.Column));
    }

    /// <summary>
    /// Where case is not part of a file's name, a project opened by another spelling of it is still
    /// named as its directory lists it, so one project has one namespace however it was typed.
    /// </summary>
    [Fact]
    public void AProjectIsNamedAsItsDirectoryListsIt()
    {
        if (PathIdentity.IsCaseSensitive)
        {
            Assert.Skip("Here a name spelled in another case names another file.");
        }

        var (project, diagnostics) = LoadNamed("Billing.pcproj", loadedAs: "billing.pcproj");

        Assert.Empty(diagnostics);
        Assert.NotNull(project);
        Assert.Equal("Billing", project.Namespace.Package);
    }

    /// <summary>
    /// A project renamed only in case says something other than it did, because its namespace moved,
    /// though it is read from the same path and states the same elements.
    /// </summary>
    [Fact]
    public void AProjectRenamedOnlyInCaseIsAnotherProject()
    {
        if (PathIdentity.IsCaseSensitive)
        {
            Assert.Skip("Here a name spelled in another case names another file.");
        }

        var directory = TestPaths.CreateTempDirectory();
        var path = Path.Combine(directory, "billing.pcproj");
        File.WriteAllText(path, string.Format(Wrapper, ""));
        var before = ProtoCrossProject.Load(path, new DiagnosticBag());

        File.Move(path, Path.Combine(directory, "Billing.pcproj"));
        var after = ProtoCrossProject.Load(path, new DiagnosticBag());

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.NotEqual(before, after);
    }
}
