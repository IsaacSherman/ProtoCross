using System.Diagnostics.CodeAnalysis;

namespace ProtoCross.Backend;

/// <summary>
/// The namespace a project's generated behavior is declared in, named as a protobuf package is named:
/// identifiers separated by periods, such as <c>acme.billing</c> (spec 24). Each backend spells it as
/// protoc spells a package of that name.
/// </summary>
/// <remarks>
/// <para>
/// A library declares symbols only in a namespace it owns. Behavior declared beside the message it
/// extends is declared in the namespace of whoever wrote the schema, and two libraries that extend one
/// message there declare the same names: in C# two classes a consumer referencing both cannot tell
/// apart, and in C++ two <c>inline</c> functions the linker merges without a word, keeping one body for
/// both. Protobuf's own extensions are placed the same way, in the package of the file that declares
/// them rather than the package of the message they extend.
/// </para>
/// <para>
/// Named as a package rather than as a namespace of either target, because it is one name that every
/// target has to spell, and protoc already settles how each spells a package. A project named after the
/// package of its own schemas therefore declares its behavior beside their messages.
/// </para>
/// <para>
/// Narrower than a package: every component begins with a letter. protoc's C# rule keeps an underscore
/// in front of a digit only at the start of a package, so a later component beginning with one names
/// no C# namespace (<c>acme._1x</c> is <c>Acme.1X</c>), and C++ reserves names beginning with an
/// underscore at global scope. A name one of the targets cannot spell is no use to a project whose
/// only reason for having it is that every target spells it.
/// </para>
/// </remarks>
public sealed record ProjectNamespace
{
    /// <summary>The namespace named <paramref name="package"/>.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="package"/> is not a name <see cref="TryParse"/> accepts.
    /// </exception>
    public ProjectNamespace(string package)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (!IsPackageName(package))
        {
            throw new ArgumentException(
                $"'{package}' cannot name a namespace; see ProjectNamespace.TryParse.", nameof(package));
        }

        Package = package;
    }

    /// <summary>The name as a package is written: <c>acme.billing</c>.</summary>
    public string Package { get; }

    /// <summary>
    /// The namespace <paramref name="name"/> names, when it is one: identifiers separated by periods,
    /// each a letter followed by letters, digits and underscores.
    /// </summary>
    public static bool TryParse(string name, [NotNullWhen(true)] out ProjectNamespace? result)
    {
        ArgumentNullException.ThrowIfNull(name);

        result = IsPackageName(name) ? new ProjectNamespace(name) : null;
        return result is not null;
    }

    public override string ToString() => Package;

    private static bool IsPackageName(string name) => name.Split('.').All(IsComponent);

    private static bool IsComponent(string component)
        => component.Length > 0
            && char.IsAsciiLetter(component[0])
            && component.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
