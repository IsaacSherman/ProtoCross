using ProtoCross.Diagnostics;
using ProtoCross.Ir;

namespace ProtoCross.Backend;

/// <summary>
/// Generates a compilation one source at a time, each into files named after it (spec 5.3), and
/// divides what is generated between the behavior output and the test output (spec 25.3.1).
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
/// <para>
/// Which output a source's behavior goes to is its <see cref="SourceRole"/>. A production source's
/// goes to the behavior output, including one that holds only tests, whose files then declare
/// nothing: that is what every source was before a project could say otherwise, and it is still
/// what a source is when nothing says otherwise. A test source's goes to the test output, beside the
/// tests that are the only code allowed to call it. Every source's tests go to the test output.
/// </para>
/// </remarks>
public static class SourceEmission
{
    /// <summary>What <paramref name="backend"/> generates for the behavior of every production source.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="result"/> did not succeed, so there is no module anything may be generated
    /// from, or it does not say which sources it holds.
    /// </exception>
    public static IReadOnlyList<GeneratedFile> Emit(CompilationResult result, IBackend backend, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(backend);

        var files = new GeneratedFiles();
        files.AddEach(result, source => source.Role is SourceRole.Production, diagnostics, backend.Emit);
        return files.Gathered;
    }

    /// <summary>
    /// What <paramref name="backend"/> generates for the tests of every source that has any, and for
    /// the behavior of every test source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A test source's behavior comes out here because the tests are what call it, and nothing
    /// generated into the behavior output may (<c>PC0088</c>). It comes with the runtime file every
    /// source's behavior does, and the behavior output holds that file already. A build that
    /// compiles both directories into one program -- the C# project <c>--scaffold</c> writes is one
    /// -- would then define its types twice, so whatever the behavior output holds is left out of
    /// this one.
    /// </para>
    /// <para>
    /// The behavior output is generated again to find out what it holds, and only its file names and
    /// contents are kept. Whatever generating it reported belongs to the caller that asked for it, and
    /// reporting it here as well would say it twice. With no test source there is nothing to leave out,
    /// and it is not generated at all.
    /// </para>
    /// </remarks>
    /// <inheritdoc cref="Emit" path="/exception"/>
    public static IReadOnlyList<GeneratedFile> EmitTests(CompilationResult result, ITestBackend backend, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(result);

        var hasTestSources = result.SyntaxTrees.Any(source => source.Role is SourceRole.Test);
        var files = new GeneratedFiles(elsewhere: hasTestSources ? Emit(result, backend, new DiagnosticBag()) : []);

        files.AddEach(result, _ => true, diagnostics, backend.EmitTests);
        files.AddEach(result, source => source.Role is SourceRole.Test, diagnostics, backend.Emit);
        return files.Gathered;
    }

    /// <summary>Generated files gathered from several sources, each name once.</summary>
    /// <param name="elsewhere">
    /// Files already written to another output. One of these generated again is left out, as a
    /// second copy within this output is.
    /// </param>
    private sealed class GeneratedFiles(IReadOnlyList<GeneratedFile>? elsewhere = null)
    {
        private readonly Dictionary<string, GeneratedFile> _byPath =
            (elsewhere ?? []).ToDictionary(file => file.RelativePath, StringComparer.Ordinal);

        private readonly List<GeneratedFile> _gathered = [];

        public IReadOnlyList<GeneratedFile> Gathered => _gathered;

        /// <summary>Generates every source <paramref name="included"/> accepts, in the compilation's order.</summary>
        public void AddEach(
            CompilationResult result,
            Func<SourceTree, bool> included,
            DiagnosticBag diagnostics,
            Func<IrModule, BackendOptions, DiagnosticBag, IReadOnlyList<GeneratedFile>> emit)
        {
            ArgumentNullException.ThrowIfNull(diagnostics);

            var module = EmittableModuleOf(result);

            foreach (var document in result.SyntaxTrees.Where(included).Select(source => source.Document))
            {
                foreach (var file in emit(module.DeclaredIn(document), BackendOptions.For(document, result.Config), diagnostics))
                {
                    Add(file);
                }
            }
        }

        private void Add(GeneratedFile file)
        {
            if (_byPath.TryGetValue(file.RelativePath, out var existing))
            {
                if (existing.Contents != file.Contents)
                {
                    throw new InvalidOperationException(
                        $"Two sources generated different files named '{file.RelativePath}'.");
                }

                return;
            }

            _byPath.Add(file.RelativePath, file);
            _gathered.Add(file);
        }
    }

    /// <summary>The module <paramref name="result"/> may be generated from.</summary>
    /// <inheritdoc cref="Emit" path="/exception"/>
    private static IrModule EmittableModuleOf(CompilationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.EmittableModule is not { } module)
        {
            throw new ArgumentException(
                "Only a compilation that succeeded may be generated from; see CompilationResult.EmittableModule.",
                nameof(result));
        }

        // A module with no sources beside it was built by hand rather than compiled, and generating
        // nothing from it would read as a compilation with nothing in it.
        if (result.SyntaxTrees.Count == 0)
        {
            throw new ArgumentException(
                "The compilation does not say which sources it holds; see CompilationResult.SyntaxTrees.",
                nameof(result));
        }

        return module;
    }
}
