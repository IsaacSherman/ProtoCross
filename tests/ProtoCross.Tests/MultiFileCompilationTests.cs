using System.Text.RegularExpressions;
using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Binding;
using ProtoCross.Config;
using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Symbols;
using ProtoCross.Tests.Conformance;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Several source files compile as one program (#27, spec 5.3): one module, one load of the schemas
/// they import, one policy, and a name of its own for each.
/// </summary>
public class MultiFileCompilationTests
{
    private const string Import = "import proto \"invoice.proto\";";

    private const string CheckedPolicy =
        "<ProtoCross><Arithmetic><Overflow>Checked</Overflow></Arithmetic></ProtoCross>";

    /// <summary>Writes each source into <paramref name="directory"/> and compiles them together.</summary>
    private static CompilationResult Compile(string directory, params (string Name, string Text)[] sources)
        => Compilation.Compile(TestPaths.WriteSources(directory, sources), [TestPaths.ExampleProtoDirectory]);

    private static CompilationResult Compile(params (string Name, string Text)[] sources)
        => Compile(TestPaths.CreateTempDirectory(), sources);

    private static string Write(string directory, string name, string text)
        => TestPaths.WriteSources(directory, (name, text))[0];

    private static string Extend(string methods, string imports = Import)
        => $$"""
             {{imports}}

             extend InvoiceItem {
                 {{methods}}
             }
             """;

    private static string Described(CompilationResult result)
        => string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.ToString()));

    /// <summary>
    /// Writes a proto3 <c>shared.proto</c> holding <paramref name="body"/> into a new directory under
    /// <paramref name="root"/>, and returns the directory.
    /// </summary>
    private static string WriteSharedSchema(string root, string directoryName, string body)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, directoryName)).FullName;
        File.WriteAllText(
            Path.Combine(directory, "shared.proto"),
            $"syntax = \"proto3\";\n{body}\n");
        return directory;
    }

    // ------- one program

    [Fact]
    public void AMethodMayCallAMethodDeclaredInAnotherFile()
    {
        var result = Compile(
            ("pricing.pcross", Extend("fn gross() -> int64 { return quantity * unit_price_cents; }")),
            ("discounts.pcross", Extend("fn net() -> int64 { return gross() - 1; }")));

        Assert.True(result.Success, Described(result));
        Assert.Equal(["pricing.pcross", "discounts.pcross"], result.SyntaxTrees.Select(tree => tree.Document.Name));
        Assert.Equal(2, result.Module!.Methods.Count);
    }

    /// <summary>
    /// A type any source imports can be named in every source (spec 5.2), the way a type an imported
    /// schema imports is nameable in the file that imports the schema.
    /// </summary>
    [Fact]
    public void ATypeOneFileImportsCanBeNamedInAnother()
    {
        var result = Compile(
            ("pricing.pcross", Extend("fn gross() -> int64 { return quantity * unit_price_cents; }")),
            ("stock.pcross", Extend(
                "fn is_empty() -> bool { return quantity == 0; }",
                imports: "import proto \"google/protobuf/timestamp.proto\";")));

        Assert.True(result.Success, Described(result));
    }

    /// <summary>
    /// Two sources importing one schema ask protoc for it once. The request is keyed by the files it
    /// names, so a compilation of both is answered from the cache that one source importing the
    /// schema filled, and asking for the schema twice would have been a request of its own.
    /// </summary>
    [Fact]
    public void ASchemaEveryFileImportsIsRequestedOnce()
    {
        var protoc = ProtocLocator.Locate();
        if (protoc is null)
        {
            Assert.Skip("No protoc on PATH and none in the NuGet cache. Restore the solution first.");
        }

        var loader = new DescriptorLoader(protoc, new DescriptorLoaderOptions { Cache = new DescriptorCache() });
        var directory = TestPaths.CreateTempDirectory();
        var alone = Write(directory, "alone.pcross", Extend("fn f() -> int64 { return 1; }"));
        string[] both =
        [
            Write(directory, "pricing.pcross", Extend("fn gross() -> int64 { return quantity * unit_price_cents; }")),
            Write(directory, "stock.pcross", Extend("fn is_empty() -> bool { return quantity == 0; }")),
        ];

        Assert.True(Compilation.Compile([alone], [TestPaths.ExampleProtoDirectory], loader).Success);
        var result = Compilation.Compile(both, [TestPaths.ExampleProtoDirectory], loader);

        Assert.True(result.Success, Described(result));
        Assert.Equal(2, result.Imports.Count);
        Assert.True(loader.ProtocInvocations == 1, "the two sources must ask for the schema exactly as one source does");
    }

    // ------- a schema beside a source, shadowed by another directory's

    /// <summary>
    /// Two sources in different directories each import <c>shared.proto</c>, and each sits beside a
    /// different file of that name. Both imports resolve to the first directory's copy, and the
    /// source whose own copy lost is warned that it did (<c>PC0087</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written for a review finding on #27: that the compiler resolved each source's imports against
    /// its own directory, then handed protoc only the path as written, so that protoc loaded the first
    /// directory's copy twice and <c>right.Right</c> went missing. The first half was not so. Spec 5.2
    /// resolves every import against one ordered list, both imports resolve to the left copy, and
    /// that is the one protoc loads, once. What was real is that the author of <c>right.pcross</c>
    /// got <c>PC0021</c> for a type declared in the file beside them, with nothing to say another
    /// file had been chosen.
    /// </para>
    /// <para>
    /// Loading both copies was rejected: protoc knows a schema by its path under its root, so one
    /// compilation cannot hold two files called <c>shared.proto</c>, and the code protoc generates
    /// from them would collide in any build that links both. Renaming the losing schema on the
    /// author's behalf was considered and shelved, because the name reaches everything protoc
    /// generates and every import of it. So the program still fails to compile, and the warning
    /// says which file won and which lost.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASchemaBesideASourceThatAnotherSourcesDirectoryShadowsIsWarnedAbout()
    {
        const string RightText =
            """
            import proto "shared.proto";

            extend right.Right {
                fn right_value() -> int64 { return value; }
            }
            """;

        var root = TestPaths.CreateTempDirectory();
        var left = WriteSharedSchema(root, "left", "package left; message Left { int64 value = 1; }");
        var right = WriteSharedSchema(root, "right", "package right; message Right { int64 value = 1; }");

        var result = Compilation.Compile(
            TestPaths.WriteSources(
                root,
                ("left/left.pcross", """
                    import proto "shared.proto";

                    extend left.Left {
                        fn left_value() -> int64 { return value; }
                    }
                    """),
                ("right/right.pcross", RightText)),
            []);

        var shadowed = Assert.Single(
            result.Diagnostics,
            diagnostic => diagnostic.Code == DiagnosticCodes.SchemaBesideSourceIsShadowed.Code);
        Assert.Equal(DiagnosticSeverity.Warning, shadowed.Severity);
        Assert.True(shadowed.Span.File == "right.pcross", $"the warning belongs to the source whose copy lost, not {shadowed.Span.File}");
        Assert.Equal(RightText.IndexOf("import", StringComparison.Ordinal), shadowed.Span.Start.Offset);
        Assert.Contains(left, shadowed.Message, StringComparison.Ordinal);
        Assert.Contains(right, shadowed.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A schema beside a source that an include path shadows is warned about in the same way, because
    /// include paths come first in the same list (spec 5.2), and one source is enough to be surprised.
    /// </summary>
    [Fact]
    public void ASchemaBesideASourceThatAnIncludePathShadowsIsWarnedAbout()
    {
        var root = TestPaths.CreateTempDirectory();
        var included = WriteSharedSchema(root, "protos", "package shared; message Included { int64 value = 1; }");
        var beside = WriteSharedSchema(root, "source", "package shared; message Beside { int64 value = 1; }");

        const string Text = "import proto \"shared.proto\";\n\nextend shared.Included {\n    fn included_value() -> int64 { return value; }\n}\n";

        var result = Compilation.Compile(Write(beside, "beside.pcross", Text), [included]);

        var shadowed = Assert.Single(
            result.Diagnostics,
            diagnostic => diagnostic.Code == DiagnosticCodes.SchemaBesideSourceIsShadowed.Code);
        Assert.Equal(Text.IndexOf("import", StringComparison.Ordinal), shadowed.Span.Start.Offset);
        Assert.Contains(included, shadowed.Message, StringComparison.Ordinal);
        Assert.Contains(beside, shadowed.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Copies of one schema that differ only in what a checkout or an editor changes unasked -- CRLF
    /// line endings, a leading UTF-8 byte order mark -- are one schema to protoc, so they are not
    /// warned about either.
    /// </summary>
    /// <remarks>
    /// <c>ShadowedSchemaComparisonTests</c> holds the other side: a line separator inside a string
    /// default is part of the value, and copies differing there are warned about.
    /// </remarks>
    [Theory]
    [InlineData("syntax = \"proto3\";\r\npackage shared;\r\nmessage Shared { int64 value = 1; }\r\n")]
    [InlineData("﻿syntax = \"proto3\";\npackage shared;\nmessage Shared { int64 value = 1; }\n")]
    public void CopiesOfASchemaThatProtocReadsAlikeAreNotWarnedAbout(string besideText)
    {
        var root = TestPaths.CreateTempDirectory();
        var included = WriteSharedSchema(root, "protos", "package shared;\nmessage Shared { int64 value = 1; }");
        var beside = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        File.WriteAllText(Path.Combine(beside, "shared.proto"), besideText);

        var result = Compilation.Compile(
            Write(beside, "beside.pcross", "import proto \"shared.proto\";\n\nextend shared.Shared {\n    fn shared_value() -> int64 { return value; }\n}\n"),
            [included]);

        Assert.Empty(result.Diagnostics);
    }

    /// <summary>
    /// Copies of one schema with the same contents are not warned about. Whichever copy is loaded,
    /// nothing differs, so nothing is wrong.
    /// </summary>
    [Fact]
    public void IdenticalCopiesOfASchemaBesideTwoSourcesAreNotWarnedAbout()
    {
        var root = TestPaths.CreateTempDirectory();
        WriteSharedSchema(root, "left", "package shared; message Shared { int64 value = 1; }");
        WriteSharedSchema(root, "right", "package shared; message Shared { int64 value = 1; }");

        var result = Compilation.Compile(
            TestPaths.WriteSources(
                root,
                ("left/left.pcross", "import proto \"shared.proto\";\n\nextend shared.Shared {\n    fn left_value() -> int64 { return value; }\n}\n"),
                ("right/right.pcross", "import proto \"shared.proto\";\n\nextend shared.Shared {\n    fn right_value() -> int64 { return value; }\n}\n")),
            []);

        Assert.True(result.Success, Described(result));
        Assert.Empty(result.Diagnostics);
    }

    // ------- a file with no import

    /// <summary>
    /// A file that imports nothing is told so, and is still bound against what the others import,
    /// so the rest of what is wrong with it is reported as well.
    /// </summary>
    [Fact]
    public void AFileWithNoImportIsReportedAndStillBound()
    {
        var bare = Extend("fn f() -> int64 { return nowhere; }", imports: string.Empty);

        var result = Compile(
            ("pricing.pcross", Extend("fn gross() -> int64 { return quantity * unit_price_cents; }")),
            ("bare.pcross", bare));

        Assert.Equal(
            [DiagnosticCodes.NoProtoImports.Code, DiagnosticCodes.UnknownName.Code],
            result.Diagnostics.Select(diagnostic => diagnostic.Code));
        Assert.All(result.Diagnostics, diagnostic => Assert.Equal("bare.pcross", diagnostic.Span.File));
        Assert.NotNull(result.Module);
    }

    [Fact]
    public void NoFileImportingAnythingStopsBeforeBinding()
    {
        var result = Compile(
            ("a.pcross", Extend("fn f() -> int64 { return 1; }", imports: string.Empty)),
            ("b.pcross", Extend("fn g() -> int64 { return 2; }", imports: string.Empty)));

        Assert.Equal(
            ["a.pcross", "b.pcross"],
            result.Diagnostics.Where(d => d.Code == DiagnosticCodes.NoProtoImports.Code).Select(d => d.Span.File));
        Assert.Null(result.Module);
    }

    // ------- names of its own

    /// <summary>
    /// Two sources whose names fold together in anything generated from them are refused, at the
    /// second, before either is read into a module.
    /// </summary>
    [Theory]
    [InlineData("a-b.pcross", "a_b.pcross")]
    [InlineData("Pricing.pcross", "sub/pricing.pcross")]
    [InlineData("1a.pcross", "t1a.pcross")]
    [InlineData("a_b.pcross", "aB.pcross")]
    public void TwoSourcesWhoseGeneratedNamesWouldCollideAreRefused(string first, string second)
    {
        var result = Compile(
            (first, Extend("fn f() -> int64 { return 1; }")),
            (second, Extend("fn g() -> int64 { return 2; }")));

        var refused = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.SourcesShareGeneratedNames.Code, refused.Code);
        Assert.Equal(Path.GetFileName(second), refused.Span.File);
        Assert.Contains($"'{Path.GetFileName(first)}'", refused.Message, StringComparison.Ordinal);
        Assert.Null(result.Module);
    }

    [Fact]
    public void OneSourceGivenTwiceIsRefused()
    {
        var path = Write(TestPaths.CreateTempDirectory(), "pricing.pcross", Extend("fn f() -> int64 { return 1; }"));

        var result = Compilation.Compile([path, path], [TestPaths.ExampleProtoDirectory]);

        var refused = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.SourcesShareGeneratedNames.Code, refused.Code);
        Assert.Equal("'pricing.pcross' is given more than once.", refused.Message);
    }

    /// <summary>
    /// The key <c>PC2006</c> compares is coarser than every name the backends derive from a source's:
    /// two names that give different keys never give one generated file, include guard or test class.
    /// </summary>
    /// <remarks>
    /// Swept over every name of up to three characters drawn from the ones that fold differently --
    /// case, the two punctuation marks the derivations treat differently, a digit, and the <c>t</c> a
    /// C# test class puts in front of one -- and read from what the backends actually emit, so a
    /// derivation that changes is caught rather than trusted.
    /// </remarks>
    [Fact]
    public void NamesWithDifferentKeysAreGeneratedUnderDifferentNames()
    {
        var module = Compile(("probe.pcross", Extend("fn f() -> int64 { return 1; }") + """

            test InvoiceItem.f "probe" {
                receiver { quantity = 1; }
                expect return 1;
            }
            """)).EmittableModule!;

        var stems = Stems("aAbt1_-", 3).ToList();
        var failures = new List<string>();

        foreach (var (kind, derive) in DerivedNames(module))
        {
            foreach (var group in stems.GroupBy(derive, StringComparer.OrdinalIgnoreCase))
            {
                var keys = group.Select(NameConventions.OutputKey).Distinct().ToList();
                if (keys.Count > 1)
                {
                    failures.Add($"{kind} '{group.Key}' is shared by {string.Join(", ", group)}, whose keys differ");
                }
            }
        }

        Assert.True(stems.Count > 300, $"the sweep must have names to fold; it has {stems.Count}");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(20)));
    }

    // ------- one policy

    [Fact]
    public void TwoFilesUnderDifferentConfigurationsAreRefused()
    {
        var root = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(root, "checked")).FullName, "protocross.config.xml"), CheckedPolicy);

        var result = Compile(
            root,
            ("checked/pricing.pcross", Extend("fn f() -> int64 { return 1; }")),
            ("plain/stock.pcross", Extend("fn g() -> int64 { return 2; }")));

        var refused = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.SourcesDisagreeOnPolicy.Code, refused.Code);
        Assert.Equal("stock.pcross", refused.Span.File);
        Assert.Null(result.Module);
    }

    [Fact]
    public void FilesUnderOneConfigurationCompileUnderIt()
    {
        var root = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "protocross.config.xml"), CheckedPolicy);

        var result = Compile(
            root,
            ("pricing/pricing.pcross", Extend("fn f() -> int64 { return 1; }")),
            ("stock/stock.pcross", Extend("fn g() -> int64 { return 2; }")));

        Assert.True(result.Success, Described(result));
        Assert.Equal(OverflowPolicy.Checked, result.Config.Overflow);
    }

    /// <summary>
    /// A buffer never saved has nowhere to look for a configuration, so it states no policy and takes
    /// the one the saved sources found, rather than disagreeing with them by stating the default.
    /// </summary>
    [Fact]
    public void ASourceWithNoDirectoryCompilesUnderThePolicyTheOthersFound()
    {
        var root = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "protocross.config.xml"), CheckedPolicy);
        var saved = SourceDocument.ReadFrom(Write(root, "pricing.pcross", Extend("fn f() -> int64 { return 1; }")));
        var unsaved = new SourceDocument(SourceIdentity.Unsaved("untitled.pcross"), Extend("fn g() -> int64 { return 2; }"));

        var result = Compilation.Compile([unsaved, saved], [TestPaths.ExampleProtoDirectory]);

        Assert.True(result.Success, Described(result));
        Assert.Equal(OverflowPolicy.Checked, result.Config.Overflow);
    }

    // ------- a list of one

    /// <summary>
    /// One source handed over in a list compiles exactly as it does handed over alone: the same
    /// diagnostics, rendered the same, and the same generated code in both backends.
    /// </summary>
    [Fact]
    public void OneSourceInAListCompilesExactlyAsItDoesAlone()
    {
        var sources = ConformanceVectors.HandWrittenSources
            .Select(path => (path, Protos: ConformanceVectors.ProtoDirectory))
            .Append((TestPaths.SimpleScript, Protos: TestPaths.ExampleProtoDirectory))
            .Append((TestPaths.WriteTempScript(CompiledCorpus.BrokenText), Protos: TestPaths.ExampleProtoDirectory));

        foreach (var (path, protos) in sources)
        {
            var alone = Compilation.Compile(path, [protos]);
            var listed = Compilation.Compile([path], [protos]);

            Assert.Equal(Described(alone), Described(listed));
            Assert.Equal(alone.Success, listed.Success);
            if (alone.Success)
            {
                Assert.Equal(Emitted(alone), Emitted(listed));
            }
        }
    }

    // ------- position questions, one document at a time

    /// <summary>
    /// Two files can hold something at the same offset, so a position question is asked of one
    /// document and answered from that document alone.
    /// </summary>
    [Fact]
    public void APositionQueryAnswersFromItsOwnDocument()
    {
        var text = Extend("fn f() -> int64 { return quantity; }");
        var result = Compile(("a.pcross", text), ("b.pcross", text.Replace("fn f()", "fn g()", StringComparison.Ordinal)));
        var offset = text.IndexOf("quantity;", StringComparison.Ordinal);

        foreach (var tree in result.SyntaxTrees)
        {
            var model = SemanticModel.For(result, tree.Document);

            Assert.Equal(tree.Document.Name, model.IrAt(offset)!.Node.Span.File);
            Assert.Equal(tree.Document.Name, model.SyntaxAt(offset)!.Node.Span.File);
            Assert.Equal(tree.Document, model.ReferenceAt(offset)!.Document);
            Assert.All(model.AllReferences, reference => Assert.Equal(tree.Document, reference.Document));
        }
    }

    /// <summary>
    /// What a symbol is and where it is used are not questions about a position, so they answer for
    /// the whole compilation: a method's references include the call written in another file.
    /// </summary>
    [Fact]
    public void AMethodsReferencesIncludeCallsInOtherFiles()
    {
        var result = Compile(
            ("pricing.pcross", Extend("fn gross() -> int64 { return quantity * unit_price_cents; }")),
            ("discounts.pcross", Extend("fn net() -> int64 { return gross() - 1; }")));
        var pricing = result.SyntaxTrees[0].Document;
        var discounts = result.SyntaxTrees[1].Document;

        var gross = result.Module!.Methods.Single(method => method.Name == "gross").Signature;
        var references = SemanticModel.For(result, pricing).ReferencesTo(gross.Id);

        Assert.Equal(2, references.Count);
        Assert.Contains(references, reference => reference.Document == pricing && reference.Kind == ReferenceKind.Declaration);
        Assert.Contains(references, reference => reference.Document == discounts && reference.Kind == ReferenceKind.Read);
        Assert.Equal(gross.Declaration, SemanticModel.For(result, discounts).DeclarationOf(gross.Id));
    }

    // ------- helpers

    private static IEnumerable<string> Emitted(CompilationResult result)
    {
        var diagnostics = new DiagnosticBag();
        ITestBackend[] backends = [new CSharpBackend(), new CppBackend()];

        return backends
            .SelectMany(backend => SourceEmission.Emit(result, backend, diagnostics).Concat(SourceEmission.EmitTests(result, backend, diagnostics)))
            .Select(file => $"{file.RelativePath}{Environment.NewLine}{file.Contents}");
    }

    /// <summary>Every string of one to <paramref name="length"/> characters from <paramref name="alphabet"/>.</summary>
    private static IEnumerable<string> Stems(string alphabet, int length)
    {
        IEnumerable<string> current = [string.Empty];
        for (var i = 0; i < length; i++)
        {
            current = current.SelectMany(prefix => alphabet.Select(c => prefix + c)).ToList();
            foreach (var stem in current)
            {
                yield return stem;
            }
        }
    }

    /// <summary>
    /// Each name a backend derives from a source's, read from what it emits for a source of that
    /// name. File names are compared ignoring case, because a file system may.
    /// </summary>
    private static IEnumerable<(string Kind, Func<string, string> Derive)> DerivedNames(IrModule module)
    {
        var diagnostics = new DiagnosticBag();
        IReadOnlyList<GeneratedFile> CSharp(string stem) => new CSharpBackend().EmitTests(module, new BackendOptions(stem + ".pcross"), diagnostics);
        IReadOnlyList<GeneratedFile> Cpp(string stem) => new CppBackend().Emit(module, new BackendOptions(stem + ".pcross"), diagnostics);
        IReadOnlyList<GeneratedFile> CppTests(string stem) => new CppBackend().EmitTests(module, new BackendOptions(stem + ".pcross"), diagnostics);

        yield return ("C# behavior file", stem => new CSharpBackend().Emit(module, new BackendOptions(stem + ".pcross"), diagnostics)[^1].RelativePath);
        yield return ("C# test file", stem => CSharp(stem)[^1].RelativePath);
        yield return ("C# test class", stem => Regex.Match(CSharp(stem)[^1].Contents, @"public sealed class (\w+)").Groups[1].Value);
        yield return ("C++ header", stem => Cpp(stem)[^1].RelativePath);
        yield return ("C++ include guard", stem => Regex.Match(Cpp(stem)[^1].Contents, @"#ifndef (\w+)").Groups[1].Value);
        yield return ("C++ test driver", stem => CppTests(stem)[^1].RelativePath);
    }
}
