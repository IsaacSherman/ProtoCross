using System.Text;
using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// The include guards on the C++ headers the backend generates: each is named after the file it
/// guards, and the three directives that spell it agree.
/// </summary>
/// <remarks>
/// Nothing asked this before, which is how the library header came to be guarded by a macro ending
/// in <c>_PL_H_</c> long after the project stopped being called ProtoLang. The guard is built from
/// the header's extension, and that extension is spelled in one place and copied into the guard, so
/// renaming <c>.pl.h</c> to <c>.pc.h</c> moved one of the two copies. A guard is published output --
/// it is what a consumer reads in their own build, and what decides whether two generated headers
/// can be included together -- so it is worth a test that fails when the two disagree rather than a
/// name somebody eventually notices.
/// </remarks>
public class CppIncludeGuardTests
{
    /// <summary>Every header one C++ emission produces, which is the library header and the runtime.</summary>
    /// <remarks>
    /// A sweep over whatever was emitted rather than a check of one named file: a third generated
    /// header would be covered the day it exists, which is the case this test is really about.
    /// </remarks>
    private static IReadOnlyList<GeneratedFile> Headers()
    {
        var result = Compilation.Compile(TestPaths.SimpleScript, [TestPaths.ExampleProtoDirectory]);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var diagnostics = new DiagnosticBag();
        var files = new CppBackend().Emit(
            result.Module!, new BackendOptions(Path.GetFileName(TestPaths.SimpleScript)), diagnostics);
        Assert.Empty(diagnostics);

        var headers = files
            .Where(file => file.RelativePath.EndsWith(".h", StringComparison.Ordinal))
            .ToList();

        Assert.True(headers.Count > 0, "the C++ backend emitted no header at all, so this proves nothing");
        return headers;
    }

    private static IEnumerable<string> Lines(GeneratedFile file)
        => file.Contents.Split('\n').Select(line => line.TrimEnd('\r'));

    /// <summary>The macro a header names itself by, read back out of its own first directive.</summary>
    private static string GuardOf(GeneratedFile header)
    {
        const string Directive = "#ifndef ";

        var line = Lines(header).FirstOrDefault(line => line.StartsWith(Directive, StringComparison.Ordinal));

        Assert.True(line is not null, $"{header.RelativePath} has no include guard at all");
        return line![Directive.Length..];
    }

    /// <summary>A file name as a macro spells it: upper case, with anything else an underscore.</summary>
    private static string SpelledForAMacro(string fileName)
    {
        var spelled = new StringBuilder();

        foreach (var c in fileName)
        {
            spelled.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        }

        spelled.Append('_');
        return spelled.ToString();
    }

    /// <summary>A header's guard ends in the name of the file it guards, and is qualified by the generator.</summary>
    /// <remarks>
    /// Stated as two halves because they answer to different things. The ending is what keeps the
    /// guard true of the file: rename the file and the guard has to follow, which is exactly what
    /// did not happen. The prefix is what keeps it unique in somebody else's build, where a macro
    /// called <c>CASTS_PC_H_</c> is a collision waiting to happen.
    /// </remarks>
    [Fact]
    public void EveryHeaderIsGuardedByTheNameOfTheFileItGuards()
        => Assert.All(Headers(), header =>
        {
            var guard = GuardOf(header);
            var name = SpelledForAMacro(Path.GetFileName(header.RelativePath));

            Assert.True(
                guard.EndsWith(name, StringComparison.Ordinal),
                $"{header.RelativePath} is guarded by {guard}, which does not end in {name}");
            Assert.StartsWith("PROTOCROSS_", guard, StringComparison.Ordinal);
        });

    /// <summary>The three directives spelling a guard say the same thing.</summary>
    /// <remarks>
    /// A <c>#define</c> that disagrees with its <c>#ifndef</c> defeats the guard silently: the header
    /// is included twice and the second copy redefines everything. A mismatched <c>#endif</c> comment
    /// only misleads a reader, but it misleads them about this.
    /// </remarks>
    [Fact]
    public void AGuardIsSpelledTheSameWayInEveryDirectiveThatNamesIt()
        => Assert.All(Headers(), header =>
        {
            var guard = GuardOf(header);
            var lines = Lines(header).ToList();

            Assert.Contains($"#define {guard}", lines);
            Assert.Contains($"#endif  // {guard}", lines);
        });
}
