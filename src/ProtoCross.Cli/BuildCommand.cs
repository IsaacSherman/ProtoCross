using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Config;
using ProtoCross.Diagnostics;
using ProtoCross.Projects;

namespace ProtoCross.Cli;

/// <summary>
/// <c>protocross build</c>: compiles the sources or the project the command line names, and writes
/// what each backend it asks for generates.
/// </summary>
/// <remarks>
/// Generating and writing are two steps, and every backend has generated before anything is written.
/// A <c>protoc</c> plugin (#8) builds as this does and writes nothing, handing what was generated back
/// to <c>protoc</c>, so what a build generates is a list of files rather than writes made on the way.
/// </remarks>
internal static class BuildCommand
{
    /// <summary>What a build compiled, the path of every file it wrote, and the project it built, if any.</summary>
    internal sealed record Built(CompilationResult Result, IReadOnlyList<string> Written, ProtoCrossProject? Project);

    public static int Run(string[] args)
    {
        var options = CommandLineOptions.Parse(args);

        if (options is null)
        {
            CommandLineOptions.PrintUsage();
            return 2;
        }

        if (!CanBuild(options))
        {
            return 2;
        }

        if (Build(options, out var exitCode) is not { } built)
        {
            return exitCode;
        }

        foreach (var path in built.Written)
        {
            Console.WriteLine(Path.GetRelativePath(Directory.GetCurrentDirectory(), path));
        }

        return 0;
    }

    /// <summary>
    /// Whether what <paramref name="options"/> asks for can be attempted: every source it names is there,
    /// and <c>--scaffold</c> has somewhere to write. Says what is wrong when it cannot; a command line it
    /// refuses ends with exit code 2.
    /// </summary>
    /// <remarks>
    /// Apart from <see cref="Build"/>, because <c>protocross test</c> asks it before looking for any tool.
    /// A source that is not there is then the usage error it is for a build, whatever the machine has
    /// installed, rather than a path tool discovery trips over.
    /// </remarks>
    internal static bool CanBuild(CommandLineOptions options)
    {
        var missing = options.SourcePaths.Where(path => !File.Exists(path)).ToList();
        foreach (var path in missing)
        {
            Console.Error.WriteLine($"error: source file not found: {path}");
        }

        if (missing.Count > 0)
        {
            return false;
        }

        if (options.Scaffold && options.TestOutputDirectory is null)
        {
            Console.Error.WriteLine("error: --scaffold needs --test-out, because it writes the build file beside the generated tests");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Compiles what <paramref name="options"/> names, once <see cref="CanBuild"/> has accepted them, and
    /// writes what each backend generates from it, printing every diagnostic on the way: what was built,
    /// or null with the exit code that says why nothing was.
    /// </summary>
    /// <remarks>
    /// Shared with <c>protocross test</c>, which builds exactly what a build of the same command line
    /// would and then runs it, so that what it tests is what a build ships.
    /// </remarks>
    internal static Built? Build(CommandLineOptions options, out int exitCode)
    {
        exitCode = 2;

        var setupDiagnostics = new DiagnosticBag();
        var project = options.ProjectPath is { } projectPath ? ProtoCrossProject.Load(projectPath, setupDiagnostics) : null;
        var compilation = options.ProjectPath is null
            ? CompilationOfSources(options, setupDiagnostics)
            : project is null ? null : CompilationOfProject(project, options, setupDiagnostics);
        PrintDiagnostics(setupDiagnostics);

        if (compilation is null)
        {
            Console.Error.WriteLine(options.ProjectPath is null ? "configuration failed" : "project failed");
            return null;
        }

        exitCode = 1;

        var result = compilation.Compile();
        PrintDiagnostics(result.Diagnostics);

        // The one module a compiler may write from. A partial one comes out of a buffer that did not
        // parse, which is what an editor asks for and what nothing here may emit.
        if (result.EmittableModule is null)
        {
            Console.Error.WriteLine($"compilation failed: {result.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)} error(s)");
            return null;
        }

        var backendDiagnostics = new DiagnosticBag();
        var written = Write(Generate(result, options, project, backendDiagnostics));
        PrintDiagnostics(backendDiagnostics);

        if (backendDiagnostics.HasErrors)
        {
            Console.Error.WriteLine("code generation failed");
            return null;
        }

        exitCode = 0;
        return new Built(result, written, project);
    }

    /// <summary>What each backend this run asks for generates, and the directory each part goes in.</summary>
    private static IReadOnlyList<OutputGroup> Generate(
        CompilationResult result,
        CommandLineOptions options,
        ProtoCrossProject? project,
        DiagnosticBag diagnostics)
    {
        var groups = new List<OutputGroup>();

        foreach (var backend in BackendsFor(options))
        {
            // Each source into files named after it, with the policy it ran under in every header, so a
            // reader can tell what produced the code in front of them without re-running the compiler.
            var outputDirectory = Path.Combine(options.OutputDirectory, backend.Name);
            groups.Add(new(outputDirectory, OutputRole.Program, SourceEmission.Emit(result, backend, diagnostics)));

            if (options.TestOutputDirectory is null || backend is not ITestBackend testBackend)
            {
                continue;
            }

            var testOutputDirectory = Path.Combine(options.TestOutputDirectory, backend.Name);
            var testFiles = SourceEmission.EmitTests(result, testBackend, diagnostics);
            groups.Add(new(testOutputDirectory, OutputRole.Tests, testFiles));

            if (options.Scaffold && backend is ITestProjectScaffold scaffold)
            {
                var scaffoldOptions = ScaffoldOptions.Create(
                    result.SearchPaths,
                    result.Descriptors,
                    outputDirectory,
                    testOutputDirectory,
                    testFiles.Select(file => file.RelativePath).ToList()) with
                {
                    Settings = ScaffoldSettingsFor(project, options),
                };

                groups.Add(new(testOutputDirectory, OutputRole.TestProject, scaffold.EmitTestProject(scaffoldOptions, diagnostics)));
            }
        }

        return groups;
    }

    /// <summary>The backends this run generates for: C# before C++, in whichever order <c>-t</c> named them.</summary>
    private static IEnumerable<IBackend> BackendsFor(CommandLineOptions options)
    {
        if (options.Targets.Contains("csharp"))
        {
            yield return new CSharpBackend();
        }

        if (options.Targets.Contains("cpp"))
        {
            yield return new CppBackend();
        }
    }

    /// <summary>Writes each group's files into its directory, and says where each file went.</summary>
    /// <remarks>
    /// A directory is created even when nothing goes in it, so that a build leaves <c>&lt;out&gt;/&lt;target&gt;/</c>
    /// behind for every target it was asked for, whatever that target generated.
    /// </remarks>
    private static IReadOnlyList<string> Write(IReadOnlyList<OutputGroup> groups)
    {
        var written = new List<string>();

        foreach (var group in groups)
        {
            Directory.CreateDirectory(group.Directory);

            foreach (var file in group.Files)
            {
                var path = Path.Combine(group.Directory, file.RelativePath);
                File.WriteAllText(path, file.Contents);
                written.Add(path);
            }

            if (group.Role is OutputRole.TestProject)
            {
                WarnOfOtherBuildFiles(group.Directory, group.Files);
            }
        }

        return written;
    }

    /// <summary>The sources named on the command line, compiled as one program.</summary>
    /// <remarks>
    /// Every one of them is a production source: only a project can say that a source is there only to
    /// test with (spec 25.3.1).
    /// </remarks>
    private static Compilation? CompilationOfSources(CommandLineOptions options, DiagnosticBag diagnostics)
    {
        var config = ConfigOfSources(options, diagnostics) is { } found ? ApplyPolicyFlags(found, options) : null;

        return config is null
            ? null
            : new Compilation(
                [.. options.SourcePaths.Select(SourceDocument.ReadFrom)],
                OptionsFor(options, options.IncludePaths, config));
    }

    /// <summary>The compilation a project describes, for the build this run asks for (spec 5.4).</summary>
    /// <remarks>
    /// <para>
    /// A project that cannot be read compiles nothing, rather than the part of it that could be read: a
    /// project missing one of its lines would build a program nobody wrote. So this is handed only a
    /// project that was read. Which projects no build may compile once read is
    /// <see cref="ProjectSources.ExpandForBuild"/>'s to say, because an editor has to refuse exactly the
    /// ones this refuses.
    /// </para>
    /// <para>
    /// The project's schema directories are searched before <c>-I</c>'s. They are the project's answer,
    /// the same on every machine that builds it; a directory named for one run adds to them rather than
    /// changing what the project's imports mean.
    /// </para>
    /// </remarks>
    private static Compilation? CompilationOfProject(ProtoCrossProject project, CommandLineOptions options, DiagnosticBag diagnostics)
    {
        if (ProjectSources.ExpandForBuild(project, diagnostics) is not { } files)
        {
            return null;
        }

        var members = options.BuildsTests ? files.TestBuild : files.ProductionBuild;
        var config = ProjectPolicy.Resolve(project, members, diagnostics) is { } found ? ApplyPolicyFlags(found, options) : null;

        return config is null
            ? null
            : new Compilation(
                [.. members.Select(member => SourceDocument.ReadFrom(member.Path) with { Role = member.Role })],
                OptionsFor(options, [.. project.ProtoPaths.Select(protoPath => protoPath.Path), .. options.IncludePaths], config)
                    with { ProjectNamespace = project.Namespace });
    }

    /// <summary>What a compilation needs besides its sources, for the build this run asks for.</summary>
    /// <remarks>
    /// Without <c>--test-out</c> nothing is generated from a test, so nothing about one is checked either
    /// (spec 25.3.1): a test that no longer binds must not stop the program shipping. With it, the tests
    /// are part of what has to compile, and one that does not leaves nothing written, the program's
    /// output included.
    /// </remarks>
    private static CompilationOptions OptionsFor(CommandLineOptions options, IReadOnlyList<string> includePaths, ProjectConfig config)
        => new()
        {
            IncludePaths = includePaths,
            Config = config,
            SkipTests = !options.BuildsTests,
        };

    /// <summary>The configuration file that governs sources named on the command line (spec 10.4).</summary>
    private static ProjectConfig? ConfigOfSources(CommandLineOptions options, DiagnosticBag diagnostics)
    {
        if (options.NoConfig)
        {
            return ProjectConfig.Default;
        }

        if (options.ConfigPath is not { } explicitPath)
        {
            // The same discovery the compiler would have done, asked for by name rather than repeated
            // here, so the CLI and the library can never disagree about which file settles the policy.
            return Compilation.ResolveSharedConfig([.. options.SourcePaths.Select(SourceIdentity.FromPath)], diagnostics);
        }

        if (!File.Exists(explicitPath))
        {
            Console.Error.WriteLine($"error: configuration file not found: {explicitPath}");
            return null;
        }

        return ProjectConfig.Load(explicitPath, diagnostics);
    }

    /// <summary>
    /// Applies the command line's policy flags to the policy a configuration file settled (spec 10.4).
    /// </summary>
    /// <remarks>
    /// The config file wins. A flag that contradicts a setting the file states is refused rather than
    /// silently applied, because the point of tracking policy in the repository is that the generated
    /// code means the same thing however it was built. <c>--override-config</c> exists so that trying
    /// another policy stays one flag away, while leaving a trace in the command that no one can
    /// mistake for the project's own answer.
    /// </remarks>
    private static ProjectConfig? ApplyPolicyFlags(ProjectConfig config, CommandLineOptions options)
    {
        if (options.Overflow is not { } overflow)
        {
            return config;
        }

        if (!config.TryOverrideOverflow(overflow, options.OverrideConfig, out var overridden, out var conflict))
        {
            Console.Error.WriteLine($"error: {conflict}");
            Console.Error.WriteLine(
                "       The config file wins, so that a build means the same thing however it was run.");

            // The flag answers one setting in the file's place, so it cannot lift a refusal from another.
            if (!options.OverrideConfig && !config.Forbids(overflow))
            {
                Console.Error.WriteLine("       Pass --override-config to use the flag anyway.");
            }

            return null;
        }

        return overridden;
    }

    /// <summary>What the test projects <c>--scaffold</c> writes are named and target (spec 25.3.2).</summary>
    /// <remarks>
    /// A flag stands over the project, unlike a policy flag, which the configuration file refuses. Policy
    /// decides what the generated code means, so a build that changed it quietly would ship different
    /// behavior; a test project's name and framework change only what builds the tests, and trying another
    /// for one build is what the flags are for.
    /// </remarks>
    private static ScaffoldSettings ScaffoldSettingsFor(ProtoCrossProject? project, CommandLineOptions options)
        => (project?.Scaffold ?? ScaffoldSettings.Default).OverriddenBy(options.ScaffoldSettings);

    /// <summary>
    /// Warns of a build file an earlier build wrote beside the one just written, under another name: what is
    /// left when the test project is renamed (spec 25.3.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Warned of rather than deleted, because the compiler writes into a directory and never removes from
    /// it. But <c>dotnet test</c> refuses a directory with two project files, so leaving it without a word
    /// leaves a build that worked yesterday failing for a reason the rename does not suggest.
    /// </para>
    /// <para>
    /// A file is the compiler's when it opens as the one just written does, with the comment saying what
    /// generated it. Its extension alone would take in any file of the user's that shares one.
    /// </para>
    /// </remarks>
    private static void WarnOfOtherBuildFiles(string directory, IReadOnlyList<GeneratedFile> buildFiles)
    {
        foreach (var file in buildFiles)
        {
            var header = file.Contents.Split('\n').Take(2).Select(line => line.TrimEnd('\r')).ToList();
            var others = Directory.EnumerateFiles(directory, "*" + Path.GetExtension(file.RelativePath))
                .Where(path => !string.Equals(Path.GetFileName(path), file.RelativePath, StringComparison.OrdinalIgnoreCase))
                .Where(path => OpensWith(path, header));

            foreach (var other in others)
            {
                Console.Error.WriteLine(
                    $"warning: {Path.GetFileName(other)}, which an earlier build wrote, is beside {file.RelativePath}, "
                        + "and a build there needs one; delete it");
            }
        }
    }

    /// <summary>Whether the file at <paramref name="path"/> begins with <paramref name="lines"/>.</summary>
    /// <remarks>
    /// False for a file that cannot be read: it is only being asked about for a warning, and a file the
    /// build cannot open must not end a build that has already written everything it set out to.
    /// </remarks>
    private static bool OpensWith(string path, IReadOnlyList<string> lines)
    {
        try
        {
            return File.ReadLines(path).Take(lines.Count).SequenceEqual(lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void PrintDiagnostics(DiagnosticBag diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            var writer = diagnostic.Severity == DiagnosticSeverity.Error ? Console.Error : Console.Out;
            writer.WriteLine(diagnostic.ToString());
            writer.WriteLine();
        }
    }
}
