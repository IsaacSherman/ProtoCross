using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// The command line compiles every source it is given as one program (#27, spec 5.3): one policy
/// settled across all of them, and each generated into files of its own.
/// </summary>
/// <remarks>
/// The built <c>protocross</c> is run as a process rather than called from this one, because what is
/// under test is what somebody typing the command gets -- its exit code, what it prints and where it
/// writes relative to where it was run -- and a process has one console and one working directory,
/// shared by every test running beside it.
/// </remarks>
public class CliTests
{
    private const string Import = "import proto \"invoice.proto\";";

    private const string CheckedPolicy =
        "<ProtoCross><Arithmetic><Overflow>Checked</Overflow></Arithmetic></ProtoCross>";

    private const string Pricing = $$"""
        {{Import}}

        extend InvoiceItem {
            fn gross() -> int64 { return quantity * unit_price_cents; }
        }
        """;

    /// <summary>Calls into <see cref="Pricing"/>, so it compiles only beside it.</summary>
    private const string Discounts = $$"""
        {{Import}}

        extend InvoiceItem {
            fn net() -> int64 { return gross() - 1; }
        }

        test InvoiceItem.net "one comes off what the other source adds up" {
            receiver {
                quantity = 2;
                unit_price_cents = 5;
            }

            expect return 9;
        }
        """;

    /// <summary>
    /// Runs the built command line in <paramref name="workingDirectory"/>, with the example schemas
    /// on its search path.
    /// </summary>
    private static ProcessResult Run(string workingDirectory, params string[] arguments)
    {
        var dotnet = Toolchain.LocateDotnet();
        if (dotnet is null)
        {
            Assert.Skip("No dotnet host found. Set DOTNET_HOST_PATH or put dotnet on PATH.");
        }

        var protoc = Toolchain.LocateProtoc();
        if (protoc is null)
        {
            Assert.Skip("No protoc executable found. Restore Grpc.Tools or install protoc.");
        }

        var startInfo = ProcessRunner.Create(dotnet, workingDirectory);
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "protocross.dll"));
        foreach (var argument in arguments.Concat(["-I", TestPaths.ExampleProtoDirectory]))
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The protoc this process compiles with, so that what the command line writes can be held
        // up against what the library generates here.
        startInfo.Environment["PROTOCROSS_PROTOC"] = protoc;
        return ProcessRunner.Run(startInfo);
    }

    /// <summary>Every file under <paramref name="root"/>, keyed by its path below it with forward slashes.</summary>
    private static Dictionary<string, string> FilesUnder(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path).Replace('\\', '/'),
                File.ReadAllText,
                StringComparer.Ordinal);

    /// <summary>
    /// What the library generates for <paramref name="sources"/> compiled together, laid out as the
    /// command line lays it out under <c>-o out --test-out tests</c>.
    /// </summary>
    private static Dictionary<string, string> GeneratedByTheLibrary(IReadOnlyList<string> sources)
    {
        var result = Compilation.Compile(sources, [TestPaths.ExampleProtoDirectory]);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var diagnostics = new DiagnosticBag();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var backend in new ITestBackend[] { new CSharpBackend(), new CppBackend() })
        {
            foreach (var file in SourceEmission.Emit(result, backend, diagnostics))
            {
                files.Add($"out/{backend.Name}/{file.RelativePath}", file.Contents);
            }

            foreach (var file in SourceEmission.EmitTests(result, backend, diagnostics))
            {
                files.Add($"tests/{backend.Name}/{file.RelativePath}", file.Contents);
            }
        }

        Assert.Empty(diagnostics);
        return files;
    }

    // ------- one program

    /// <summary>
    /// A call from one source into another resolves, which it can only do if both were bound into one
    /// module: compiled one at a time, the caller names a method that does not exist. The caller is
    /// given first, because sources are not ordered and a caller must not have to follow its callee.
    /// </summary>
    [Fact]
    public void SeveralSourcesCompileAsOneProgram()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("pricing.pcross", Pricing), ("discounts.pcross", Discounts));

        var run = Run(directory, "discounts.pcross", "pricing.pcross", "-o", "out");

        Assert.True(run.ExitCode == 0, run.Output);
    }

    /// <summary>
    /// The command line writes exactly what the library generates for the same sources, file for file
    /// and byte for byte: each source under names of its own, each runtime file once, and a test file
    /// for the source that has tests. It adds nothing and loses nothing on the way to the disk.
    /// </summary>
    [Fact]
    public void WhatTheCommandLineWritesIsWhatTheLibraryGenerates()
    {
        var directory = TestPaths.CreateTempDirectory();
        var sources = TestPaths.WriteSources(directory, ("pricing.pcross", Pricing), ("discounts.pcross", Discounts));

        var run = Run(directory, "pricing.pcross", "discounts.pcross", "-o", "out", "--test-out", "tests");
        Assert.True(run.ExitCode == 0, run.Output);

        var written = FilesUnder(Path.Combine(directory, "out"))
            .Select(file => KeyValuePair.Create("out/" + file.Key, file.Value))
            .Concat(FilesUnder(Path.Combine(directory, "tests"))
                .Select(file => KeyValuePair.Create("tests/" + file.Key, file.Value)))
            .ToDictionary(StringComparer.Ordinal);

        var expected = GeneratedByTheLibrary(sources);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), written.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, contents) in expected)
        {
            Assert.True(contents == written[path], $"'{path}' is not what the library generates for it");
        }
    }

    /// <summary>
    /// With <c>--scaffold</c>, the build file runs the tests of every source that has any, not only
    /// the first: each source's driver is a target of its own in it.
    /// </summary>
    /// <remarks>
    /// That the CMake project turns every driver it is handed into a test is pinned in
    /// <c>ScaffoldTests</c>; what is pinned here is that the command line hands it all of them.
    /// </remarks>
    [Fact]
    public void TheBuildFileRunsTheTestsOfEverySource()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("pricing.pcross", Pricing + """

                test InvoiceItem.gross "quantity times unit price" {
                    receiver {
                        quantity = 2;
                        unit_price_cents = 5;
                    }

                    expect return 10;
                }
                """),
            ("discounts.pcross", Discounts));

        var run = Run(
            directory,
            "pricing.pcross",
            "discounts.pcross",
            "-t",
            "cpp",
            "-o",
            "out",
            "--test-out",
            "tests",
            "--scaffold");
        Assert.True(run.ExitCode == 0, run.Output);

        var testDirectory = Path.Combine(directory, "tests", "cpp");
        var drivers = Directory.GetFiles(testDirectory, "*.tests.cc").Select(Path.GetFileName).ToList();
        Assert.Equal(2, drivers.Count);

        var buildFile = File.ReadAllText(Path.Combine(testDirectory, CppTestProject.FileName));
        foreach (var driver in drivers)
        {
            Assert.True(
                buildFile.Contains($"\"{driver}\"", StringComparison.Ordinal),
                $"the build file does not build {driver}:{Environment.NewLine}{buildFile}");
        }
    }

    /// <summary>
    /// A source that does not compile leaves nothing written for any source, so a build never picks
    /// up one source's new output beside another's stale output.
    /// </summary>
    [Fact]
    public void AnErrorInAnySourceWritesNothingForAny()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("pricing.pcross", Pricing),
            ("discounts.pcross", Discounts.Replace("gross()", "missing()", StringComparison.Ordinal)));

        var run = Run(directory, "pricing.pcross", "discounts.pcross", "-o", "out");

        Assert.True(run.ExitCode == 1, run.Output);
        Assert.False(
            Directory.Exists(Path.Combine(directory, "out")),
            "pricing.pcross compiles on its own, and nothing of it may be written when discounts.pcross does not");
    }

    // ------- policy

    /// <summary>
    /// Two sources under different <c>protocross.config.xml</c> files are refused (<c>PC2005</c>)
    /// rather than both compiled under whichever the first source found.
    /// </summary>
    [Fact]
    public void SourcesThatFindDifferentConfigFilesAreRefused()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("checked/protocross.config.xml", CheckedPolicy),
            ("checked/pricing.pcross", Pricing),
            ("unstated/discounts.pcross", Discounts));

        var run = Run(directory, "checked/pricing.pcross", "unstated/discounts.pcross", "-o", "out");

        Assert.True(run.ExitCode == 2, run.Output);
        Assert.Contains(DiagnosticCodes.SourcesDisagreeOnPolicy.Code, run.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(directory, "out")), "a refused compilation writes nothing");
    }

    /// <summary>
    /// A config file named with <c>--config</c> governs every source, wherever each would have
    /// searched from (spec 10.4), so sources that would disagree compile, and all under that file.
    /// </summary>
    [Fact]
    public void AConfigNamedOnTheCommandLineGovernsEverySource()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(
            directory,
            ("checked/protocross.config.xml", CheckedPolicy),
            ("checked/pricing.pcross", Pricing),
            ("unstated/discounts.pcross", Discounts));

        var run = Run(
            directory,
            "checked/pricing.pcross",
            "unstated/discounts.pcross",
            "--config",
            "checked/protocross.config.xml",
            "-t",
            "csharp",
            "-o",
            "out");

        Assert.True(run.ExitCode == 0, run.Output);
        foreach (var source in new[] { "pricing", "discounts" })
        {
            var generated = File.ReadAllText(Path.Combine(directory, "out", "csharp", source + ".g.cs"));
            Assert.True(
                generated.Contains("integer overflow = Checked", StringComparison.Ordinal),
                $"{source}.pcross was not generated under the policy --config named");
        }
    }

    // ------- arguments

    /// <summary>Every source that is not there is named, not only the first, so one run lists every path to fix.</summary>
    [Fact]
    public void EveryMissingSourceIsNamed()
    {
        var directory = TestPaths.CreateTempDirectory();

        var run = Run(directory, "first-missing.pcross", "second-missing.pcross", "-o", "out");

        Assert.True(run.ExitCode == 2, run.Output);
        Assert.Contains("first-missing.pcross", run.Output, StringComparison.Ordinal);
        Assert.Contains("second-missing.pcross", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A source given twice reaches the compiler twice and is refused there (<c>PC2006</c>, spec 5.3),
    /// rather than being quietly compiled once, which would hide a mistake in whatever built the
    /// command.
    /// </summary>
    [Fact]
    public void OneSourceGivenTwiceIsRefused()
    {
        var directory = TestPaths.CreateTempDirectory();
        TestPaths.WriteSources(directory, ("pricing.pcross", Pricing));

        var run = Run(directory, "pricing.pcross", "pricing.pcross", "-o", "out");

        Assert.True(run.ExitCode == 1, run.Output);
        Assert.Contains(DiagnosticCodes.SourcesShareGeneratedNames.Code, run.Output, StringComparison.Ordinal);
    }
}
