namespace ProtoLang.LanguageServer.Protocol.Lsp;

/// <summary>The workspace's trust state has changed.</summary>
/// <param name="Trusted">
/// Whether the user now trusts the workspace, or null when the notification did not carry a boolean.
/// </param>
/// <remarks>
/// <para>
/// The client's half of spec 10.4.1's trust rule, beside the <c>workspaceTrusted</c> initialization
/// option that states where a session begins. VS Code sends it from <c>onDidGrantWorkspaceTrust</c>;
/// Visual Studio, which has no trust model, never sends it and begins untrusted.
/// </para>
/// <para>
/// Both directions are accepted though VS Code only grants: withdrawing trust there reloads the
/// window, and a reloaded window is a new server. A second client that can withdraw trust in place
/// should not find the server able to hear half of it.
/// </para>
/// <para>
/// <b>Nullable, so that a malformed notification changes nothing.</b> A plain <c>bool</c> reads a
/// missing or misspelt member as <c>false</c>, and the server would go untrusted over a message that
/// said nothing about trust -- recompiling every document without the user's protoc and warning them
/// about a decision they never made. Staying in the state already in force is the only reading that
/// does not invent an answer.
/// </para>
/// </remarks>
public sealed record WorkspaceTrustParams(bool? Trusted);
