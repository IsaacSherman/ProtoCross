using ProtoCross.Config;
using ProtoCross.Diagnostics;

namespace ProtoCross.LanguageServer.Workspace;

/// <summary>One setting as a client sent it: a key, and the one or many strings under it.</summary>
/// <remarks>
/// Strings rather than JSON, because the model must not depend on how a client serializes its
/// settings. #42 owns the protocol and the deserializer; what reaches here is the flattened result,
/// which is also what a test can write by hand without building a document object model to describe
/// two directories.
/// </remarks>
public sealed record SettingValue(string Key, IReadOnlyList<string> Values)
{
    /// <summary>A setting with one value, which is what all but the path lists are.</summary>
    public SettingValue(string key, string value)
        : this(key, [value])
    {
    }

    /// <summary>The first value that says anything, or null when the setting is present but blank.</summary>
    /// <remarks>
    /// An editor writes an unset string setting as the empty string rather than leaving it out, so
    /// blank has to mean unset. Treating it as a value would have every default overridden by nothing
    /// the moment a user opens the settings page and closes it again.
    /// </remarks>
    public string? Stated => Values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

/// <summary>
/// What an editor may state about ProtoCross, at one scope.
/// </summary>
/// <remarks>
/// <para>
/// Three settings, and the list is short on purpose. Language policy -- overflow, conversions,
/// divide-by-zero, unset message reads -- is not here and will not be: spec 10.4 settles it in
/// <c>protocross.config.xml</c>, tracked beside the code it governs, so that generated code means the
/// same thing however it was built. An editor that could restate it would make a buffer mean one
/// thing on the screen and another in the build, which is the failure the file exists to prevent.
/// What an editor may do is point at a different file, which is exactly what <c>--config</c> does for
/// the command line.
/// </para>
/// <para>
/// A setting that states policy anyway is <em>reported</em> rather than dropped in silence
/// (<c>PC2101</c>), as is one this server does not recognize (<c>PC2102</c>). A user who has written
/// a setting and sees no effect has no way to tell a typo from a refusal from a bug, and guessing
/// between those three is the most expensive minute in a support request.
/// </para>
/// </remarks>
public sealed record ProtoCrossSettings
{
    /// <summary>The settings section a client is asked for, and the prefix a key may carry.</summary>
    public const string Section = "protocross";

    /// <summary>Which protoc to run. The editor's answer to <c>PROTOCROSS_PROTOC</c>.</summary>
    public const string ProtocPathKey = "protocross.protocPath";

    /// <summary>Directories searched for imported schemas. The editor's answer to <c>-I</c>.</summary>
    public const string IncludePathsKey = "protocross.includePaths";

    /// <summary>A <c>protocross.config.xml</c> to use instead of searching. The answer to <c>--config</c>.</summary>
    public const string ConfigPathKey = "protocross.configPath";

    /// <summary>A scope that states nothing.</summary>
    public static ProtoCrossSettings None { get; } = new();

    /// <summary>
    /// Every setting this server reads, in the order they are documented, each declaring whether a
    /// workspace nobody has trusted may state it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the named set spec 10.4.1 asks for, and it is the only list of settings there is.</b>
    /// <see cref="Keys"/>, <see cref="RestrictedKeys"/>, reading a scope and withholding from one are
    /// all derived from it, so a setting cannot be readable without having been classified: adding one
    /// means adding a row here, and a row cannot be written without a <see cref="SettingTrust"/> and a
    /// reason. That is the failure the issue names -- a future setting classified by omission -- made
    /// unwritable rather than discouraged.
    /// </para>
    /// <para>
    /// <b>The test for requiring trust is whether the setting can make this machine run something.</b>
    /// Only <see cref="ProtocPathKey"/> can. The other two direct reads and run nothing, and neither
    /// reaches anywhere a document cannot already reach without them: a <c>.pcross</c> file may
    /// import a schema by a path relative to its own directory, and discovers its own
    /// <c>protocross.config.xml</c> by walking upward. Withholding them would buy nothing and cost an
    /// untrusted repository that relies on an include path every diagnostic it has, which is the
    /// degraded experience the issue asks to keep usable.
    /// </para>
    /// <para>
    /// An executable the <em>extension</em> reads -- where the server itself lives, which runtime
    /// starts it -- never reaches this server, and belongs in the extension manifest's own restricted
    /// list beside this one. #45 carries that.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(
            ProtocPathKey,
            SettingTrust.RequiresTrust,
            "names an executable this server starts",
            settings => settings.ProtocPath is { } path ? [path] : [],
            (settings, values) => settings with { ProtocPath = values.FirstOrDefault(value => Stated(value) is not null) }),
        new(
            IncludePathsKey,
            SettingTrust.Honoured,
            "directs where schemas are read from and starts nothing",
            settings => settings.IncludePaths,
            (settings, values) => settings with { IncludePaths = values }),
        new(
            ConfigPathKey,
            SettingTrust.Honoured,
            "names a policy file that is read and never run",
            settings => settings.ConfigPath is { } path ? [path] : [],
            (settings, values) => settings with { ConfigPath = values.FirstOrDefault(value => Stated(value) is not null) }),
    ];

    /// <summary>Every key this server understands, in the order they are documented.</summary>
    public static IReadOnlyList<string> Keys { get; } = [.. Definitions.Select(definition => definition.Key)];

    /// <summary>The keys withheld from workspace and folder scope until the workspace is trusted.</summary>
    /// <inheritdoc cref="Definitions" path="/remarks"/>
    public static IReadOnlyList<string> RestrictedKeys { get; } =
        [.. Definitions.Where(definition => definition.Trust is SettingTrust.RequiresTrust).Select(definition => definition.Key)];

    /// <inheritdoc cref="ProtocPathKey"/>
    public string? ProtocPath
    {
        get => _protocPath;
        init => _protocPath = Stated(value);
    }

    /// <inheritdoc cref="IncludePathsKey"/>
    /// <remarks>
    /// As written, which may be relative. What they are relative to is a property of the scope that
    /// stated them and not of the list, so resolving happens in
    /// <see cref="WorkspaceConfiguration.Resolve"/> where the scope is known.
    /// </remarks>
    public IReadOnlyList<string> IncludePaths
    {
        get => _includePaths;
        init => _includePaths = value is null ? [] : [.. value.Where(path => Stated(path) is not null)];
    }

    /// <inheritdoc cref="ConfigPathKey"/>
    public string? ConfigPath
    {
        get => _configPath;
        init => _configPath = Stated(value);
    }

    private readonly string? _protocPath;
    private readonly IReadOnlyList<string> _includePaths = [];
    private readonly string? _configPath;

    /// <summary>The value a setting states, or null when it states nothing.</summary>
    /// <remarks>
    /// <para>
    /// Blank means unset, and it is settled here rather than in <see cref="Read"/> so that it is a
    /// property of the type instead of a habit of one method. An editor writes an unset string setting
    /// as the empty string rather than leaving it out, so blank is the ordinary shape of "no answer",
    /// not a malformed one -- and a host is free to build these by hand, deserializing a client's
    /// settings straight into the record, which is exactly the route that would otherwise skip the
    /// normalization.
    /// </para>
    /// <para>
    /// It is not cosmetic. A blank that survived to <see cref="WorkspaceConfiguration.Resolve"/> reached
    /// <see cref="Binding.ProtocLocator.Resolve"/>, which refuses a blank tool name outright -- so a
    /// user clearing a setting in the settings editor took the resolution down with an exception,
    /// rather than falling back to the next source as it should.
    /// </para>
    /// </remarks>
    private static string? Stated(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Whether this scope states anything at all.</summary>
    public bool StatesNothing => ProtocPath is null && ConfigPath is null && IncludePaths.Count == 0;

    /// <summary>What this scope states under <paramref name="key"/>, as written; empty when nothing.</summary>
    /// <remarks>
    /// Accepts a key with or without the <c>protocross.</c> prefix, as <see cref="Read"/> does. A key
    /// this server does not read states nothing rather than throwing, because nothing is what a scope
    /// says about a setting that does not exist.
    /// </remarks>
    public IReadOnlyList<string> ValuesOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Find(key)?.StatedIn(this) ?? [];
    }

    /// <summary>
    /// This scope without the settings that require trust, and which of them it had stated.
    /// </summary>
    /// <param name="withheld">
    /// Each restricted setting this scope actually stated, as written. Empty when it stated none, which
    /// is what lets a caller tell "nothing was withheld" from "the workspace is untrusted", and tell the
    /// user only about the first.
    /// </param>
    /// <remarks>
    /// Which scopes this applies to is not decided here. A scope does not know whether a repository
    /// could have written it -- <c>WorkspaceConfiguration</c> does, and asks this only of the scopes a
    /// repository can write.
    /// </remarks>
    public ProtoCrossSettings WithoutRestricted(out IReadOnlyList<SettingValue> withheld)
    {
        var kept = this;
        var removed = new List<SettingValue>();

        foreach (var definition in Definitions.Where(definition => definition.Trust is SettingTrust.RequiresTrust))
        {
            var stated = definition.StatedIn(this);

            if (stated.Count == 0)
            {
                continue;
            }

            removed.Add(new SettingValue(definition.Key, stated));
            kept = definition.With(kept, []);
        }

        withheld = removed;
        return kept;
    }

    /// <summary>
    /// Reads what a client sent for one scope, reporting every entry that will not be used.
    /// </summary>
    /// <param name="scope">
    /// Which scope these were written at, so a diagnostic can say which settings page to open.
    /// </param>
    public static ProtoCrossSettings Read(
        IEnumerable<SettingValue> values,
        ConfigurationSource scope,
        DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var settings = None;

        foreach (var value in values)
        {
            if (Find(value.Key) is { } definition)
            {
                settings = definition.With(settings, value.Values);
            }
            else
            {
                Refuse(value, scope, diagnostics);
            }
        }

        return settings;
    }

    /// <summary>The setting <paramref name="key"/> names, or null when this server reads no such setting.</summary>
    /// <remarks>
    /// Case-insensitive on the name after the prefix, which is what <see cref="Read"/> accepted before
    /// the settings were a table.
    /// </remarks>
    private static SettingDefinition? Find(string key)
        => NameOf(key) is { } name
            ? Definitions.FirstOrDefault(definition => string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <remarks>
    /// A key stating language policy is told where that policy lives; anything else is told what this
    /// server does understand. The two are separate diagnostics because they call for different
    /// actions -- move the setting, or fix the spelling -- and a single "unknown setting" would send a
    /// user who wrote <c>protocross.overflow</c> looking for a typo that is not there.
    /// </remarks>
    private static void Refuse(SettingValue value, ConfigurationSource scope, DiagnosticBag diagnostics)
    {
        var span = new SourceSpan(scope.Label(), SourcePosition.None, SourcePosition.None);

        if (PolicyKeyFor(value.Key) is { } policyKey)
        {
            diagnostics.Warning(
                "PC2101",
                "editor setting ignored",
                $"'{value.Key}' is being ignored: language policy is stated in {ProjectConfig.FileName}, "
                    + "not in editor settings.",
                span,
                $"Put <{policyKey.Split('/')[^1]}> in the <{policyKey.Split('/')[0]}> section of a "
                    + $"{ProjectConfig.FileName}, so that a build and this editor agree about what the "
                    + $"code means. Use '{ConfigPathKey}' to point at a particular one.");
            return;
        }

        diagnostics.Warning(
            "PC2102",
            "unknown editor setting",
            $"'{value.Key}' is not a setting this server understands, so it is being ignored.",
            span,
            $"Settings this server reads: {string.Join(", ", Keys)}.");
    }

    /// <summary>
    /// The <c>protocross.config.xml</c> setting this key is trying to state, or null if it is not
    /// trying to state one.
    /// </summary>
    /// <remarks>
    /// Matched on the last segment, so <c>protocross.overflow</c>, <c>protocross.arithmetic.overflow</c>
    /// and a bare <c>Overflow</c> are all recognized as the same attempt. The list comes from
    /// <see cref="ProjectConfig.Keys"/> rather than being restated here, so a setting added to the
    /// file is refused in an editor without anyone remembering to add it in two places.
    /// </remarks>
    private static string? PolicyKeyFor(string key)
    {
        var leaf = Leaf(key);

        return ProjectConfig.Keys.FirstOrDefault(
            candidate => string.Equals(Leaf(candidate), leaf, StringComparison.OrdinalIgnoreCase));
    }

    /// <remarks>
    /// A client that was asked for the <c>protocross</c> section sends bare keys, and one that sends
    /// its whole settings tree sends qualified ones. Both are the same setting, so the prefix is
    /// stripped rather than being one more thing for a caller to get right.
    /// </remarks>
    private static string? NameOf(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var name = key.StartsWith(Section + ".", StringComparison.OrdinalIgnoreCase)
            ? key[(Section.Length + 1)..]
            : key;

        return name.Length == 0 ? null : name;
    }

    private static string Leaf(string key)
    {
        var separator = key.LastIndexOfAny(['.', '/', ':']);
        return separator < 0 ? key : key[(separator + 1)..];
    }
}
