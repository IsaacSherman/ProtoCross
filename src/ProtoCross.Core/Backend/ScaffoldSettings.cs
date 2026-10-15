using System.Text;

namespace ProtoCross.Backend;

/// <summary>
/// What a project or a command line chose about the test projects <c>--scaffold</c> writes: what each
/// is named and, for C#, the framework it targets (spec 25.3.2). A setting left null is the backend's
/// default.
/// </summary>
/// <remarks>
/// <para>
/// Names and a framework, and nothing that changes what a test does. The package versions stay pinned,
/// because Google.Protobuf has to match the protoc that Grpc.Tools runs, and the test framework is the
/// one each test backend writes source for: a C# test is an xUnit test, and choosing another would take
/// a second emitter rather than a setting.
/// </para>
/// <para>
/// One record for both targets rather than one per backend, because a project states both whatever a
/// build targets. A build of one target leaves the other's settings unread.
/// </para>
/// </remarks>
public sealed record ScaffoldSettings
{
    /// <summary>No setting stated, so every test project is written as it always was.</summary>
    public static ScaffoldSettings Default { get; } = new();

    /// <summary>The C# test project's target framework, such as <c>net8.0</c>.</summary>
    public string? CSharpTargetFramework { get; init; }

    /// <summary>The C# test project's name, which names its <c>.csproj</c> and so the assembly it builds.</summary>
    public string? CSharpProjectName { get; init; }

    /// <summary>The name of the CMake project the C++ tests build in.</summary>
    public string? CppProjectName { get; init; }

    /// <summary>What begins each target the C++ test project declares, followed by an underscore.</summary>
    public string? CppTargetPrefix { get; init; }

    /// <summary>These settings, with each one <paramref name="overrides"/> states in place of this one's.</summary>
    /// <remarks>
    /// How a flag stands over a project: the project is what every build of it gets, and a flag is one
    /// build's answer to one question, so it replaces that answer and leaves the rest standing.
    /// </remarks>
    public ScaffoldSettings OverriddenBy(ScaffoldSettings overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        return ScaffoldSetting.All.Aggregate(
            this,
            (settings, setting) => setting.ValueIn(overrides) is { } value ? setting.With(settings, value) : settings);
    }
}

/// <summary>
/// One of the choices <see cref="ScaffoldSettings"/> holds: the attribute a project states it with, the
/// flag that states it for one build, and what it accepts (spec 25.3.2).
/// </summary>
/// <remarks>
/// Each setting is described once, here, and the project reader and the command line both ask it. A
/// value one of them accepted and the other refused would make two ways of saying one thing disagree,
/// and a project could then state what no command line can try.
/// </remarks>
public sealed class ScaffoldSetting
{
    /// <summary>The oldest .NET a C# test project may target.</summary>
    /// <remarks>
    /// The pinned <c>Microsoft.NET.Test.Sdk</c> refuses to build for anything older, and xunit.v3 does
    /// not support it either. The generated behavior and tests need no newer language than .NET 8's.
    /// Raising a pinned version can raise this, and nothing else should.
    /// </remarks>
    public const int OldestDotNet = 8;

    public static ScaffoldSetting CSharpTargetFramework { get; } = new(
        "csharp",
        "CSharp",
        "TargetFramework",
        settings => settings.CSharpTargetFramework,
        (settings, value) => settings with { CSharpTargetFramework = value },
        IsSupportedTargetFramework,
        "a target framework the generated tests can build for",
        $"Write net{OldestDotNet}.0 or a later one, as 'net{OldestDotNet}.0' is written. The test SDK the project "
            + "pins supports nothing older, and the tests need no platform.");

    public static ScaffoldSetting CSharpProjectName { get; } = new(
        "csharp",
        "CSharp",
        "ProjectName",
        settings => settings.CSharpProjectName,
        (settings, value) => settings with { CSharpProjectName = value },
        value => ProjectNamespace.TryParse(value, out _),
        "a name for a C# project",
        "Write identifiers separated by periods, each a letter followed by letters, digits and underscores, "
            + "as 'Acme.Billing.Tests' is. It names the .csproj, and so the assembly the tests build into.");

    public static ScaffoldSetting CppProjectName { get; } = new(
        "cpp",
        "Cpp",
        "ProjectName",
        settings => settings.CppProjectName,
        (settings, value) => settings with { CppProjectName = value },
        ProjectNamespace.IsIdentifier,
        "a name for a CMake project",
        "Write a letter followed by letters, digits and underscores, as 'billing_tests' is.");

    public static ScaffoldSetting CppTargetPrefix { get; } = new(
        "cpp",
        "Cpp",
        "TargetPrefix",
        settings => settings.CppTargetPrefix,
        (settings, value) => settings with { CppTargetPrefix = value },
        ProjectNamespace.IsIdentifier,
        "a prefix for CMake targets",
        "Write a letter followed by letters, digits and underscores, as 'billing' is. Each target the "
            + "CMakeLists.txt declares begins with it and an underscore, so that test projects built together "
            + "declare no target twice.");

    /// <summary>Every setting, in the order a project's elements and the usage text list them.</summary>
    public static IReadOnlyList<ScaffoldSetting> All { get; } =
        [CSharpTargetFramework, CSharpProjectName, CppProjectName, CppTargetPrefix];

    private readonly Func<ScaffoldSettings, string?> _read;
    private readonly Func<ScaffoldSettings, string, ScaffoldSettings> _write;
    private readonly Func<string, bool> _accepts;
    private readonly string _meaning;

    private ScaffoldSetting(
        string target,
        string element,
        string attribute,
        Func<ScaffoldSettings, string?> read,
        Func<ScaffoldSettings, string, ScaffoldSettings> write,
        Func<string, bool> accepts,
        string meaning,
        string help)
    {
        Target = target;
        Element = element;
        Attribute = attribute;
        _read = read;
        _write = write;
        _accepts = accepts;
        _meaning = meaning;
        Help = help;
    }

    /// <summary>The target whose test project this setting shapes, as <c>-t</c> names it.</summary>
    public string Target { get; }

    /// <summary>The element inside a project's <c>&lt;Scaffold&gt;</c> that states it: <c>CSharp</c> or <c>Cpp</c>.</summary>
    public string Element { get; }

    /// <summary>The attribute on <see cref="Element"/> that states it.</summary>
    public string Attribute { get; }

    /// <summary>The flag that states it for one build: the target, then the attribute in lowercase words.</summary>
    public string Flag => $"--{Target}-{InLowercaseWords(Attribute)}";

    /// <summary>What to write instead of a value this setting refuses.</summary>
    public string Help { get; }

    /// <summary>The value <paramref name="settings"/> states for this setting, or null when it states none.</summary>
    public string? ValueIn(ScaffoldSettings settings) => _read(settings);

    /// <summary><paramref name="settings"/>, stating <paramref name="value"/> for this setting.</summary>
    public ScaffoldSettings With(ScaffoldSettings settings, string value) => _write(settings, value);

    /// <summary>Why <paramref name="value"/> cannot be this setting, as a sentence; null when it can.</summary>
    /// <remarks>
    /// A value is refused rather than rewritten into one that can be: a name with its spaces turned to
    /// underscores is a name nobody wrote, and the test project would answer to it and not to the
    /// one in the project.
    /// </remarks>
    public string? ProblemWith(string value)
        => _accepts(value) ? null : $"'{value}' is not {_meaning}.";

    public override string ToString() => $"{Element}/{Attribute}";

    /// <summary>
    /// Whether <paramref name="value"/> names a .NET a test project can target: <c>net</c>, then a
    /// major and a minor version no older than <see cref="OldestDotNet"/>, with no platform after them.
    /// </summary>
    private static bool IsSupportedTargetFramework(string value)
        => value.StartsWith("net", StringComparison.Ordinal)
            && value[3..].Split('.') is [var major, var minor]
            && IsNumber(major)
            && IsNumber(minor)
            && int.TryParse(major, out var number)
            && number >= OldestDotNet;

    /// <summary>Digits, with no zero in front of a number that has more than one.</summary>
    private static bool IsNumber(string text)
        => text.Length > 0 && text.All(char.IsAsciiDigit) && (text.Length == 1 || text[0] != '0');

    /// <summary><c>TargetFramework</c> as a flag spells it: <c>target-framework</c>.</summary>
    private static string InLowercaseWords(string pascalCase)
    {
        var words = new StringBuilder();
        foreach (var c in pascalCase)
        {
            if (char.IsUpper(c) && words.Length > 0)
            {
                words.Append('-');
            }

            words.Append(char.ToLowerInvariant(c));
        }

        return words.ToString();
    }
}
