namespace ProtoLang.LanguageServer.Protocol.Lsp;

/// <summary>Every LSP method this server sends or answers, spelled once.</summary>
/// <remarks>
/// A method name is a string a client and a server have to agree on exactly, and a typo in one is a
/// feature that silently does not exist. Registration and dispatch both read from here.
/// </remarks>
public static class Methods
{
    public const string Initialize = "initialize";
    public const string Initialized = "initialized";
    public const string Shutdown = "shutdown";
    public const string Exit = "exit";
    public const string CancelRequest = "$/cancelRequest";
    public const string SetTrace = "$/setTrace";

    public const string DidOpen = "textDocument/didOpen";
    public const string DidChange = "textDocument/didChange";
    public const string DidClose = "textDocument/didClose";
    public const string DidSave = "textDocument/didSave";
    public const string PublishDiagnostics = "textDocument/publishDiagnostics";
    public const string SemanticTokensFull = "textDocument/semanticTokens/full";
    public const string SemanticTokensFullDelta = "textDocument/semanticTokens/full/delta";
    public const string Completion = "textDocument/completion";
    public const string Hover = "textDocument/hover";
    public const string Definition = "textDocument/definition";
    public const string DocumentSymbol = "textDocument/documentSymbol";
    public const string References = "textDocument/references";
    public const string DocumentHighlight = "textDocument/documentHighlight";
    public const string SignatureHelp = "textDocument/signatureHelp";

    public const string DidChangeConfiguration = "workspace/didChangeConfiguration";
    public const string DidChangeWorkspaceFolders = "workspace/didChangeWorkspaceFolders";
    public const string Configuration = "workspace/configuration";
    public const string DidChangeWatchedFiles = "workspace/didChangeWatchedFiles";

    public const string RegisterCapability = "client/registerCapability";

    public const string LogMessage = "window/logMessage";
    public const string ShowMessage = "window/showMessage";

    /// <summary>The status report, which is this server's own method rather than one of LSP's.</summary>
    /// <remarks>
    /// Prefixed with the language name, as the protocol asks of an extension: <c>$/</c> is reserved
    /// for the protocol itself, and an unprefixed name risks colliding with a method LSP may define
    /// later. A custom request rather than a command, because <c>workspace/executeCommand</c> is for
    /// the server asking the client to change something, and this changes nothing -- it is a
    /// question.
    /// </remarks>
    public const string Status = "protolang/status";

    /// <summary>The client telling the server that the user has changed whether they trust the workspace.</summary>
    /// <remarks>
    /// This server's own notification, prefixed for the reason <see cref="Status"/> is. LSP has no
    /// notion of workspace trust, so there is no standard method to prefer.
    /// </remarks>
    public const string DidChangeWorkspaceTrust = "protolang/didChangeWorkspaceTrust";
}
