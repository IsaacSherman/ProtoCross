using ProtoCross.Diagnostics;

namespace ProtoCross.LanguageServer.Workspace;

/// <summary>
/// The <c>PC21##</c> diagnostics a host raises about its own configuration: which setting was
/// ignored, which path could not be used, and which protoc was named and could not be run
/// (spec 10.4.1).
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="DiagnosticCodes"/> because these describe an editor's settings, scopes
/// and precedence, and <c>ProtoCross.Core</c> carries nothing editor-specific -- it has no notion of
/// a user scope or a workspace folder to name in a message. Spec 26 gives the range its own row for
/// the same reason. The two tables are checked against each other and against the spec's ranges by
/// <c>DiagnosticCodeTests</c>, so a code allocated twice fails the suite wherever it was written.
/// </para>
/// <para>
/// Every one of these is a warning except the two that stop a document being compiled at all: a
/// configuration file that was found and refused, and a protoc that was named, exists, and cannot be
/// prepared to run. A setting that is merely ignored leaves the defaults in force, which is a
/// situation the author can work in and needs to be told about; the other two leave no policy to
/// compile under.
/// </para>
/// </remarks>
public static class HostDiagnosticCodes
{
    /// <summary>A language-policy setting sent by a client, which only the config file states (spec 10.4.1).</summary>
    public static readonly DiagnosticDescriptor EditorSettingIgnored =
        new("PC2101", DiagnosticSeverity.Warning, "editor setting ignored");

    /// <summary>A setting in this server's section that it does not understand (spec 10.4.1).</summary>
    public static readonly DiagnosticDescriptor UnknownEditorSetting =
        new("PC2102", DiagnosticSeverity.Warning, "unknown editor setting");

    /// <summary>
    /// A path in a setting that could not be made absolute: it is relative and its scope has no
    /// directory to resolve it against, or the path itself is malformed (spec 10.4.1).
    /// </summary>
    /// <remarks>
    /// One code and one title for both, because both say the same thing to the reader -- the path was
    /// not used, and here is why -- and the why is the message's job. The relative case used to carry
    /// its own title, which made the same code render as two different diagnostics.
    /// </remarks>
    public static readonly DiagnosticDescriptor PathCouldNotBeUsed =
        new("PC2103", DiagnosticSeverity.Warning, "path could not be used");

    /// <summary>A configuration file named by a setting that names no file that exists (spec 10.4.1).</summary>
    public static readonly DiagnosticDescriptor ConfigurationFileNotFound =
        new("PC2104", DiagnosticSeverity.Warning, "configuration file not found");

    /// <summary>A protoc named by a setting that is not there, so the next source is used (spec 10.4.1).</summary>
    public static readonly DiagnosticDescriptor ProtocNotFoundWhereItWasNamed =
        new("PC2105", DiagnosticSeverity.Warning, "protoc not found where it was named");

    /// <summary>A configuration file that was found and could not be read, so nothing compiles (spec 10.4.1).</summary>
    public static readonly DiagnosticDescriptor ConfigurationFileRefused =
        new("PC2106", DiagnosticSeverity.Error, "configuration file refused");

    /// <summary>
    /// A protoc named by a setting that exists and still could not be prepared to run, so nothing
    /// compiles for the document (spec 10.4.1).
    /// </summary>
    /// <remarks>
    /// Not <see cref="ProtocNotFoundWhereItWasNamed"/>: that one is a warning about a protoc that is
    /// absent and falls through to the next source, and giving one code two severities would leave a
    /// reader looking up a severity the code is documented never to have.
    /// </remarks>
    public static readonly DiagnosticDescriptor ProtocCouldNotBeUsed =
        new("PC2107", DiagnosticSeverity.Error, "protoc could not be used");
}
