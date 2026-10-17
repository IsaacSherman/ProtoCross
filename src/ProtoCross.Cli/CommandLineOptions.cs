using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Config;
using ProtoCross.Projects;

namespace ProtoCross.Cli;

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

    /// <summary>The scaffold settings this run's flags state, each over the project's (spec 25.3.2).</summary>
    public ScaffoldSettings ScaffoldSettings { get; init; } = ScaffoldSettings.Default;

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
        var scaffoldSettings = ScaffoldSettings.Default;

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

                case var flag when ScaffoldSetting.All.FirstOrDefault(setting => setting.Flag == flag) is { } setting:
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine($"error: {arg} requires a value");
                        return null;
                    }

                    if (setting.ProblemWith(args[i]) is { } problem)
                    {
                        Console.Error.WriteLine($"error: {arg}: {problem}");
                        Console.Error.WriteLine($"       {setting.Help}");
                        return null;
                    }

                    if (setting.ValueIn(scaffoldSettings) is not null)
                    {
                        // A project refuses a setting stated twice, and a flag must not accept what a project refuses.
                        Console.Error.WriteLine($"error: {arg} is given more than once; pass one");
                        return null;
                    }

                    scaffoldSettings = setting.With(scaffoldSettings, args[i]);
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

        if (!AreScaffoldFlagsUsed(scaffoldSettings, scaffold, targets))
        {
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
            overrideConfig)
        {
            ScaffoldSettings = scaffoldSettings,
        };
    }

    /// <summary>Whether every scaffold setting a flag states shapes a test project this run writes.</summary>
    /// <remarks>
    /// A flag that shapes nothing is refused rather than ignored: a build that took it and wrote no test
    /// project, or none for its target, would look as if it had used it. A project's settings for a target
    /// the build leaves out are another matter, because a project states every target's whatever one
    /// build asks for.
    /// </remarks>
    private static bool AreScaffoldFlagsUsed(ScaffoldSettings settings, bool scaffold, IReadOnlySet<string> targets)
    {
        foreach (var setting in ScaffoldSetting.All.Where(setting => setting.ValueIn(settings) is not null))
        {
            if (!scaffold)
            {
                Console.Error.WriteLine($"error: {setting.Flag} needs --scaffold, because it shapes the test project --scaffold writes");
                return false;
            }

            if (!targets.Contains(setting.Target))
            {
                Console.Error.WriteLine(
                    $"error: {setting.Flag} shapes the {setting.Target} test project, and this build writes none; "
                        + $"add {setting.Target} to -t");
                return false;
            }
        }

        return true;
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
        Console.Error.WriteLine("usage: protocross [build] <source.pcross>... [options]");
        Console.Error.WriteLine("       protocross [build] <project.pcproj> [options]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("commands:");
        foreach (var verb in CommandLine.All)
        {
            Console.Error.WriteLine($"  {verb.Name,-25}{verb.Summary}");
        }

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
        Console.Error.WriteLine($"  {ScaffoldSetting.CSharpTargetFramework.Flag} <tfm>");
        Console.Error.WriteLine($"                           The C# test project's framework, net{ScaffoldSetting.OldestDotNet}.0 or later");
        Console.Error.WriteLine($"                           (default: {CSharpTestProject.DefaultTargetFramework}).");
        Console.Error.WriteLine($"  {ScaffoldSetting.CSharpProjectName.Flag} <name>");
        Console.Error.WriteLine($"                           The C# test project's name (default: {CSharpTestProject.DefaultProjectName}).");
        Console.Error.WriteLine($"  {ScaffoldSetting.CppProjectName.Flag} <name>");
        Console.Error.WriteLine($"                           The CMake project's name (default: {CppTestProject.DefaultProjectName}).");
        Console.Error.WriteLine($"  {ScaffoldSetting.CppTargetPrefix.Flag} <prefix>");
        Console.Error.WriteLine("                           What begins each CMake target, followed by _ (default: none).");
        Console.Error.WriteLine("                           Each needs --scaffold, and stands over the project's <Scaffold>.");
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
