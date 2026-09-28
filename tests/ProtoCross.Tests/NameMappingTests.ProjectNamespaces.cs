using ProtoCross.Backend;
using Xunit;

namespace ProtoCross.Tests;

public partial class NameMappingTests
{
    // ------- a project's namespace, spelled as protoc spells a package of that name (spec 24)

    /// <summary>
    /// A schema for each of <paramref name="packages"/>, declaring it as the schema's package, with
    /// one message so that protoc has something to declare there.
    /// </summary>
    private static IReadOnlyList<(string Name, string Text)> SchemasPackaged(IEnumerable<string> packages)
        => [.. packages.Select((package, index) => ($"Pkg{index}.proto", $"syntax = \"proto3\";\npackage {package};\nmessage M {{}}\n"))];

    private static IReadOnlyList<string> ProjectNames => ProjectNamespaceTests.NamespaceNames;

    /// <summary>
    /// A project's C# namespace is the one protoc declares for a schema whose package is the
    /// project's name, so a project named after its schemas' package puts its behavior beside their
    /// messages.
    /// </summary>
    [Fact]
    public void AProjectsCSharpNamespaceIsTheOneProtocDeclaresForThatPackage()
    {
        var (files, directory) = GenerateWithPinnedProtoc(SchemasPackaged(ProjectNames), "csharp_out");

        foreach (var (file, name) in files.Zip(ProjectNames))
        {
            var declared = DeclaredNamespace(File.ReadAllText(Path.Combine(directory, Path.ChangeExtension(file.Name, ".cs"))));
            var named = NameConventions.GetCSharpNamespace(new ProjectNamespace(name));
            Assert.True(
                named == declared,
                $"protoc declares the namespace '{declared}' for the package '{name}', and a project of that name is given '{named}'");
        }
    }

    /// <summary>
    /// A project's C++ namespace is the one protoc opens for a schema whose package is the project's
    /// name, a component at a time and with a keyword escaped.
    /// </summary>
    [Fact]
    public void AProjectsCppNamespaceIsTheOneProtocOpensForThatPackage()
    {
        var (files, directory) = GenerateWithPinnedProtoc(SchemasPackaged(ProjectNames), "cpp_out");

        foreach (var (file, name) in files.Zip(ProjectNames))
        {
            var header = File.ReadAllText(Path.Combine(directory, NameConventions.GetCppProtoHeader(file))).ReplaceLineEndings("\n");
            var opening = string.Concat(
                NameConventions.GetCppNamespace(new ProjectNamespace(name)).Split("::").Select(component => $"namespace {component} {{\n"));
            Assert.True(
                header.Contains(opening, StringComparison.Ordinal),
                $"protoc does not open the namespaces{Environment.NewLine}{opening}for the package '{name}'");
        }
    }
}
