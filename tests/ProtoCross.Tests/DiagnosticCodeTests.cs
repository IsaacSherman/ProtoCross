using System.Text.RegularExpressions;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// The diagnostic code tables as a set: no code allocated twice, none outside the range spec 26
/// gives its layer, no gaps, and no code named in the specification or the documentation that
/// nothing raises.
/// </summary>
/// <remarks>
/// These are the properties a scattered set of string literals could not be asked about at all, which
/// is why #65 collected them into a table. They are checked here rather than in the compiler because
/// enumerating the tables means reflection, which belongs nowhere near a path that runs per keystroke.
/// The source and documentation scans are the other half: a table nobody is obliged to use is a
/// suggestion, and the codes the specification quotes are a contract that can only drift silently.
/// </remarks>
public class DiagnosticCodeTests
{
    /// <summary>A <c>PC####</c> code written as a string literal.</summary>
    private const string QuotedCode = "\"PC[0-9]{4}\"";

    /// <summary>The compiler's own sources, which are where a diagnostic is raised from.</summary>
    /// <remarks>
    /// The two tables are excluded because they are where the codes are supposed to be spelled. Build
    /// output is excluded because a generated attribute file is not a raise site and scanning it would
    /// make the test's answer depend on whether the tree had been built.
    /// </remarks>
    private static IEnumerable<string> CompilerSources()
        => Directory
            .EnumerateFiles(Path.Combine(TestPaths.RepositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Where(path => Path.GetFileName(path) is not ("DiagnosticCodes.cs" or "HostDiagnosticCodes.cs"));

    private static bool IsBuildOutput(string path)
    {
        var separator = Path.DirectorySeparatorChar;
        return path.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
            || path.Contains($"{separator}obj{separator}", StringComparison.Ordinal);
    }

    private static string Relative(string path)
        => Path.GetRelativePath(TestPaths.RepositoryRoot, path).Replace('\\', '/');

    // ------------------------------------------------------- the tables are the only spelling

    /// <summary>Every diagnostic is raised through a descriptor rather than a code written by hand.</summary>
    /// <remarks>
    /// A literal at a raise site is how one code came to carry two titles: nothing connects the two
    /// places, so nothing can notice that they disagree. This also keeps the tables complete, which is
    /// what every other test in this class is asking about.
    /// </remarks>
    [Fact]
    public void NoRaiseSiteSpellsACodeByHand()
    {
        var spelled = new List<string>();

        foreach (var path in CompilerSources())
        {
            var number = 0;

            foreach (var line in File.ReadLines(path))
            {
                number++;

                if (Regex.IsMatch(line, QuotedCode))
                {
                    spelled.Add($"{Relative(path)}:{number}");
                }
            }
        }

        Assert.True(
            spelled.Count == 0,
            "a diagnostic code is a string literal at these sites, which should name a descriptor "
                + $"from DiagnosticCodes or HostDiagnosticCodes instead:\n{string.Join("\n", spelled)}");
    }
}
