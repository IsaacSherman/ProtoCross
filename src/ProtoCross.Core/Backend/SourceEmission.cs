using ProtoCross.Diagnostics;
using ProtoCross.Ir;

namespace ProtoCross.Backend;

/// <summary>
/// Generates a compilation one source at a time, each into files named after it (spec 5.3).
/// </summary>
/// <remarks>
/// <para>
/// A compilation binds every source into one module, because a call may cross from one into another,
/// and a backend is handed a module. So a caller holding a compilation of several sources has to
/// divide the module back into its sources, give each the options that name its files, and gather
/// what comes back. That is the same four steps for the command line, the conformance harness and
/// any host, and three copies of it would be three answers to which name a source's files take.
/// </para>
/// <para>
/// Each backend also emits a runtime file under a fixed name beside every source's own. The copies
/// are identical, which is what the fixed name exists for, so one is kept. Two different files under
/// one name would be one source's output overwriting another's, which <c>PC2006</c> refuses before a
/// module exists; meeting one here is a defect in the compiler rather than in the input, and it
/// throws rather than choosing which to keep.
/// </para>
/// </remarks>
public static class SourceEmission
{
    /// <summary>What <paramref name="backend"/> generates for the behavior of every source.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="result"/> did not succeed, so there is no module anything may be generated from.
    /// </exception>
    public static IReadOnlyList<GeneratedFile> Emit(CompilationResult result, IBackend backend, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(backend);

        return EachSource(result, diagnostics, backend.Emit);
    }

    /// <summary>What <paramref name="backend"/> generates for the tests of every source that has any.</summary>
    /// <inheritdoc cref="Emit" path="/exception"/>
    public static IReadOnlyList<GeneratedFile> EmitTests(CompilationResult result, ITestBackend backend, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(backend);

        return EachSource(result, diagnostics, backend.EmitTests);
    }

    private static List<GeneratedFile> EachSource(
        CompilationResult result,
        DiagnosticBag diagnostics,
        Func<IrModule, BackendOptions, DiagnosticBag, IReadOnlyList<GeneratedFile>> emit)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (result.EmittableModule is not { } module)
        {
            throw new ArgumentException(
                "Only a compilation that succeeded may be generated from; see CompilationResult.EmittableModule.",
                nameof(result));
        }

        var files = new List<GeneratedFile>();
        var byPath = new Dictionary<string, GeneratedFile>(StringComparer.Ordinal);

        foreach (var document in result.SyntaxTrees.Select(tree => tree.Document))
        {
            foreach (var file in emit(module.DeclaredIn(document), BackendOptions.For(document, result.Config), diagnostics))
            {
                if (!byPath.TryAdd(file.RelativePath, file))
                {
                    if (byPath[file.RelativePath].Contents != file.Contents)
                    {
                        throw new InvalidOperationException(
                            $"Two sources generated different files named '{file.RelativePath}'.");
                    }

                    continue;
                }

                files.Add(file);
            }
        }

        return files;
    }
}
