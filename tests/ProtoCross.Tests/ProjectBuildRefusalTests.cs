using ProtoCross.Diagnostics;
using ProtoCross.Projects;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Which projects no build may compile once they are read (#106, spec 5.4): one whose
/// <c>&lt;Sources&gt;</c> find nothing is refused, by the one rule the command line and the editor ask.
/// </summary>
public class ProjectBuildRefusalTests
{
    private const string ProjectName = "billing" + ProtoCrossProject.Extension;

    /// <summary>
    /// Creates <paramref name="files"/> (empty) beside a project holding <paramref name="body"/>, and asks
    /// what a build of it compiles, reporting into <paramref name="diagnostics"/>.
    /// </summary>
    private static (ProjectFiles? Files, ProjectFiles Expanded) ExpandForBuild(
        string body, DiagnosticBag diagnostics, params string[] files)
    {
        var directory = TestPaths.CreateTempDirectory();
        foreach (var file in files)
        {
            var path = Path.Combine(directory, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
        }

        var projectPath = Path.Combine(directory, ProjectName);
        File.WriteAllText(projectPath, $"<ProtoCrossProject>\n{body}\n</ProtoCrossProject>\n");

        var project = ProtoCrossProject.Load(projectPath, new DiagnosticBag());
        Assert.NotNull(project);

        return (ProjectSources.ExpandForBuild(project, diagnostics), ProjectSources.Expand(project, new DiagnosticBag()));
    }

    /// <summary>
    /// A project whose <c>&lt;Sources&gt;</c> find nothing is refused although it has tests to compile,
    /// with an error placed in the project file, where an editor publishes it.
    /// </summary>
    [Fact]
    public void AProjectWhoseSourcesMatchNothingCompilesNothingEvenWithTestsToCompile()
    {
        var diagnostics = new DiagnosticBag();
        var (files, _) = ExpandForBuild(
            "<Sources Include=\"src/*.pcross\" />\n<Tests Include=\"tests/*.pcross\" />",
            diagnostics,
            "tests/pricing.pcross");

        Assert.Null(files);
        var refusal = Assert.Single(diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.ProjectCompilesNothing.Code);
        Assert.Equal(DiagnosticSeverity.Error, refusal.Severity);
        Assert.True(
            refusal.Span.File == ProjectName && !refusal.Span.IsNone,
            $"the refusal must be placed in the project file, not at '{refusal.Span}'");
    }

    /// <summary>A project with no <c>&lt;Sources&gt;</c> element at all is refused the same way.</summary>
    [Fact]
    public void AProjectWithoutASourcesElementCompilesNothing()
    {
        var diagnostics = new DiagnosticBag();
        var (files, _) = ExpandForBuild("<Tests Include=\"*.pcross\" />", diagnostics, "pricing.pcross");

        Assert.Null(files);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.ProjectCompilesNothing.Code);
    }

    /// <summary>A project that has sources builds exactly what its patterns match, and nothing is reported.</summary>
    [Fact]
    public void AProjectWithSourcesBuildsWhatItsPatternsMatch()
    {
        var diagnostics = new DiagnosticBag();
        var (files, expanded) = ExpandForBuild(
            "<Sources Include=\"src/*.pcross\" />\n<Tests Include=\"tests/*.pcross\" />",
            diagnostics,
            "src/pricing.pcross",
            "tests/pricing_tests.pcross");

        Assert.Empty(diagnostics);
        Assert.Equal(expanded, files);
    }

    /// <summary>
    /// A project one of whose patterns searches a directory that cannot be listed compiles nothing,
    /// rather than the sources that could be found: a build missing some of them would build a program
    /// nobody wrote.
    /// </summary>
    [Fact]
    public void AProjectWhoseFilesCannotAllBeListedCompilesNothing()
    {
        var diagnostics = new DiagnosticBag();
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("pricing.pcross", string.Empty), ("locked/totals.pcross", string.Empty));
        var projectPath = Path.Combine(directory, ProjectName);
        File.WriteAllText(projectPath, "<ProtoCrossProject><Sources Include=\"*.pcross\" /><Sources Include=\"locked/*.pcross\" /></ProtoCrossProject>");
        var project = ProtoCrossProject.Load(projectPath, new DiagnosticBag());
        Assert.NotNull(project);

        var locked = new DirectoryInfo(Path.Combine(directory, "locked"));
        using (Unlistable(locked))
        {
            Assert.Null(ProjectSources.ExpandForBuild(project, diagnostics));
        }

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.ProjectCouldNotBeRead.Code);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.ProjectCompilesNothing.Code);
    }

    /// <summary>Makes <paramref name="directory"/> unlistable until the result is disposed.</summary>
    private static IDisposable Unlistable(DirectoryInfo directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return DeniedListing(directory);
        }

        if (Environment.UserName == "root")
        {
            Assert.Skip("root lists any directory, so none can be made unlistable here.");
        }

        return WithoutPermissions(directory);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IDisposable DeniedListing(DirectoryInfo directory)
    {
        var rule = new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny);
        var security = directory.GetAccessControl();
        security.AddAccessRule(rule);
        directory.SetAccessControl(security);

        return new Restore(() =>
        {
            var restored = directory.GetAccessControl();
            restored.RemoveAccessRule(rule);
            directory.SetAccessControl(restored);
        });
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static IDisposable WithoutPermissions(DirectoryInfo directory)
    {
        var mode = File.GetUnixFileMode(directory.FullName);
        File.SetUnixFileMode(directory.FullName, UnixFileMode.None);

        return new Restore(() => File.SetUnixFileMode(directory.FullName, mode));
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>
    /// What decides is what the expansion reported. An error the caller's bag already held, about
    /// something else, does not refuse a project that builds.
    /// </summary>
    [Fact]
    public void AnErrorTheCallerAlreadyHeldDoesNotRefuseTheProject()
    {
        var diagnostics = new DiagnosticBag();
        diagnostics.Report(DiagnosticCodes.ProjectCouldNotBeRead, "some other file could not be read.", SourceSpan.None);

        var (files, _) = ExpandForBuild("<Sources Include=\"*.pcross\" />", diagnostics, "pricing.pcross");

        Assert.NotNull(files);
        Assert.Single(diagnostics);
    }
}
