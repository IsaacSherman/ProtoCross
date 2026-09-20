using System.Reflection;
using System.Text.RegularExpressions;
using ProtoCross.Diagnostics;
using ProtoCross.LanguageServer.Workspace;
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

    /// <summary>A <c>PC####</c> code named anywhere in prose.</summary>
    private const string AnyCode = "PC[0-9]{4}";

    /// <summary>Two codes joined by a dash, which is how spec 26 writes the bounds of a range.</summary>
    private const string CodeRange = "PC[0-9]{4}`?\\s*[-–—]\\s*`?PC[0-9]{4}";

    /// <summary>The ranges spec 26 assigns, and the table the layer owning each one raises from.</summary>
    /// <remarks>
    /// Written out here rather than read from the specification: a test that parsed the table it is
    /// checking against would agree with whatever that table said, including a typo. Two of these
    /// ranges are empty, because PC1001 and PC1101 left with <c>virtual</c> in #10 and no backend has
    /// raised a diagnostic since.
    /// </remarks>
    private static readonly Layer[] Layers =
    [
        new("the compiler front end", 1, 999, nameof(DiagnosticCodes)),
        new("the C# backend", 1001, 1099, nameof(DiagnosticCodes)),
        new("the C++ backend", 1101, 1199, nameof(DiagnosticCodes)),
        new("the driver and the configuration file", 2001, 2099, nameof(DiagnosticCodes)),
        new("host configuration", 2100, 2199, nameof(HostDiagnosticCodes)),
    ];

    /// <summary>One row of spec 26's range table.</summary>
    private sealed record Layer(string Owner, int First, int Last, string Table);

    /// <summary>Every descriptor either table declares, paired with the table it came from.</summary>
    private static IReadOnlyList<(string Table, DiagnosticDescriptor Descriptor)> Descriptors { get; } =
    [
        .. DescriptorsOf(typeof(DiagnosticCodes)),
        .. DescriptorsOf(typeof(HostDiagnosticCodes)),
    ];

    private static IEnumerable<(string Table, DiagnosticDescriptor Descriptor)> DescriptorsOf(Type table)
        => table
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(DiagnosticDescriptor))
            .Select(field => (table.Name, (DiagnosticDescriptor)field.GetValue(null)!));

    /// <summary>The number in a code, which is what the ranges are stated in.</summary>
    private static int Number(DiagnosticDescriptor descriptor)
        => int.Parse(descriptor.Code[2..], System.Globalization.CultureInfo.InvariantCulture);

    private static IEnumerable<DiagnosticDescriptor> Within(Layer layer)
        => Descriptors
            .Select(entry => entry.Descriptor)
            .Where(descriptor => Number(descriptor) >= layer.First && Number(descriptor) <= layer.Last);

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

    /// <summary>The documents that name diagnostic codes in prose, and so make them a contract.</summary>
    /// <remarks>
    /// Named roots rather than every markdown file in the tree: the extension's dependencies and the
    /// worktrees a parallel session leaves behind are both full of markdown nobody here wrote, and a
    /// scan of them would be slow and would fail on somebody else's numbering.
    /// </remarks>
    private static IEnumerable<string> Documentation()
    {
        string[] directories = ["ProtoCross_Spec", "docs"];
        string[] files = ["README.md", "ARCHITECTURE.md", "CLAUDE.md", Path.Combine("tests", "conformance", "README.md")];

        foreach (var directory in directories)
        {
            foreach (var path in Directory.EnumerateFiles(
                Path.Combine(TestPaths.RepositoryRoot, directory), "*.md", SearchOption.AllDirectories))
            {
                yield return path;
            }
        }

        foreach (var file in files)
        {
            yield return Path.Combine(TestPaths.RepositoryRoot, file);
        }
    }

    // ------------------------------------------------------- the set of codes

    /// <summary>One code means one thing, so no two descriptors may carry it.</summary>
    /// <remarks>
    /// This is the invariant the whole table exists for. Two sites raising one code could disagree
    /// about its title, and PC0015 and PC2103 both did; two descriptors carrying one code would put
    /// that back, one table away from where a reader would look for it.
    /// </remarks>
    [Fact]
    public void NoTwoDescriptorsShareACode()
    {
        var shared = Descriptors
            .GroupBy(entry => entry.Descriptor.Code, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} is declared {group.Count()} times");

        Assert.Empty(shared);
    }

    /// <summary>A code retired with the feature that raised it is never handed to something else.</summary>
    /// <remarks>
    /// A code is how a reader finds an explanation, so a reused one makes every older account of it
    /// wrong -- including the specification's, which still names PC1001 and PC1101 as the diagnostics
    /// the backends raised for <c>virtual</c>.
    /// </remarks>
    [Fact]
    public void NoRetiredCodeIsBackInATable()
        => Assert.All(
            Descriptors,
            entry => Assert.False(
                DiagnosticCodes.Retired.Contains(entry.Descriptor.Code),
                $"{entry.Descriptor.Code} was retired and has been allocated again, to '{entry.Descriptor.Title}'"));

    /// <summary>Every code sits in the range spec 26 gives the layer that raises it.</summary>
    [Fact]
    public void EveryCodeLiesInTheRangeSpec26AssignsItsLayer()
        => Assert.All(Descriptors, entry =>
        {
            var number = Number(entry.Descriptor);
            var layer = Layers.FirstOrDefault(range => number >= range.First && number <= range.Last);

            Assert.True(layer is not null, $"{entry.Descriptor.Code} is in none of spec 26's ranges");
            Assert.True(
                layer!.Table == entry.Table,
                $"{entry.Descriptor.Code} is declared in {entry.Table}, but spec 26 gives its range to "
                    + $"{layer.Owner}, which raises from {layer.Table}");
        });

    /// <summary>
    /// The codes in use in a range run from its first one with no gaps, so the next code to allocate
    /// is the one after the last.
    /// </summary>
    /// <remarks>
    /// A gap is either a code that was retired without being recorded as retired, or a code somebody
    /// skipped, and both make the table stop answering the question it is kept for. The run starts at
    /// the first code in use rather than at the bound of the range, because the host range begins at
    /// PC2100 and its first code is PC2101.
    /// </remarks>
    [Fact]
    public void EachRangeIsContiguousFromItsFirstCode()
        => Assert.All(Layers, layer =>
        {
            var numbers = Within(layer).Select(Number).Order().ToList();

            if (numbers.Count == 0)
            {
                return;
            }

            var missing = Enumerable
                .Range(numbers[0], numbers[^1] - numbers[0] + 1)
                .Except(numbers)
                .Select(number => $"PC{number:D4}");

            Assert.True(
                !missing.Any(),
                $"the codes {layer.Owner} raises skip {string.Join(", ", missing)}");
        });

    /// <summary>Every code the specification or the documentation names is one that exists.</summary>
    /// <remarks>
    /// The codes are published in two places, and prose is the half that cannot fail a build. A rule
    /// that moves to a different code, or a diagnostic that is removed, leaves the document pointing
    /// at a number that means nothing -- which is exactly what a reader hitting the diagnostic is
    /// trying to look up. A code deliberately gone is recorded in <see cref="DiagnosticCodes.Retired"/>
    /// and may still be written about; a range bound is not a code at all, so the bounds that state
    /// spec 26's ranges are struck out before the line is read.
    /// </remarks>
    [Fact]
    public void EveryCodeTheSpecOrDocsNameExists()
    {
        var known = Descriptors.Select(entry => entry.Descriptor.Code).ToHashSet(StringComparer.Ordinal);
        var dangling = new List<string>();

        foreach (var path in Documentation())
        {
            var number = 0;

            foreach (var line in File.ReadLines(path))
            {
                number++;

                // The bounds are struck out of the line rather than the line being skipped, so that a
                // real code written beside a range -- which is how spec 26's table would say which
                // codes a range currently holds -- is still checked.
                foreach (Match match in Regex.Matches(Regex.Replace(line, CodeRange, string.Empty), AnyCode))
                {
                    if (!known.Contains(match.Value) && !DiagnosticCodes.Retired.Contains(match.Value))
                    {
                        dangling.Add($"{Relative(path)}:{number} names {match.Value}");
                    }
                }
            }
        }

        Assert.True(
            dangling.Count == 0,
            $"these name a diagnostic code nothing raises:\n{string.Join("\n", dangling)}");
    }

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
