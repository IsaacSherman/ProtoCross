using ProtoCross.Ir;
using ProtoCross.Semantics;
using ProtoCross.Syntax;
using ProtoCross.Tests.Conformance;

namespace ProtoCross.Tests;

/// <summary>One compiled source, kept beside the text it came from, and seen on its own.</summary>
/// <remarks>
/// <para>
/// A compilation of several sources is one result, and an offset means something only within one of
/// them. So everything a sweep asks at an offset, or of what one file wrote, is asked of
/// <see cref="Document"/>: its own tree, its own part of the module, and a model that answers at its
/// offsets. What belongs to the whole program stays on <see cref="Result"/>: its diagnostics, its
/// policy, and the module in which a call written here finds what it calls.
/// </para>
/// <para>
/// A sweep that asked the result instead would be asking about the first source with this one's
/// text, and would find out only where the two happened to disagree.
/// <see cref="CompiledCorpus.CrossFile"/> is in the corpus so that they always do somewhere.
/// </para>
/// </remarks>
internal sealed record CorpusSource(string Name, string Text, CompilationResult Result, SourceIdentity Document)
{
    /// <summary>A source compiled on its own, and so the only one its result holds.</summary>
    /// <remarks>
    /// Refuses a result holding any other number of trees, naming the source, because this runs in
    /// the corpus's type initializer: a bare <see cref="Enumerable.Single{TSource}(IEnumerable{TSource})"/>
    /// would fail every sweep with "sequence contains no elements" and never say which file stopped.
    /// </remarks>
    public CorpusSource(string name, string text, CompilationResult result)
        : this(name, text, result, OnlyDocumentOf(name, result))
    {
    }

    private static SourceIdentity OnlyDocumentOf(string name, CompilationResult result)
        => result.SyntaxTrees is [var only]
            ? only.Document
            : throw new InvalidOperationException(
                $"'{name}' compiled to {result.SyntaxTrees.Count} syntax trees rather than one: "
                + string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.ToString())));

    /// <summary>This source's syntax tree, or null when it was never parsed.</summary>
    public CompilationUnit? SyntaxTree => Result.SyntaxTrees.FirstOrDefault(tree => tree.Document == Document)?.Unit;

    /// <summary>The part of the module this source declared, or null when nothing was bound.</summary>
    public IrModule? Module => Result.Module?.DeclaredIn(Document);

    /// <summary>A model that answers at this source's offsets.</summary>
    public SemanticModel Model => SemanticModel.For(Result, Document);

    /// <summary>
    /// Whether this is the only source of its compilation, which is how the language server compiles
    /// every buffer until a project can say which files belong together (#106).
    /// </summary>
    public bool StandsAlone => Result.SyntaxTrees.Count == 1;
}

/// <summary>
/// Every ProtoCross source the repository maintains, compiled once, for the tests that assert a
/// property of all of them.
/// </summary>
/// <remarks>
/// <para>
/// The conformance vectors are the corpus worth sweeping: between them they use every construct the
/// language has, they are kept compiling by a test of their own, and they are edited when the
/// language grows. A hand-written fixture claiming the same coverage would be a second corpus to
/// remember to extend, and the first thing it would fall behind on is the construct that was just
/// added.
/// </para>
/// <para>
/// Compiled once for the whole assembly because each source shells out to protoc. The broken buffer
/// is here for the same reason the others are: error recovery is what puts nodes in surprising
/// places, so a sweep that only ever sees well-formed files is a sweep over the easy half.
/// </para>
/// <para>
/// The vectors the arithmetic sweep generates are left out. Between them they are a few constructs
/// repeated several thousand times, so they hold nothing a sweep over constructs would not already
/// meet in the written ones, and a sweep that visits every offset or every node costs in proportion
/// to their size: with them in, the sweeps that run every time took ten times as long.
/// </para>
/// </remarks>
internal static class CompiledCorpus
{
    /// <summary>
    /// A file with several distinct mistakes in it: a member name never written, a parameter list
    /// that was abandoned, a call to a method that is not there, and a call through something that
    /// could never be one -- the last two being the shapes that put an
    /// <see cref="Ir.IrUncallableInvocation"/> in the tree with each of its two halves.
    /// </summary>
    public const string BrokenText =
        """
        import proto "invoice.proto";
        extend Invoice {
            fn f() -> int64 {
                for line in items {
                    return line.
                }

                return items.
            }

            fn g( -> int64 { return nosuchmethod(1, 2); }

            fn h() -> int64 { return 1(2); }
        }
        """;

    /// <summary>
    /// A buffer that simply stops, mid-construct, the way every buffer does between one keystroke
    /// and the next: a loop that closed, inside a method that did not, inside an <c>extend</c> that
    /// did not either.
    /// </summary>
    /// <remarks>
    /// <see cref="BrokenText"/> is broken and <em>balanced</em> -- every brace in it has its pair,
    /// and its mistakes are ones the binder finds. Nothing in the corpus was unfinished, so no sweep
    /// over it ever met a construct the parser could not close, which is the state a file spends
    /// most of its life in while someone is typing into it. The distinction is not academic: a query
    /// answering at the end of this file has to tell the loop, which a brace closed, from the method,
    /// which nothing did, and they end at the same offset.
    /// </remarks>
    public const string UnclosedText =
        """
        import proto "invoice.proto";

        extend Invoice {
            fn totals() -> int64 {
                var total: int64 = 0;

                for item in items {
                    total = total + item.quantity;
                }
        """;

    public static CorpusSource SimpleScript { get; } = new(
        "simpleScript",
        File.ReadAllText(TestPaths.SimpleScript),
        Compilation.Compile(TestPaths.SimpleScript, [TestPaths.ExampleProtoDirectory]));

    public static CorpusSource Broken { get; } = new(
        "broken",
        BrokenText,
        Compilation.Compile(TestPaths.WriteTempScript(BrokenText), [TestPaths.ExampleProtoDirectory]));

    public static CorpusSource Unclosed { get; } = new(
        "unclosed",
        UnclosedText,
        Compilation.Compile(TestPaths.WriteTempScript(UnclosedText), [TestPaths.ExampleProtoDirectory]));

    /// <summary>
    /// The shapes a dotted name can take, which the rest of the corpus does not happen to contain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every source here was written to exercise the language, and between them they do. None was
    /// written to exercise a <em>name</em>, so the corpus reached this far without one qualified type
    /// reference, one package-qualified <c>extend</c> receiver, or one name spread over two lines --
    /// and each of those was a defect found by hand after the sweep had passed over the file that
    /// should have contained it. A fixture written beside the test that found the defect protects
    /// that test; a source in the corpus protects every sweep there will ever be.
    /// </para>
    /// <para>
    /// It does not compile, and that is deliberate in one place only: an enum-valued field with a dot
    /// after it. There is no valid way to write that -- constants are reached through the enum's name
    /// and never through a value of it -- so the shape exists solely in a buffer someone is midway
    /// through, which is the state completion is asked about. <see cref="BrokenText"/> is here on the
    /// same argument.
    /// </para>
    /// <para>
    /// Compiled against the fixture schemas as well as the example ones, because those are where a
    /// package, a nested type and an enum-valued field all exist together.
    /// </para>
    /// </remarks>
    public const string QualifiedText =
        """
        import proto "fixtures.proto";

        extend protocross.tests.Outer {
            fn qualified(other: protocross.
                tests.Outer) -> int64 {
                var level: protocross.tests.TopLevelStatus = other.status;
                return other.count;
            }

            fn deep(inner: protocross.tests.Outer.Inner) -> protocross.tests.Outer.Inner.Deep {
                return inner.deep;
            }

            fn plain() -> int64 {
                return count;
            }

            fn reached() -> int64 {
                return status.
            }
        }

        test protocross.tests.Outer.plain "a package-qualified test target" {
            receiver {
                count = 2;
            }
            expect return 2;
        }
        """;

    /// <inheritdoc cref="QualifiedText"/>
    public static CorpusSource Qualified { get; } = new(
        "qualified",
        QualifiedText,
        Compilation.Compile(
            TestPaths.WriteTempScript(QualifiedText),
            [TestPaths.ExampleProtoDirectory, TestPaths.FixtureProtoDirectory]));

    /// <summary>
    /// The first of two sources compiled as one program: a method that calls one the other source
    /// declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other entry is a compilation of one source, where the source and the compilation are the
    /// same thing and a sweep cannot tell which of the two it asked. Here they differ: each source
    /// has offsets the other does not, a reference in each names a declaration in the other, and
    /// both extend <c>Invoice</c>, so one receiver's methods are split between them.
    /// </para>
    /// <para>
    /// The two are the shortest program that has all three. The multi-file conformance vectors
    /// arrive after this and will be swept the same way; this entry is what shows the sweeps were
    /// ready for them.
    /// </para>
    /// </remarks>
    public const string CrossFileTotalsText =
        """
        import proto "invoice.proto";

        extend Invoice {
            fn total_cents() -> int64 {
                var total: int64 = 0;

                for item in items {
                    total = total + item.line_cents();
                }

                return total;
            }
        }
        """;

    /// <summary>
    /// The second source of the program <see cref="CrossFileTotalsText"/> begins: the method the
    /// first calls, another method on <c>Invoice</c>, and a test of the first source's method.
    /// </summary>
    public const string CrossFileLinesText =
        """
        import proto "invoice.proto";

        extend InvoiceItem {
            fn line_cents() -> int64 {
                return quantity * unit_price_cents;
            }
        }

        extend Invoice {
            fn line_count() -> int64 {
                var count: int64 = 0;

                for item in items {
                    count = count + 1;
                }

                return count;
            }
        }

        test Invoice.total_cents "adds up lines another file prices" {
            receiver {
                items {
                    quantity = 2;
                    unit_price_cents = 300;
                }
            }

            expect return 600;
        }
        """;

    /// <inheritdoc cref="CrossFileTotalsText"/>
    public static IReadOnlyList<CorpusSource> CrossFile { get; } = Together(
        [TestPaths.ExampleProtoDirectory],
        ("crossfile_totals", CrossFileTotalsText),
        ("crossfile_lines", CrossFileLinesText));

    /// <summary>
    /// The example, the broken buffer, the unfinished one, the qualified names, the program written
    /// across two files, and every conformance vector someone wrote.
    /// </summary>
    public static IReadOnlyList<CorpusSource> All { get; } =
    [
        SimpleScript,
        Broken,
        Unclosed,
        Qualified,
        .. CrossFile,
        .. ConformanceVectors.HandWritten.Select(vector => new CorpusSource(
            vector.Name,
            File.ReadAllText(vector.SourcePath),
            ConformanceVectors.Compile(vector))),
    ];

    /// <summary>Compiles <paramref name="sources"/> as one program, and sees each of them on its own.</summary>
    private static IReadOnlyList<CorpusSource> Together(
        IReadOnlyList<string> protoPaths,
        params (string Name, string Text)[] sources)
    {
        var paths = TestPaths.WriteSources(
            TestPaths.CreateTempDirectory(),
            [.. sources.Select(source => ($"{source.Name}.pcross", source.Text))]);
        var result = Compilation.Compile(paths, protoPaths);

        return [.. sources.Zip(paths, (source, path) => new CorpusSource(source.Name, source.Text, result, SourceIdentity.FromPath(path)))];
    }
}
