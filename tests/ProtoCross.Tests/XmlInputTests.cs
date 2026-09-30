using ProtoCross.Config;
using ProtoCross.Diagnostics;
using ProtoCross.Projects;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// What holds of every XML file the compiler takes settings from, whichever kind it is, because both
/// are read through <see cref="XmlInput"/>.
/// </summary>
public class XmlInputTests
{
    /// <summary>Reads <paramref name="xml"/> as the named kind of file, returning whether it was accepted.</summary>
    private static (bool Accepted, DiagnosticBag Diagnostics) Read(string kind, string xml)
    {
        var directory = TestPaths.CreateTempDirectory();
        var diagnostics = new DiagnosticBag();

        if (kind == "config")
        {
            var path = Path.Combine(directory, ProjectConfig.FileName);
            File.WriteAllText(path, xml);
            return (ProjectConfig.Load(path, diagnostics) is not null, diagnostics);
        }

        var projectPath = Path.Combine(directory, "billing" + ProtoCrossProject.Extension);
        File.WriteAllText(projectPath, xml);
        return (ProtoCrossProject.Load(projectPath, diagnostics) is not null, diagnostics);
    }

    /// <summary>
    /// A file declaring a document type is refused before any entity in it is expanded. Either kind
    /// comes with a repository, which an editor reads as soon as a folder is opened, and a few hundred
    /// bytes of entity definitions can expand into gigabytes.
    /// </summary>
    [Theory]
    [InlineData("config", "ProtoCross", "<Arithmetic><Overflow>&a;</Overflow></Arithmetic>", "PC2003")]
    [InlineData("project", "ProtoCrossProject", "<Sources Include=\"&a;\" />", "PC2007")]
    public void AFileDeclaringADocumentTypeIsRefused(string kind, string root, string body, string code)
    {
        var (accepted, diagnostics) = Read(
            kind,
            $"<?xml version=\"1.0\"?>\n<!DOCTYPE {root} [<!ENTITY a \"Checked\">]>\n<{root}>{body}</{root}>\n");

        Assert.False(accepted, $"a {kind} file declaring a document type was accepted");
        Assert.Equal(code, Assert.Single(diagnostics).Code);
    }
}
