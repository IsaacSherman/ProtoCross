using System.Text.RegularExpressions;
using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

public partial class BackendTests
{
    // ------- a project's behavior, declared in the project's namespace (#29, spec 24)

    private static readonly ProjectNamespace Owned = new("acme.billing");

    /// <summary>
    /// Every compilation in the corpus that succeeded, as though it were the compilation of a project
    /// named <see cref="Owned"/>.
    /// </summary>
    /// <remarks>
    /// The corpus rather than a sample, because a name the placement missed is one written by a
    /// construct nobody thought of: a call inside a loop condition, a test targeting a method another
    /// source declares, an enum-typed parameter.
    /// </remarks>
    private static IReadOnlyList<CompilationResult> CorpusAsProjects { get; } =
    [
        .. CompiledCorpus.All
            .Select(source => source.Result)
            .Distinct(ReferenceEqualityComparer.Instance)
            .Cast<CompilationResult>()
            .Where(result => result.Success)
            .Select(result => result with { ProjectNamespace = Owned }),
    ];

    /// <summary>The files one compilation generates, behavior and tests, leaving out the fixed-name support files.</summary>
    private static IReadOnlyList<GeneratedFile> GeneratedFrom(CompilationResult result, ITestBackend backend)
    {
        var diagnostics = new DiagnosticBag();
        var files = SourceEmission.Emit(result, backend, diagnostics)
            .Concat(SourceEmission.EmitTests(result, backend, diagnostics))
            .Where(file => !NameConventions.FixedNames.Contains(Path.GetFileName(file.RelativePath).Split('.')[0]))
            .ToList();

        Assert.Empty(diagnostics);
        return files;
    }

    /// <summary>
    /// A project's C# behavior declares every class in the project's namespace, whatever message
    /// it extends and whichever namespace protoc declared that message in.
    /// </summary>
    [Fact]
    public void AProjectsCSharpIsDeclaredInItsNamespaceAlone()
    {
        var declared = CorpusAsProjects
            .SelectMany(result => GeneratedFrom(result, new CSharpBackend()))
            .Where(file => !file.RelativePath.Contains(NameConventions.TestsSuffix, StringComparison.Ordinal))
            .SelectMany(file => Regex.Matches(file.Contents, @"^namespace (\S+)", RegexOptions.Multiline).Select(match => (file.RelativePath, Namespace: match.Groups[1].Value)))
            .ToList();

        Assert.NotEmpty(declared);
        Assert.All(declared, entry => Assert.True(
            entry.Namespace == NameConventions.GetCSharpNamespace(Owned),
            $"{entry.RelativePath} declares behavior in '{entry.Namespace}', a namespace the project does not own"));
    }

    /// <summary>
    /// Every call a project's C# makes to an extension method, from behavior or from a test, names the
    /// project's one class, qualified by the project's namespace.
    /// </summary>
    [Fact]
    public void EveryCSharpCallAProjectMakesNamesItsOneClass()
    {
        var expected = $"{NameConventions.GetCSharpNamespace(Owned)}.ProtoCrossExtensions";
        var named = CorpusAsProjects
            .SelectMany(result => GeneratedFrom(result, new CSharpBackend()))
            .SelectMany(file => Regex.Matches(file.Contents, @"global::([\w.]+\.\w*ProtoCrossExtensions)\.").Select(match => (file.RelativePath, Class: match.Groups[1].Value)))
            .ToList();

        Assert.NotEmpty(named);
        Assert.All(named, entry => Assert.True(
            entry.Class == expected,
            $"{entry.RelativePath} calls through '{entry.Class}' rather than the project's '{expected}'"));
    }

    /// <summary>
    /// A project's C++ opens no namespace but the project's, whatever message it extends and whichever
    /// namespace protoc declared that message in.
    /// </summary>
    [Fact]
    public void AProjectsCppIsDeclaredInItsNamespaceAlone()
    {
        var opened = CorpusAsProjects
            .SelectMany(result => GeneratedFrom(result, new CppBackend()))
            .SelectMany(file => Regex.Matches(file.Contents, @"^namespace (\S+)\r?$", RegexOptions.Multiline).Select(match => (file.RelativePath, Namespace: match.Groups[1].Value)))
            .ToList();

        Assert.NotEmpty(opened);
        Assert.All(opened, entry => Assert.True(
            entry.Namespace == NameConventions.GetCppNamespace(Owned),
            $"{entry.RelativePath} declares behavior in '{entry.Namespace}', a namespace the project does not own"));
    }

    /// <summary>
    /// Every call a project's C++ makes to one of its methods, from behavior or from a test driver,
    /// is qualified by the project's namespace, so no argument-dependent lookup can find another
    /// library's function of the same name.
    /// </summary>
    [Fact]
    public void EveryCppCallAProjectMakesIsQualifiedByItsNamespace()
    {
        var calls = CorpusAsProjects
            .SelectMany(result => CallsTo(result.Module!, GeneratedFrom(result, new CppBackend())))
            .ToList();

        Assert.NotEmpty(calls);
        Assert.All(calls, entry => Assert.True(
            entry.Namespace == NameConventions.GetCppNamespace(Owned),
            $"{entry.RelativePath} calls '{entry.Method}' in '{entry.Namespace}' rather than the project's namespace"));

        static IEnumerable<(string RelativePath, string Method, string Namespace)> CallsTo(Ir.IrModule module, IReadOnlyList<GeneratedFile> files)
            => from method in module.Methods.Select(method => NameConventions.EscapeCppKeyword(method.Name)).Distinct()
               from file in files
               from Match match in Regex.Matches(file.Contents, $@"::([\w:]+)::{Regex.Escape(method)}\(")
               select (file.RelativePath, method, match.Groups[1].Value);
    }

    /// <summary>
    /// A project's header is guarded by the project's name and its own, so two libraries that each
    /// have a source of one name can have both headers in one translation unit.
    /// </summary>
    [Fact]
    public void AProjectsHeaderIsGuardedByTheProjectsNameAndItsOwn()
    {
        var result = CompileSources(Pricing) with { ProjectNamespace = Owned };

        var header = SourceEmission.Emit(result, new CppBackend(), new DiagnosticBag())
            .Single(file => file.RelativePath == "pricing.pc.h");

        const string Guard = "PROTOCROSS_ACME_BILLING_PRICING_PC_H_";
        Assert.Contains($"#ifndef {Guard}\n", header.Contents, StringComparison.Ordinal);
        Assert.Contains($"#define {Guard}\n", header.Contents, StringComparison.Ordinal);
        Assert.Contains($"#endif  // {Guard}\n", header.Contents, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each source of a project declares one part of the project's class, holding every method it
    /// declares, whichever messages they extend, and its summary names those messages.
    /// </summary>
    [Fact]
    public void EachSourceOfAProjectDeclaresOnePartOfItsClass()
    {
        var result = CompileSources(
            Pricing,
            ("totals.pcross", """
                import proto "invoice.proto";

                extend InvoiceItem {
                    fn doubled() -> int64 { return gross() * 2; }
                }

                extend Invoice {
                    fn line_count() -> int64 { return 0; }
                }
                """)) with { ProjectNamespace = Owned };

        var totals = SourceEmission.Emit(result, new CSharpBackend(), new DiagnosticBag())
            .Single(file => file.RelativePath == "totals.g.cs").Contents;

        Assert.Equal(1, Regex.Count(totals, "partial class"));
        Assert.Contains("public static partial class ProtoCrossExtensions", totals, StringComparison.Ordinal);
        Assert.Contains(
            "/// <summary>ProtoCross behavior for <c>protocross.examples.Invoice</c>, <c>protocross.examples.InvoiceItem</c>.</summary>",
            totals,
            StringComparison.Ordinal);
        Assert.True(
            totals.IndexOf("LineCount(", StringComparison.Ordinal) < totals.IndexOf("Doubled(", StringComparison.Ordinal),
            "a part lists its methods by the message they extend, not in the order the source declared them");
    }

    /// <summary>
    /// The namespace a compilation is given reaches what is generated from it, without the caller
    /// handing it to the backend a second time.
    /// </summary>
    [Fact]
    public void ACompilationsProjectNamespaceReachesWhatIsGeneratedFromIt()
    {
        var directory = TestPaths.CreateTempDirectory();
        var sources = TestPaths.WriteSources(directory, [Pricing]);
        var result = new Compilation(
                [.. sources.Select(SourceDocument.ReadFrom)],
                new CompilationOptions { IncludePaths = [TestPaths.ExampleProtoDirectory], ProjectNamespace = Owned })
            .Compile();

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        Assert.Contains(
            SourceEmission.Emit(result, new CSharpBackend(), new DiagnosticBag()),
            file => file.Contents.Contains($"namespace {NameConventions.GetCSharpNamespace(Owned)}", StringComparison.Ordinal));
    }
}
