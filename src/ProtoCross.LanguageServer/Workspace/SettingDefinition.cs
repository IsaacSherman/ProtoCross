namespace ProtoCross.LanguageServer.Workspace;

/// <summary>Whether a setting may be taken from a workspace nobody has trusted.</summary>
/// <remarks>
/// A required part of every <see cref="SettingDefinition"/>, with no default, so that a setting cannot
/// be added without somebody deciding this. See <see cref="ProtoCrossSettings.Definitions"/> for the
/// test each existing setting was held to.
/// </remarks>
public enum SettingTrust
{
    /// <summary>Taken from every scope, whether or not the workspace is trusted.</summary>
    Honoured,

    /// <summary>
    /// Withheld from workspace and folder scope while the workspace is untrusted, because a repository
    /// could otherwise use it to make this machine run something.
    /// </summary>
    RequiresTrust,
}

/// <summary>One setting this server reads: its key, what an untrusted workspace may do with it, and why.</summary>
/// <remarks>
/// <para>
/// Carries how to read and write its own value on a <see cref="ProtoCrossSettings"/>, so that the key,
/// its classification and the property it lands in are one row. The alternative -- a table of keys
/// beside a switch over key names -- is two lists that have to agree, and the one place they would stop
/// agreeing is a new setting that was readable and never classified.
/// </para>
/// <para>
/// Constructed only inside this assembly, because the settings a server reads are the server's to
/// declare. What a caller may do with one is read its classification.
/// </para>
/// </remarks>
public sealed record SettingDefinition
{
    private readonly Func<ProtoCrossSettings, IReadOnlyList<string>> _read;
    private readonly Func<ProtoCrossSettings, IReadOnlyList<string>, ProtoCrossSettings> _write;

    internal SettingDefinition(
        string key,
        SettingTrust trust,
        string because,
        Func<ProtoCrossSettings, IReadOnlyList<string>> read,
        Func<ProtoCrossSettings, IReadOnlyList<string>, ProtoCrossSettings> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(because);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);

        if (!key.StartsWith(ProtoCrossSettings.Section + ".", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"'{key}' must be written with the '{ProtoCrossSettings.Section}.' prefix, which is how a user sees it.",
                nameof(key));
        }

        Key = key;
        Trust = trust;
        Because = because;
        _read = read;
        _write = write;
    }

    /// <summary>The key as a user writes it, prefix included.</summary>
    public string Key { get; }

    /// <summary>The key without its prefix, which is what a client asked for the section sends.</summary>
    public string Name => Key[(ProtoCrossSettings.Section.Length + 1)..];

    /// <inheritdoc cref="SettingTrust"/>
    public SettingTrust Trust { get; }

    /// <summary>Why it is classified the way it is, as a clause a report can quote.</summary>
    public string Because { get; }

    /// <summary>What <paramref name="settings"/> states for this setting, as written; empty when nothing.</summary>
    internal IReadOnlyList<string> StatedIn(ProtoCrossSettings settings) => _read(settings);

    /// <summary><paramref name="settings"/> with this setting stating <paramref name="values"/> instead.</summary>
    /// <remarks>An empty list clears it, which is how a setting is withheld.</remarks>
    internal ProtoCrossSettings With(ProtoCrossSettings settings, IReadOnlyList<string> values) => _write(settings, values);
}
