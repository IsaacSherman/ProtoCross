namespace ProtoLang.LanguageServer.Workspace;

/// <summary>What the client has said about whether the open workspace is trusted.</summary>
/// <remarks>
/// <para>
/// <b>Three states rather than a boolean</b>, because "the client did not say" is a real answer and a
/// report has to be able to print it. It is treated as trusted -- see <see cref="NotReported"/> -- but a
/// user asking why a committed <c>protoc</c> ran is owed the difference between "you trusted this
/// workspace" and "nothing told the server it was untrusted", and a boolean would have erased it.
/// </para>
/// <para>
/// <b>The server is told rather than deciding.</b> Trust is a decision a person makes in an editor,
/// about a folder, and only the editor saw them make it. What the server owns is what follows from it,
/// which is spec 10.4.1's: the settings that require trust are withheld from workspace and folder
/// scope, and everything else keeps working.
/// </para>
/// </remarks>
public enum WorkspaceTrust
{
    /// <summary>The client said nothing about trust, so the workspace is treated as trusted.</summary>
    /// <remarks>
    /// <para>
    /// The alternative -- treating silence as untrusted -- was considered and rejected. The clients that
    /// say nothing are the ones with no trust model to report: a generic LSP client configured by hand,
    /// whose settings the user wrote themselves. Withholding there would take away a setting with a
    /// message about a trust decision the user has no way to make, and it would change what every
    /// client written before this issue gets.
    /// </para>
    /// <para>
    /// What makes that safe for the editors this project ships is that both of its own clients always
    /// say: VS Code reports its own trust state, and Visual Studio, which has no trust model, reports
    /// <see cref="Untrusted"/> unconditionally. Spec 10.4.1 records both.
    /// </para>
    /// </remarks>
    NotReported,

    /// <summary>The client reported that the user trusts this workspace.</summary>
    Trusted,

    /// <summary>The client reported that the user has not trusted this workspace.</summary>
    Untrusted,
}

/// <summary>How each <see cref="WorkspaceTrust"/> reads, and what it permits.</summary>
public static class WorkspaceTrusts
{
    /// <summary>
    /// Whether settings that require trust may be taken from workspace and folder scope.
    /// </summary>
    /// <remarks>
    /// Asked in exactly one place, <c>WorkspaceConfiguration</c>, so the question of what "not reported"
    /// permits has one answer rather than one per caller.
    /// </remarks>
    public static bool PermitsRestrictedSettings(this WorkspaceTrust trust) => trust is not WorkspaceTrust.Untrusted;

    /// <summary>How this state reads in a status report.</summary>
    public static string Describe(this WorkspaceTrust trust) => trust switch
    {
        WorkspaceTrust.NotReported => "not reported by the client, so treated as trusted",
        WorkspaceTrust.Trusted => "trusted",
        WorkspaceTrust.Untrusted => "untrusted",
        _ => throw new ArgumentOutOfRangeException(nameof(trust), trust, "Unhandled trust state."),
    };
}
