using ProtoCross;
using ProtoCross.Backend;
using ProtoCross.Config;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Diagnostics;
using ProtoCross.Projects;

var options = CommandLineOptions.Parse(args);

if (options is null)
{
    CommandLineOptions.PrintUsage();
    return 2;
}

var missing = options.SourcePaths.Where(path => !File.Exists(path)).ToList();
foreach (var path in missing)
{
    Console.Error.WriteLine($"error: source file not found: {path}");
}

if (missing.Count > 0)
{
    return 2;
}

if (options.Scaffold && options.TestOutputDirectory is null)
{
    Console.Error.WriteLine("error: --scaffold needs --test-out, because it writes the build file beside the generated tests");
    return 2;
}

var setupDiagnostics = new DiagnosticBag();
var compilation = options.ProjectPath is { } projectPath
    ? CompilationOfProject(projectPath, options, setupDiagnostics)
    : CompilationOfSources(options, setupDiagnostics);
PrintDiagnostics(setupDiagnostics);

if (compilation is null)
{
    Console.Error.WriteLine(options.ProjectPath is null ? "configuration failed" : "project failed");
    return 2;
}

var result = compilation.Compile();
PrintDiagnostics(result.Diagnostics);

// The one module a compiler may write from. A partial one comes out of a buffer that did not
// parse, which is what an editor asks for and what nothing here may emit.
if (result.EmittableModule is null)
{
    Console.Error.WriteLine($"compilation failed: {result.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)} error(s)");
    return 1;
}

var backends = new List<IBackend>();
if (options.Targets.Contains("csharp"))
{
    backends.Add(new CSharpBackend());
}

if (options.Targets.Contains("cpp"))
{
    backends.Add(new CppBackend());
}

var backendDiagnostics = new DiagnosticBag();
var written = new List<string>();

foreach (var backend in backends)
{
    // Each source into files named after it, with the policy it ran under in every header, so a
    // reader can tell what produced the code in front of them without re-running the compiler.
    var files = SourceEmission.Emit(result, backend, backendDiagnostics);

    var outputDirectory = Path.Combine(options.OutputDirectory, backend.Name);
    Directory.CreateDirectory(outputDirectory);

    foreach (var file in files)
    {
        var path = Path.Combine(outputDirectory, file.RelativePath);
        File.WriteAllText(path, file.Contents);
        written.Add(path);
    }

    if (options.TestOutputDirectory is not null && backend is ITestBackend testBackend)
    {
        var testFiles = SourceEmission.EmitTests(result, testBackend, backendDiagnostics);

        var testOutputDirectory = Path.Combine(options.TestOutputDirectory, backend.Name);
        Directory.CreateDirectory(testOutputDirectory);

        foreach (var file in testFiles)
        {
            var path = Path.Combine(testOutputDirectory, file.RelativePath);
            File.WriteAllText(path, file.Contents);
            written.Add(path);
        }

        if (options.Scaffold && backend is ITestProjectScaffold scaffold)
        {
            var scaffoldOptions = ScaffoldOptions.Create(
                result.SearchPaths,
                result.Descriptors,
                outputDirectory,
                testOutputDirectory,
                testFiles.Select(file => file.RelativePath).ToList());

            foreach (var file in scaffold.EmitTestProject(scaffoldOptions, backendDiagnostics))
            {
                var path = Path.Combine(testOutputDirectory, file.RelativePath);
                File.WriteAllText(path, file.Contents);
                written.Add(path);
            }
        }
    }
}

PrintDiagnostics(backendDiagnostics);

if (backendDiagnostics.HasErrors)
{
    Console.Error.WriteLine("code generation failed");
    return 1;
}

foreach (var path in written)
{
    Console.WriteLine(Path.GetRelativePath(Directory.GetCurrentDirectory(), path));
}

return 0;

/// <summary>The sources named on the command line, compiled as one program.</summary>
/// <remarks>
/// Every one of them is a production source: only a project can say that a source is there only to
/// test with (spec 25.3.1).
/// </remarks>
static Compilation? CompilationOfSources(CommandLineOptions options, DiagnosticBag diagnostics)
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
/// A project that cannot be read, or whose files could not all be listed, compiles nothing, rather than
/// the part of it that could be: a project missing one of its lines would build a program nobody
/// wrote. A project whose <c>&lt;Sources&gt;</c> find nothing is refused in either build, because a test
/// build writes what the production build would, and the production build has nothing to write.
/// </para>
/// <para>
/// The project's schema directories are searched before <c>-I</c>'s. They are the project's answer,
/// the same on every machine that builds it; a directory named for one run adds to them rather than
/// changing what the project's imports mean.
/// </para>
/// </remarks>
static Compilation? CompilationOfProject(string projectPath, CommandLineOptions options, DiagnosticBag diagnostics)
{
    var project = ProtoCrossProject.Load(projectPath, diagnostics);
    if (project is null)
    {
        return null;
    }

    var files = ProjectSources.Expand(project, diagnostics);
    if (diagnostics.HasErrors)
    {
        return null;
    }

    if (files.Sources.Count == 0)
    {
        Console.Error.WriteLine(
            $"error: {Path.GetFileName(project.Path)} compiles nothing: no <Sources> element matches a "
                + $"{ProjectSources.SourceExtension} file");
        return null;
    }

    var members = options.BuildsTests ? files.TestBuild : files.ProductionBuild;
    var config = ProjectPolicy.Resolve(project, members, diagnostics) is { } found ? ApplyPolicyFlags(found, options) : null;

    return config is null
        ? null
        : new Compilation(
            [.. members.Select(member => SourceDocument.ReadFrom(member.Path) with { Role = member.Role })],
            OptionsFor(options, [.. project.ProtoPaths.Select(protoPath => protoPath.Path), .. options.IncludePaths], config));
}

/// <summary>What a compilation needs besides its sources, for the build this run asks for.</summary>
/// <remarks>
/// Without <c>--test-out</c> nothing is generated from a test, so nothing about one is checked either
/// (spec 25.3.1): a test that no longer binds must not stop the program shipping. With it, the tests
/// are part of what has to compile, and one that does not leaves nothing written, the program's
/// output included.
/// </remarks>
static CompilationOptions OptionsFor(CommandLineOptions options, IReadOnlyList<string> includePaths, ProjectConfig config)
    => new()
    {
        IncludePaths = includePaths,
        Config = config,
        SkipTests = !options.BuildsTests,
    };

/// <summary>The configuration file that governs sources named on the command line (spec 10.4).</summary>
static ProjectConfig? ConfigOfSources(CommandLineOptions options, DiagnosticBag diagnostics)
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
static ProjectConfig? ApplyPolicyFlags(ProjectConfig config, CommandLineOptions options)
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
        Console.Error.WriteLine("       Pass --override-config to use the flag anyway.");
        return null;
    }

    return overridden;
}

static void PrintDiagnostics(DiagnosticBag diagnostics)
{
    foreach (var diagnostic in diagnostics)
    {
        var writer = diagnostic.Severity == DiagnosticSeverity.Error ? Console.Error : Console.Out;
        writer.WriteLine(diagnostic.ToString());
        writer.WriteLine();
    }
}

/// <param name="SourcePaths">The sources named on the command line; empty when a project is named.</param>
/// <param name="ProjectPath">The project named on the command line, or null when sources are named instead.</param>
internal sealed record CommandLineOptions(
    IReadOnlyList<string> SourcePaths,
    string? ProjectPath,
    IReadOnlyList<string> IncludePaths,
    string OutputDirectory,
    string? TestOutputDirectory,
    bool Scaffold,
    IReadOnlySet<string> Targets,
    string? ConfigPath,
    bool NoConfig,
    OverflowPolicy? Overflow,
    bool OverrideConfig)
{
    private static readonly string[] KnownTargets = ["csharp", "cpp"];

    /// <summary>Whether this run builds the tests as well as the program (spec 25.3.1).</summary>
    public bool BuildsTests => TestOutputDirectory is not null;

    public static CommandLineOptions? Parse(string[] args)
    {
        var sourcePaths = new List<string>();
        var includePaths = new List<string>();
        var outputDirectory = "generated";
        string? testOutputDirectory = null;
        var scaffold = false;
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? configPath = null;
        var noConfig = false;
        OverflowPolicy? overflow = null;
        var overrideConfig = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            switch (arg)
            {
                case "-I" or "--proto_path":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine($"error: {arg} requires a directory");
                        return null;
                    }

                    includePaths.Add(args[i]);
                    break;

                case "-o" or "--out":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine($"error: {arg} requires a directory");
                        return null;
                    }

                    outputDirectory = args[i];
                    break;

                case "--test-out":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine($"error: {arg} requires a directory");
                        return null;
                    }

                    testOutputDirectory = args[i];
                    break;

                case "--scaffold":
                    scaffold = true;
                    break;

                case "--config":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine($"error: {arg} requires a file path");
                        return null;
                    }

                    configPath = args[i];
                    break;

                case "--no-config":
                    noConfig = true;
                    break;

                case "--override-config":
                    overrideConfig = true;
                    break;

                case "--arithmetic-overflow":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine($"error: {arg} requires a mode");
                        return null;
                    }

                    if (!TryParseOverflow(args[i], out var parsed))
                    {
                        Console.Error.WriteLine(
                            $"error: unknown overflow mode '{args[i]}' (expected one of: "
                            + "wrapping, checked, saturating)");
                        return null;
                    }

                    overflow = parsed;
                    break;

                case "-t" or "--target":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine($"error: {arg} requires a target name");
                        return null;
                    }

                    foreach (var target in args[i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = target.Trim();
                        if (!KnownTargets.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                        {
                            Console.Error.WriteLine(
                                $"error: unknown target '{trimmed}' (expected one of: {string.Join(", ", KnownTargets)})");
                            return null;
                        }

                        targets.Add(trimmed.ToLowerInvariant());
                    }

                    break;

                case "-h" or "--help":
                    return null;

                default:
                    if (arg.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"error: unknown option '{arg}'");
                        return null;
                    }

                    sourcePaths.Add(arg);
                    break;
            }
        }

        if (sourcePaths.Count == 0)
        {
            return null;
        }

        if (targets.Count == 0)
        {
            foreach (var target in KnownTargets)
            {
                targets.Add(target);
            }
        }

        if (noConfig && configPath is not null)
        {
            Console.Error.WriteLine("error: --no-config and --config say opposite things; pass one");
            return null;
        }

        string? projectPath = null;
        if (sourcePaths.Any(ProtoCrossProject.IsProjectFile))
        {
            if (!TryTakeProject(sourcePaths, configPath is not null ? "--config" : noConfig ? "--no-config" : null))
            {
                return null;
            }

            projectPath = sourcePaths[0];
            sourcePaths.Clear();
        }

        return new CommandLineOptions(
            sourcePaths,
            projectPath,
            includePaths,
            outputDirectory,
            testOutputDirectory,
            scaffold,
            targets,
            configPath,
            noConfig,
            overflow,
            overrideConfig);
    }

    /// <summary>
    /// Whether <paramref name="paths"/>, which name a project, name it alone, with no flag naming a
    /// configuration beside it.
    /// </summary>
    /// <remarks>
    /// A project is one compilation, and it says which sources that is made of and which configuration
    /// file governs it (spec 5.4). A source named beside it, a second project, or <c>--config</c>
    /// would each be a second answer to one of those questions, and quietly preferring either answer
    /// builds something other than what one of them says.
    /// </remarks>
    private static bool TryTakeProject(List<string> paths, string? configFlag)
    {
        if (paths.Count(ProtoCrossProject.IsProjectFile) > 1)
        {
            Console.Error.WriteLine("error: a project is one compilation, so name one project at a time");
            return false;
        }

        if (paths.FirstOrDefault(path => !ProtoCrossProject.IsProjectFile(path)) is { } source)
        {
            Console.Error.WriteLine(
                $"error: '{source}' is named beside a project, and a project names its own sources; "
                    + "add it to the project's <Sources> or <Tests>");
            return false;
        }

        if (configFlag is not null)
        {
            Console.Error.WriteLine(
                $"error: {configFlag} cannot be used with a project, which settles its own configuration: "
                    + "the file its <Config> names, or else the nearest protocross.config.xml at or above it");
            return false;
        }

        return true;
    }

    private static bool TryParseOverflow(string text, out OverflowPolicy policy)
    {
        // Lowercase on the command line, PascalCase in the file. A flag is typed by hand and a
        // config value is read by a parser, so they answer to different conventions.
        switch (text.Trim().ToLowerInvariant())
        {
            case "wrapping":
                policy = OverflowPolicy.Wrapping;
                return true;
            case "checked":
                policy = OverflowPolicy.Checked;
                return true;
            case "saturating":
                policy = OverflowPolicy.Saturating;
                return true;
            default:
                policy = default;
                return false;
        }
    }

    public static void PrintUsage()
    {
        Console.Error.WriteLine("protocross - ProtoCross compiler");
        Console.Error.WriteLine();
        Console.Error.WriteLine("usage: protocross <source.pcross>... [options]");
        Console.Error.WriteLine("       protocross <project.pcproj> [options]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Several sources compile together as one program, and each is generated into");
        Console.Error.WriteLine("files named after it. A project names its sources, the sources it only tests");
        Console.Error.WriteLine("with, and its schema directories itself (spec 5.4).");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Without --test-out, the program is built, and its tests are neither checked nor");
        Console.Error.WriteLine("generated. With --test-out, the tests are built too, and nothing is written");
        Console.Error.WriteLine("unless all of it compiles.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("options:");
        Console.Error.WriteLine("  -I, --proto_path <dir>   Directory searched for imported .proto files.");
        Console.Error.WriteLine("                           May be repeated. Each source's directory is always searched.");
        Console.Error.WriteLine("                           A project's <ProtoPath> directories are searched first.");
        Console.Error.WriteLine("  -o, --out <dir>          Output directory (default: generated).");
        Console.Error.WriteLine("                           Each backend writes to <dir>/<target>/.");
        Console.Error.WriteLine("  --test-out <dir>         Build the tests too, into this directory.");
        Console.Error.WriteLine("                           Each test backend writes to <dir>/<target>/.");
        Console.Error.WriteLine("  --scaffold               Also write the build file that builds and runs the");
        Console.Error.WriteLine("                           generated tests: a .csproj for csharp, a CMakeLists.txt");
        Console.Error.WriteLine("                           for cpp. Requires --test-out.");
        Console.Error.WriteLine("  -t, --target <list>      Comma-separated targets: csharp, cpp (default: all).");
        Console.Error.WriteLine("  -h, --help               Show this help.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("policy (spec 10.4):");
        Console.Error.WriteLine("  --config <file>          Use this protocross.config.xml instead of searching.");
        Console.Error.WriteLine("                           Not with a project, which names its own.");
        Console.Error.WriteLine("  --no-config              Ignore any config file and use the built-in defaults.");
        Console.Error.WriteLine("                           Not with a project.");
        Console.Error.WriteLine("  --arithmetic-overflow <mode>");
        Console.Error.WriteLine("                           wrapping (default), checked, or saturating.");
        Console.Error.WriteLine("  --override-config        Let a policy flag win over a setting the config file");
        Console.Error.WriteLine("                           states. Without it, the conflict is an error: the file");
        Console.Error.WriteLine("                           is the project's answer and a flag is not.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  With no --config or --no-config, protocross.config.xml is searched for in each");
        Console.Error.WriteLine("  source file's directory and every directory above it, nearest first. Sources");
        Console.Error.WriteLine("  compiled together must all find the same one. A project compiles under the file");
        Console.Error.WriteLine("  its <Config> names, or else the nearest one at or above its own directory.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("environment:");
        Console.Error.WriteLine("  PROTOCROSS_PROTOC         Path to protoc. Otherwise PATH and the NuGet");
        Console.Error.WriteLine("                           Grpc.Tools package are searched.");
    }
}
