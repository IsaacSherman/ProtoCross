using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Conformance;
using Xunit;

namespace ProtoCross.Tests;

public partial class BackendTests
{
    // ------- every namespace of a header declared before any is defined (#135, spec 24.2)

    /// <summary>
    /// In every header the corpus generates, every function and factory member is declared before
    /// any is defined, whichever namespace each is in (spec 24.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A header without a project declares each method beside its message, so one source extending
    /// messages of two packages writes two namespaces into one header. Written a namespace at a time,
    /// the first namespace's definitions called functions the second had not declared yet (#135).
    /// The C++ conformance build would refuse that header, but it runs only where a protobuf C++
    /// install is found, and this asks the same of the text on every machine.
    /// </para>
    /// <para>
    /// Swept over the corpus rather than asked of one vector, because a header takes one of two
    /// layouts by how many namespaces it writes and whether it calls another source, and the corpus
    /// holds each kind: one namespace, two, a source calling another, and a source of tests alone.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryHeaderDeclaresEveryFunctionBeforeDefiningAny()
    {
        foreach (var vector in ConformanceVectors.HandWritten)
        {
            var result = ConformanceVectors.Compile(vector);
            var headers = SourceEmission.Emit(result, new CppBackend(), new DiagnosticBag())
                .Where(file => file.RelativePath.EndsWith(".pc.h", StringComparison.Ordinal));

            foreach (var header in headers)
            {
                var lines = header.Contents.Split('\n').Select(line => line.Trim()).ToList();
                var lastDeclaration = lines.FindLastIndex(IsDeclaration);
                var firstDefinition = lines.FindIndex(IsDefinition);

                // A source of tests alone generates a header that defines nothing.
                if (firstDefinition >= 0 && lastDeclaration > firstDefinition)
                {
                    Assert.Fail(
                        $"{vector.Name}: {header.RelativePath} declares '{lines[lastDeclaration]}' on line "
                        + $"{lastDeclaration + 1}, after defining '{lines[firstDefinition]}' on line {firstDefinition + 1}.");
                }
            }
        }
    }

    /// <summary>A function's declaration, or a factory's member declared in its struct.</summary>
    private static bool IsDeclaration(string line)
        => (line.StartsWith("inline ", StringComparison.Ordinal) || line.StartsWith("static ", StringComparison.Ordinal))
            && line.EndsWith(';');

    /// <summary>The first line of a function's definition, or of a factory member's.</summary>
    private static bool IsDefinition(string line)
        => line.StartsWith("inline ", StringComparison.Ordinal) && !line.EndsWith(';');
}
