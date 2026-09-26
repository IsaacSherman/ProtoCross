using System.Xml.Linq;
using ProtoCross.Config;
using ProtoCross.Diagnostics;

namespace ProtoCross.Projects;

/// <summary>
/// A <c>.pcproj</c>: which sources one compilation is made of, which of them hold its tests, where
/// the schemas they import are found, and which configuration file settles its policy (spec 5.4).
/// </summary>
/// <remarks>
/// <para>
/// What is compiled and how it behaves are two questions, answered by two files. A project says what
/// is compiled; <c>protocross.config.xml</c> says what the arithmetic means. The project may name the
/// configuration file but never restates it: a project that could carry <c>&lt;Arithmetic&gt;</c>
/// itself would give policy two homes, and the two would come to disagree.
/// </para>
/// <para>
/// Reading a project reads the project file and nothing else. Finding the sources its patterns match
/// is <see cref="ProjectSources.Expand"/>, a separate step, because it walks directories -- and a
/// caller that only needs to know what a project says, which configuration file it names or where
/// its schemas are, should not pay for walking a tree to find out.
/// </para>
/// </remarks>
public sealed record ProtoCrossProject
{
    /// <summary>The extension a project file carries.</summary>
    public const string Extension = ".pcproj";

    /// <summary>The name a project file's root element must have.</summary>
    public const string RootElement = "ProtoCrossProject";

    /// <summary>The project file's full path.</summary>
    public required string Path { get; init; }

    /// <summary>The directory the project's relative paths and patterns are resolved against: its own.</summary>
    public string Directory => System.IO.Path.GetDirectoryName(Path)!;

    /// <summary>The configuration file the project names, or null when it names none.</summary>
    public NamedPath? Config { get; init; }

    /// <summary>The <c>&lt;Sources&gt;</c> elements, in the order the file gives them.</summary>
    public IReadOnlyList<ProjectItem> Sources { get; init; } = [];

    /// <summary>The <c>&lt;Tests&gt;</c> elements, in the order the file gives them.</summary>
    public IReadOnlyList<ProjectItem> Tests { get; init; } = [];

    /// <summary>The directories schemas are searched for in, in the order the file gives them.</summary>
    public IReadOnlyList<NamedPath> ProtoPaths { get; init; } = [];

    /// <summary>Whether two projects say the same things, read from the same file.</summary>
    /// <remarks>Written out for the reason <see cref="ProjectItem.Equals(ProjectItem?)"/> is.</remarks>
    public bool Equals(ProtoCrossProject? other)
        => other is not null
            && string.Equals(Path, other.Path, StringComparison.Ordinal)
            && Config == other.Config
            && Sources.SequenceEqual(other.Sources)
            && Tests.SequenceEqual(other.Tests)
            && ProtoPaths.SequenceEqual(other.ProtoPaths);

    public override int GetHashCode()
        => HashCode.Combine(Path, Config, Sources.Count, Tests.Count, ProtoPaths.Count);

    /// <summary>
    /// Reads a project file. Every problem is reported through <paramref name="diagnostics"/> at the
    /// line and column it occurred at.
    /// </summary>
    /// <returns>
    /// Null when the file could not be read or states anything it cannot mean, rather than a project
    /// with the offending element left out: a project missing one of its <c>&lt;Sources&gt;</c> lines
    /// would compile a program nobody wrote.
    /// </returns>
    public static ProtoCrossProject? Load(string path, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(diagnostics);

        string fullPath;
        try
        {
            fullPath = System.IO.Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Reported rather than thrown: the path comes from a command line or an editor, and
            // neither may take the process down by naming a file that cannot exist.
            diagnostics.Report(
                DiagnosticCodes.ProjectCouldNotBeRead,
                $"'{path}' is not a path: {ex.Message}",
                new SourceSpan(path, SourcePosition.None, SourcePosition.None));
            return null;
        }

        var file = XmlInput.Read(fullPath, RootElement, DiagnosticCodes.ProjectCouldNotBeRead, diagnostics);

        return file is null ? null : new ProjectReader(file, fullPath, diagnostics).Read();
    }

    /// <summary>One read of one project file, gathering what it states and whether any of it failed.</summary>
    private sealed class ProjectReader(XmlInput file, string path, DiagnosticBag diagnostics)
    {
        private static readonly string[] KnownElements = ["Sources", "Tests", "ProtoPath", "Config"];

        private static readonly string[] ItemAttributes = ["Include", "Exclude"];

        private readonly string _directory = System.IO.Path.GetDirectoryName(path)!;
        private readonly List<ProjectItem> _sources = [];
        private readonly List<ProjectItem> _tests = [];
        private readonly List<NamedPath> _protoPaths = [];
        private NamedPath? _config;
        private bool _failed;

        public ProtoCrossProject? Read()
        {
            RefuseAttributes(file.Root);

            foreach (var element in file.Root.Elements())
            {
                switch (element.Name.LocalName)
                {
                    case "Sources":
                        ReadItem(element, _sources);
                        break;
                    case "Tests":
                        ReadItem(element, _tests);
                        break;
                    case "ProtoPath":
                        ReadProtoPath(element);
                        break;
                    case "Config":
                        ReadConfig(element);
                        break;
                    default:
                        RefuseUnknownElement(element);
                        break;
                }
            }

            return _failed
                ? null
                : new ProtoCrossProject
                {
                    Path = path,
                    Config = _config,
                    Sources = _sources,
                    Tests = _tests,
                    ProtoPaths = _protoPaths,
                };
        }

        private void ReadItem(XElement element, List<ProjectItem> items)
        {
            var name = element.Name.LocalName;
            RefuseAttributes(element, ItemAttributes);
            RefuseChildren(element);

            if (TextOf(element) is { Length: > 0 } text)
            {
                Invalid(
                    element,
                    $"<{name}> names its files with an Include attribute, not with text.",
                    $"Write <{name} Include=\"{text}\" />.");
                return;
            }

            var include = Patterns(element.Attribute("Include"));
            var exclude = Patterns(element.Attribute("Exclude"));

            if (include is { Count: 0 })
            {
                Invalid(
                    element,
                    $"<{name}> needs an Include attribute naming the files it holds.",
                    $"For example, <{name} Include=\"src/**/*.pcross\" />.");
                return;
            }

            if (include is not null && exclude is not null)
            {
                items.Add(new ProjectItem(include, exclude, file.Span(element)));
            }
        }

        /// <summary>
        /// The patterns an attribute lists, separated by semicolons as MSBuild separates them; empty
        /// when the attribute is absent or lists none, and null when one of them cannot be a pattern.
        /// </summary>
        private List<string>? Patterns(XAttribute? attribute)
        {
            var patterns = (attribute?.Value ?? string.Empty)
                .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            // A pattern is matched below a directory, so it has to be written relative to one. A full
            // path in a project that is committed also names a directory on one machine only.
            var rooted = patterns.Where(System.IO.Path.IsPathRooted).ToList();
            foreach (var pattern in rooted)
            {
                Invalid(
                    attribute!,
                    $"'{pattern}' is a full path, and a pattern is matched below the project's directory.",
                    "Write it relative to the project's directory; ../ reaches above it.");
            }

            return rooted.Count == 0 ? patterns : null;
        }

        private void ReadProtoPath(XElement element)
        {
            if (ReadPath(element, "a directory schemas are searched for in") is { } protoPath)
            {
                _protoPaths.Add(protoPath);
            }
        }

        private void ReadConfig(XElement element)
        {
            if (_config is not null)
            {
                Invalid(
                    element,
                    "<Config> is stated more than once.",
                    "One compilation runs under one policy, so a project names one configuration file.");
                return;
            }

            _config = ReadPath(element, "the protocross.config.xml the project compiles under");
        }

        /// <summary>The path an element's text gives, resolved against the project's directory.</summary>
        private NamedPath? ReadPath(XElement element, string meaning)
        {
            var name = element.Name.LocalName;
            RefuseAttributes(element);
            RefuseChildren(element);

            var text = TextOf(element);
            if (text.Length == 0)
            {
                Invalid(element, $"<{name}> is empty.", $"<{name}> names {meaning}, relative to the project's directory.");
                return null;
            }

            try
            {
                return new NamedPath(System.IO.Path.GetFullPath(System.IO.Path.Combine(_directory, text)), file.Span(element));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Invalid(element, $"'{text}' is not a path: {ex.Message}", null);
                return null;
            }
        }

        private void RefuseUnknownElement(XElement element)
        {
            var name = element.Name.LocalName;
            Unknown(
                element,
                $"<{name}> is not an element ProtoCross knows about inside <{RootElement}>.",
                name switch
                {
                    // Help for the two a reader is likeliest to try, because the answer to each is
                    // somewhere else rather than nowhere.
                    "ProtocPath" =>
                        "A project comes with the repository, and a repository does not choose which program "
                        + "the machine runs (spec 10.4.1). Name protoc with the protocross.protocPath setting "
                        + "or PROTOCROSS_PROTOC.",
                    "Arithmetic" or "Presence" =>
                        "Policy is stated in protocross.config.xml, never in a project. Name that file with <Config>.",
                    _ => $"Known elements inside <{RootElement}>: {string.Join(", ", KnownElements)}.",
                });
        }

        private void RefuseChildren(XElement element)
        {
            foreach (var child in element.Elements())
            {
                Unknown(
                    child,
                    $"<{child.Name.LocalName}> is not an element ProtoCross knows about inside <{element.Name.LocalName}>.",
                    $"<{element.Name.LocalName}> contains no elements.");
            }
        }

        private void RefuseAttributes(XElement element, params string[] known)
        {
            var name = element.Name.LocalName;
            // An attribute in a namespace belongs to another vocabulary -- xsi:schemaLocation, which an
            // editor reads for completion, is the usual one -- and says nothing to ProtoCross.
            foreach (var attribute in element.Attributes().Where(attribute => attribute.Name.Namespace == XNamespace.None))
            {
                if (known.Contains(attribute.Name.LocalName, StringComparer.Ordinal))
                {
                    continue;
                }

                Unknown(
                    attribute,
                    $"'{attribute.Name.LocalName}' is not an attribute ProtoCross knows about on <{name}>.",
                    known.Length == 0
                        ? $"<{name}> takes no attributes."
                        : $"Known attributes on <{name}>: {string.Join(", ", known)}.");
            }
        }

        /// <summary>The element's own text, trimmed; empty when it has none but whitespace.</summary>
        private static string TextOf(XElement element)
            => string.Concat(element.Nodes().OfType<XText>().Select(text => text.Value)).Trim();

        private void Unknown(XObject node, string message, string help)
        {
            diagnostics.Report(DiagnosticCodes.UnknownProjectElement, message, file.Span(node), help);
            _failed = true;
        }

        private void Invalid(XObject node, string message, string? help)
        {
            diagnostics.Report(DiagnosticCodes.InvalidProjectSetting, message, file.Span(node), help);
            _failed = true;
        }
    }
}
