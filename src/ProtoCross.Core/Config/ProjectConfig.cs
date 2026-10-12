using System.Xml.Linq;
using ProtoCross.Diagnostics;

namespace ProtoCross.Config;

/// <summary>How an arithmetic operation behaves when its result leaves the range of its type.</summary>
public enum OverflowPolicy
{
    /// <summary>Two's-complement wraparound. The default, and what unmodified C# does.</summary>
    Wrapping,

    /// <summary>Terminal failure, through the same path as <c>on_zero fail</c>.</summary>
    Checked,

    /// <summary>Clamp to the bound the true result exceeded.</summary>
    Saturating,
}

/// <summary>What an explicit numeric conversion does when the value does not fit (spec 10.3).</summary>
public enum ConversionPolicy
{
    /// <summary>
    /// Integer targets take the low bits; a floating-point source truncates toward zero, clamps,
    /// and maps NaN to zero. One member today, matching what the .NET runtime produces.
    /// </summary>
    WrapOrSaturate,
}

/// <summary>What an integer division does about a zero divisor (spec 10.2.1).</summary>
public enum DivideByZeroPolicy
{
    /// <summary>The author must write an <c>on_zero</c> clause. One member today.</summary>
    RequireOnZero,
}

/// <summary>What reading an unset singular message field means (spec 13.1).</summary>
public enum UnsetMessageReadPolicy
{
    /// <summary>
    /// Using the value requires an established presence test, or it is a compile error. One member
    /// today; see <c>docs/reference-semantics.md</c> for why this is not C#'s own answer.
    /// </summary>
    RequireGuard,
}

/// <summary>
/// What a conversion to one enum makes of a number the enum does not name, stated once for every
/// conversion the project writes:
/// <c>&lt;UnknownFallback Type="shop.Flower"&gt;PETUNIA&lt;/UnknownFallback&gt;</c> (spec 10.4, 12.1).
/// </summary>
/// <remarks>
/// Held as written, because what either half means depends on the schemas a compilation loads, and
/// the file is read before any are. The binder resolves both, and reports what does not resolve at
/// these spans.
/// </remarks>
/// <param name="Type">The enum, by full name or by an unambiguous simple name.</param>
/// <param name="Value">One of the enum's value names as the schema spells it, or <c>fail</c>.</param>
/// <param name="TypeSpan">Where the enum is named, in the configuration file.</param>
/// <param name="ValueSpan">Where the value is written, in the configuration file.</param>
public sealed record EnumUnknownFallback(string Type, string Value, SourceSpan TypeSpan, SourceSpan ValueSpan)
{
    /// <summary>The value that ends the program, as <c>on_unknown fail</c> does.</summary>
    public const string Fail = "fail";

    /// <summary>Whether a number the enum does not name ends the program.</summary>
    public bool IsFail => Value == Fail;

    /// <summary>What a generated file's header says about this setting.</summary>
    public string DescribeForHeader()
        => IsFail
            ? $"A number {Type} does not name ends the program (spec 12.1)."
            : $"A number {Type} does not name becomes {Value} (spec 12.1).";
}

/// <summary>
/// The project's language-dependent preferences, as read from <c>protocross.config.xml</c>.
/// </summary>
/// <remarks>
/// <para>
/// Spec 10.4. The point of a file rather than command-line switches is that the semantics of a
/// repository's generated code should travel with the repository, not with whoever remembered which
/// flags to type. A build that produces different arithmetic depending on the operator's shell
/// history is not reproducible in any sense worth having.
/// </para>
/// <para>
/// Several settings have exactly one legal value today. That is deliberate: this file's job is to
/// enumerate every language-dependent preference, including the settled ones, so that a reader can
/// see the whole contract in one place and a future option is an addition rather than a discovery.
/// </para>
/// </remarks>
public sealed record ProjectConfig(
    OverflowPolicy Overflow,
    ConversionPolicy Conversion,
    DivideByZeroPolicy DivideByZero,
    UnsetMessageReadPolicy UnsetMessageRead)
{
    /// <summary>The file name searched for, in the source directory and every directory above it.</summary>
    public const string FileName = "protocross.config.xml";

    /// <summary>The behavior a project gets when it states nothing.</summary>
    public static ProjectConfig Default { get; } = new(
        OverflowPolicy.Wrapping,
        ConversionPolicy.WrapOrSaturate,
        DivideByZeroPolicy.RequireOnZero,
        UnsetMessageReadPolicy.RequireGuard);

    /// <summary>
    /// Which settings the file stated explicitly. A command-line override has to know the
    /// difference between a value the project chose and a default that was merely left in place.
    /// </summary>
    public IReadOnlySet<string> ExplicitKeys { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The file this came from, or null for <see cref="Default"/>.</summary>
    public string? Path { get; init; }

    /// <summary>
    /// What a conversion to each enum the file names makes of a number the enum does not name, in the
    /// order the file states them (spec 12.1).
    /// </summary>
    /// <remarks>
    /// A list of settings rather than one, since each names its enum, and an init-only property
    /// rather than another positional member, since a project states as many as it has enums to
    /// speak for and most state none.
    /// </remarks>
    public IReadOnlyList<EnumUnknownFallback> EnumFallbacks { get; init; } = [];

    /// <summary>Whether two configurations state the same policy, read from the same file.</summary>
    /// <remarks>
    /// Written out rather than left to the record, for one member, exactly as
    /// <c>SchemaFile.Equals</c> is. <see cref="ExplicitKeys"/> is a set, and the generated equality
    /// compares it by reference -- so two reads of one unchanged file were never equal, which makes
    /// a value type's whole promise false for every configuration that came off disk. The one caller
    /// that noticed was a language server asking "would this document compile the same way as it did
    /// a keystroke ago", and being told no every time by a project that had not changed at all.
    /// </remarks>
    public bool Equals(ProjectConfig? other)
        => other is not null
            && Overflow == other.Overflow
            && Conversion == other.Conversion
            && DivideByZero == other.DivideByZero
            && UnsetMessageRead == other.UnsetMessageRead
            && string.Equals(Path, other.Path, StringComparison.Ordinal)
            && ExplicitKeys.SetEquals(other.ExplicitKeys)
            && EnumFallbacks.SequenceEqual(other.EnumFallbacks);

    public override int GetHashCode()
        => HashCode.Combine(
            Overflow, Conversion, DivideByZero, UnsetMessageRead, Path, ExplicitKeys.Count, EnumFallbacks.Count);

    /// <summary>
    /// The policy lines a generated file's header carries, so a reader can tell which policy
    /// produced the code below it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rendered here rather than in each backend, for two reasons. Every target says the same thing
    /// about the same build, which is the point of a header that claims reproducibility. And a
    /// backend receives these as prose it can only print, never as a policy it could branch on --
    /// every emission decision has to come from the annotation the binder stamped onto the IR node,
    /// which is what makes a new mode a compile error at every emission site instead of a silently
    /// wrong default.
    /// </para>
    /// <para>
    /// Only the settings visible in the emitted code are listed. The other two govern what compiles
    /// rather than what is written out, and a header that repeated them in every file forever would
    /// be noise. No path is included: an absolute path would make otherwise identical output differ
    /// between machines.
    /// </para>
    /// <para>
    /// Each enum fallback is a line of its own after those, as the file states it. A project that
    /// states none gets the header it always had, so its generated files do not move.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> DescribeForHeader() =>
    [
        $"Language policy (spec 10.4): integer overflow = {DescribeOverflow(Overflow)},",
        $"numeric conversions = {Conversion}. Both are emitted explicitly, so a",
        "consumer's build settings cannot change what this code does.",
        .. EnumFallbacks.Select(fallback => fallback.DescribeForHeader()),
    ];

    private static string DescribeOverflow(OverflowPolicy overflow) => overflow switch
    {
        OverflowPolicy.Wrapping => "Wrapping (two's complement)",
        OverflowPolicy.Checked => "Checked (terminates, exit code 70)",
        OverflowPolicy.Saturating => "Saturating (clamps to the type's bounds)",
        _ => throw new ArgumentOutOfRangeException(nameof(overflow), overflow, "Unhandled overflow policy."),
    };

    /// <summary>
    /// Applies a command-line override to one setting, or refuses it.
    /// </summary>
    /// <remarks>
    /// The file wins. A flag that contradicts a setting the project states is refused rather than
    /// silently applied, because the point of tracking policy in the repository is that generated
    /// code means the same thing however it was built. <paramref name="allowOverride"/> exists so
    /// that trying another policy stays one flag away, while leaving a trace in the command that
    /// nobody can mistake for the project's own answer.
    /// </remarks>
    /// <param name="conflict">
    /// Null on success. Otherwise a sentence naming both answers, for the driver to print.
    /// </param>
    public bool TryOverrideOverflow(
        OverflowPolicy overflow,
        bool allowOverride,
        out ProjectConfig result,
        out string? conflict)
    {
        if (!allowOverride && ExplicitKeys.Contains("Arithmetic/Overflow") && Overflow != overflow)
        {
            result = this;
            conflict =
                $"--arithmetic-overflow {overflow.ToString().ToLowerInvariant()} contradicts "
                + $"Arithmetic/Overflow = {Overflow} in {Path}";
            return false;
        }

        result = this with { Overflow = overflow };
        conflict = null;
        return true;
    }

    /// <summary>
    /// Searches <paramref name="startDirectory"/> and each directory above it for
    /// <see cref="FileName"/>, the way <c>.editorconfig</c> and <c>Directory.Build.props</c> are
    /// found. Returns the nearest one, or null.
    /// </summary>
    public static string? Discover(string startDirectory)
    {
        var directory = new DirectoryInfo(System.IO.Path.GetFullPath(startDirectory));

        while (directory is not null)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Reads a configuration file. Every problem is reported through <paramref name="diagnostics"/>
    /// with the line and column it occurred at; an unreadable or invalid file yields null rather
    /// than silently falling back to the defaults, because a project that states a policy and is
    /// then ignored is worse off than one that states nothing.
    /// </summary>
    public static ProjectConfig? Load(string path, DiagnosticBag diagnostics)
    {
        var file = XmlInput.Read(path, "ProtoCross", DiagnosticCodes.ConfigurationFileCouldNotBeRead, diagnostics);
        if (file is null)
        {
            return null;
        }

        var config = Default with { Path = path };
        var explicitKeys = new HashSet<string>(StringComparer.Ordinal);
        var enumFallbacks = new List<EnumUnknownFallback>();
        var failed = false;

        foreach (var section in file.Root.Elements())
        {
            var sectionName = section.Name.LocalName;
            if (!KnownSections.Contains(sectionName))
            {
                UnknownElement(diagnostics, file, section, sectionName, "ProtoCross", KnownSections);
                failed = true;
                continue;
            }

            foreach (var setting in section.Elements())
            {
                var key = $"{sectionName}/{setting.Name.LocalName}";
                var text = setting.Value.Trim();

                // One per enum rather than one per file, so it is told apart by the enum it names
                // rather than by its key, and is stated twice only when one enum is named twice.
                if (key == EnumFallbackKey)
                {
                    failed |= !TryReadEnumFallback(diagnostics, file, setting, text, enumFallbacks);
                    continue;
                }

                switch (key)
                {
                    case "Arithmetic/Overflow":
                        if (TryParse<OverflowPolicy>(diagnostics, file, setting, key, text, out var overflow))
                        {
                            config = config with { Overflow = overflow };
                        }
                        else
                        {
                            failed = true;
                        }

                        break;

                    case "Arithmetic/Conversion":
                        if (TryParse<ConversionPolicy>(diagnostics, file, setting, key, text, out var conversion))
                        {
                            config = config with { Conversion = conversion };
                        }
                        else
                        {
                            failed = true;
                        }

                        break;

                    case "Arithmetic/DivideByZero":
                        if (TryParse<DivideByZeroPolicy>(diagnostics, file, setting, key, text, out var divide))
                        {
                            config = config with { DivideByZero = divide };
                        }
                        else
                        {
                            failed = true;
                        }

                        break;

                    case "Presence/UnsetMessageRead":
                        if (TryParse<UnsetMessageReadPolicy>(diagnostics, file, setting, key, text, out var unset))
                        {
                            config = config with { UnsetMessageRead = unset };
                        }
                        else
                        {
                            failed = true;
                        }

                        break;

                    default:
                        UnknownElement(
                            diagnostics,
                            file,
                            setting,
                            setting.Name.LocalName,
                            sectionName,
                            KnownSettings(sectionName));
                        failed = true;
                        continue;
                }

                if (!explicitKeys.Add(key))
                {
                    diagnostics.Report(
                        DiagnosticCodes.DuplicateConfigurationSetting,
                        $"'{key}' is stated more than once.",
                        file.Span(setting),
                        "Two answers to one question is not a configuration, it is a coin toss. Keep one.");
                    failed = true;
                }
            }
        }

        return failed ? null : config with { ExplicitKeys = explicitKeys, EnumFallbacks = enumFallbacks };
    }

    /// <summary>The key of an <see cref="EnumUnknownFallback"/>, of which a file may state several.</summary>
    private const string EnumFallbackKey = "Enums/UnknownFallback";

    /// <summary>
    /// Reads one <c>&lt;UnknownFallback&gt;</c>, as far as the file alone can tell: that it names an
    /// enum and a value, says nothing else, and names no enum another one already named.
    /// </summary>
    /// <remarks>
    /// Whether the enum exists and has the value is the binder's to say, since only it has the schemas
    /// (spec 10.4). A second setting for the same enum spelled another way is caught there too, where
    /// the two spellings resolve to one enum.
    /// </remarks>
    private static bool TryReadEnumFallback(
        DiagnosticBag diagnostics,
        XmlInput file,
        XElement setting,
        string value,
        List<EnumUnknownFallback> read)
    {
        if (setting.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName != "Type") is { } stray)
        {
            diagnostics.Report(
                DiagnosticCodes.InvalidEnumFallback,
                $"<UnknownFallback> has no attribute '{stray.Name.LocalName}'.",
                file.Span(stray),
                "It takes one, Type, naming the enum.");
            return false;
        }

        if (setting.Attribute("Type") is not { } named || named.Value.Trim().Length == 0)
        {
            diagnostics.Report(
                DiagnosticCodes.InvalidEnumFallback,
                "<UnknownFallback> names no enum.",
                file.Span(setting),
                "Name it with Type, as in <UnknownFallback Type=\"shop.Flower\">PETUNIA</UnknownFallback>.");
            return false;
        }

        var type = named.Value.Trim();

        if (!IsProtobufName(type, qualified: true))
        {
            diagnostics.Report(
                DiagnosticCodes.InvalidEnumFallback,
                $"'{type}' is not the name of an enum.",
                file.Span(named),
                "Write its full name, such as shop.Flower, or its simple name, such as Flower.");
            return false;
        }

        if (value.Length == 0)
        {
            diagnostics.Report(
                DiagnosticCodes.InvalidEnumFallback,
                $"<UnknownFallback Type=\"{type}\"> does not say what a number the enum does not name becomes.",
                file.Span(setting),
                $"Write one of the enum's value names, or {EnumUnknownFallback.Fail}.");
            return false;
        }

        if (value != EnumUnknownFallback.Fail && !IsProtobufName(value, qualified: false))
        {
            diagnostics.Report(
                DiagnosticCodes.InvalidEnumFallback,
                $"'{value}' is not the name of a value.",
                file.Span(setting),
                $"Write one of the enum's value names as the schema spells it, or {EnumUnknownFallback.Fail}.");
            return false;
        }

        if (read.Any(earlier => earlier.Type == type))
        {
            diagnostics.Report(
                DiagnosticCodes.DuplicateConfigurationSetting,
                $"'{EnumFallbackKey}' is stated more than once for '{type}'.",
                file.Span(setting),
                "Two answers to one question is not a configuration, it is a coin toss. Keep one.");
            return false;
        }

        var written = setting.Nodes().OfType<XText>().FirstOrDefault(node => node.Value.Trim().Length > 0);
        read.Add(new EnumUnknownFallback(
            type,
            value,
            file.Span(named),
            written is null ? file.Span(setting) : file.SpanOfText(written)));
        return true;
    }

    /// <summary>Whether <paramref name="text"/> is a protobuf name, or, if qualified, several joined by dots.</summary>
    /// <remarks>
    /// Asked of the file because only the binder can say whether a name names anything, and a setting
    /// about an enum a compilation never loads is never asked there. Its text still goes into every
    /// generated file's header, where a line break, which an XML character reference can write,
    /// would end the comment and leave the rest of the setting as code.
    /// </remarks>
    private static bool IsProtobufName(string text, bool qualified)
    {
        var parts = text.Split('.');

        return (qualified || parts.Length == 1)
            && parts.All(part => part.Length > 0
                && (char.IsAsciiLetter(part[0]) || part[0] == '_')
                && part.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'));
    }

    private static readonly string[] KnownSections = ["Arithmetic", "Presence", "Enums"];

    /// <summary>Every setting this file may state, as <c>Section/Setting</c>.</summary>
    /// <remarks>
    /// Published so that a host which accepts configuration of its own can tell a policy setting from
    /// a setting it does not recognize, and refuse the first by name -- "that one is stated in
    /// <c>protocross.config.xml</c>" is a useful thing to be told, and "unknown setting" is not.
    /// Derived from the same two lists the loader validates against rather than written out again,
    /// because a second list is one that eventually accepts a setting the file rejects.
    /// </remarks>
    public static IReadOnlyList<string> Keys { get; } =
    [
        .. KnownSections.SelectMany(section => KnownSettings(section).Select(setting => $"{section}/{setting}")),
    ];

    private static string[] KnownSettings(string section) => section switch
    {
        "Arithmetic" => ["Overflow", "Conversion", "DivideByZero"],
        "Presence" => ["UnsetMessageRead"],
        "Enums" => ["UnknownFallback"],
        _ => [],
    };

    private static bool TryParse<T>(
        DiagnosticBag diagnostics,
        XmlInput file,
        XElement element,
        string key,
        string text,
        out T value)
        where T : struct, Enum
    {
        // Deliberately case-sensitive and exact. A configuration file that quietly accepts
        // "wrapping", "WRAPPING", and "Wrap" for the same setting invites a project to be written
        // one way and read another, which is the failure this whole file exists to prevent.
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(candidate.ToString(), text, StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }

        diagnostics.Report(
            DiagnosticCodes.UnknownConfigurationValue,
            $"'{text}' is not a legal value for '{key}'.",
            file.Span(element),
            $"Legal values: {string.Join(", ", Enum.GetValues<T>().Select(v => v.ToString()))}.");

        value = default;
        return false;
    }

    private static void UnknownElement(
        DiagnosticBag diagnostics,
        XmlInput file,
        XElement element,
        string name,
        string parent,
        IReadOnlyList<string> known)
    {
        diagnostics.Report(
            DiagnosticCodes.UnknownConfigurationElement,
            $"<{name}> is not a setting ProtoCross knows about inside <{parent}>.",
            file.Span(element),
            known.Count == 0
                ? null
                : $"Known elements inside <{parent}>: {string.Join(", ", known)}.");
    }
}
