# ProtoCross architecture

A map for a cold start: what exists, where it lives, and which invariants constrain a change. The
language itself is specified in [ProtoCross_Spec/](ProtoCross_Spec/README.md); how to write code here is in
[CLAUDE.md](CLAUDE.md); the per-issue process is in
[docs/language-1-workflow.md](docs/language-1-workflow.md) for the 1.0 language epic and
[docs/epic-47-workflow.md](docs/epic-47-workflow.md) for the editor-support epic; what the server is held to for latency, and
what that was measured to be, is in [docs/performance.md](docs/performance.md).

ProtoCross compiles small methods written against protobuf messages into equivalent C# and C++.
Behavior is defined once and generated per target, and it has to mean the same thing in each.

## The solution

`ProtoCross.slnx`, seven projects, `net10.0`. Settings are central in
[Directory.Build.props](Directory.Build.props): nullable enabled, implicit usings, **warnings as
errors**, and `CheckForOverflowUnderflow=false` on purpose — the compiler must never inherit the
arithmetic behavior it exists to define.

| Project | Role |
|---|---|
| [src/ProtoCross.Core](src/ProtoCross.Core) | Lexer, parser, binder, IR, diagnostics, config. No CLI coupling. |
| [src/ProtoCross.Backend.CSharp](src/ProtoCross.Backend.CSharp) | C# emission, plus generated test projects. |
| [src/ProtoCross.Backend.Cpp](src/ProtoCross.Backend.Cpp) | The same for C++. |
| [src/ProtoCross.Cli](src/ProtoCross.Cli) | `protocross`: argument parsing, driving a compilation, writing files. |
| [src/ProtoCross.LanguageServer](src/ProtoCross.LanguageServer) | `protocross-server`: LSP over stdio, and the workspace configuration model under it. |
| [src/ProtoCross.Projects](src/ProtoCross.Projects) | Reading a `.pcproj`, and finding the sources its patterns match. |
| [tests/ProtoCross.Tests](tests/ProtoCross.Tests) | One xunit project covering all of it. |

Dependencies run one way. Backends, the CLI and the language server reference Core; **Core
references nothing in the repo**. That is what lets a language server consume the compiler without
dragging the CLI along, and it is worth preserving. `ProtoCross.Projects` references Core and a
globbing package and nothing else, so that listing directories to find sources happens beside Core
rather than in it.

Outside the solution, [editors/vscode](editors/vscode) is the VS Code extension: TypeScript, built with
npm, and consuming the server only as a process it starts. See *The VS Code extension* below.

## The pipeline

Driven by [`Compilation`](src/ProtoCross.Core/Compilation.cs). Three doors into it: the constructor
(hold it to recompile the same buffer), `Compile(SourceDocument, …)`, and `Compile(string path, …)`,
each with a form that takes a list of sources.

1. **Source.** [`SourceDocument`](src/ProtoCross.Core/SourceDocument.cs) is text plus a
   `SourceIdentity` — the name diagnostics print, the directory that settles policy and anchors
   imports, and the path, which is `null` for a buffer that was never saved. `ReadFrom` is the only
   place the compiler reads ProtoCross source from disk. A compilation takes one source or several;
   several are one program, and are refused (`PC2006`) when two would be generated under one name —
   compared by `NameConventions.OutputKey`, which folds a name the way every generated name does.
2. **Policy.** The nearest `protocross.config.xml` at or above the source directory
   ([`ProjectConfig.Discover`/`Load`](src/ProtoCross.Core/Config/ProjectConfig.cs)). Every source of
   a compilation must find the same one (`PC2005`). A config that
   exists and cannot be read **stops** the compilation rather than falling back to defaults. A host
   serving an editor settles this per document instead, through
   [`WorkspaceConfiguration`](src/ProtoCross.LanguageServer/Workspace/WorkspaceConfiguration.cs) — see
   *Configuration* below.
3. **Lex.** [`Lexer.Tokenize`](src/ProtoCross.Core/Syntax/Lexer.cs) → `List<Token>`. No token spans
   more than one line.
4. **Parse.** [`Parser.ParseCompilationUnit`](src/ProtoCross.Core/Syntax/Parser.cs) → the AST in
   [Ast.cs](src/ProtoCross.Core/Syntax/Ast.cs). Recursive descent, error-recovering, depth-budgeted
   (`MaxNestingDepth`) because a `StackOverflowException` cannot be caught. The budget bounds the
   tree as well as the recursion: an expression built by a loop -- `a.b.c`, `1 + 2 + 3` -- is held
   to it by height, because every later stage recurses over what the parser builds. A name it
   expected and did not find is a [`SyntaxName`](src/ProtoCross.Core/Syntax/SyntaxName.cs) that
   says so, carrying the empty range where the name would go.
5. **No gate.** Parse errors do not stop the pipeline. A buffer being typed into is broken most of
   the time an editor asks anything about it, and what it most often asks — what may follow this
   dot — only the binder can answer.
6. **Descriptors.** Every source's imports are loaded together, each schema once, and every source
   binds against all of them. Each import is resolved into an
   [`ImportResolution`](src/ProtoCross.Core/ImportResolution.cs) — resolved, not found, or never
   written — against the roots
   [`SchemaCatalog.RootsFor`](src/ProtoCross.Core/Binding/SchemaCatalog.cs) settles: the search paths,
   then the loader's own. The whole list is published on the result, and one that was not found is
   told which schema in the directory it named it came closest to. Then
   [`DescriptorLoader`](src/ProtoCross.Core/Binding/DescriptorLoader.cs) shells out to `protoc`
   (located by [`ProtocLocator`](src/ProtoCross.Core/Binding/ProtocLocator.cs)) and returns a
   [`DescriptorBundle`](src/ProtoCross.Core/Binding/DescriptorBundle.cs): the built `FileDescriptor`s,
   the `FileDescriptorSet` they came from with its `--include_source_info` source info, and the file
   each schema in the transitive closure was read from. `Load` still returns the descriptor list
   alone, so no existing caller moved. A [`DescriptorCache`](src/ProtoCross.Core/Binding/DescriptorCache.cs)
   on the loader's options keeps bundles, keyed by a
   [`DescriptorRequest`](src/ProtoCross.Core/Binding/DescriptorRequest.cs) — which `protoc`, which
   roots in which order, which files — and re-checked against a content hash of every file in the
   closure, because the request cannot name a schema that is only reached through an import. The
   loader is uncached unless a caller supplies one, `protoc` runs under a timeout — reported as
   `PC0083` and as a kind on the failure, so an expiry is not mistaken for a schema error — and a
   failure keeps its report line by line as
   [`ProtocDiagnostic`](src/ProtoCross.Core/Binding/ProtocDiagnostic.cs) rather than only as prose.
   A cached entry holds the load as a `Task` rather than a `Lazy`, which is what lets a caller
   abandon its wait: `LoadBundle` and `Compile` take a `CancellationToken` that stops the *waiting*,
   and stops `protoc` itself only for a load no cache holds, because a cached load belongs to the
   cache and its successor usually wants exactly it. The source info the set carries is answered rather than merely kept:
   `DescriptorBundle.DeclarationOf` turns a message, enum, field or enum value descriptor into a
   [`SchemaDeclaration`](src/ProtoCross.Core/Symbols/SchemaDeclaration.cs) — the `.proto` it was
   written in, the range of the declaration and of its name, and the comments around it — through a
   per-file [`SchemaSourceIndex`](src/ProtoCross.Core/Binding/SchemaSourceIndex.cs) built on first ask
   and kept on the bundle. The same question is answerable from a `SymbolId` rather than a descriptor,
   which is the handle a caret produces: a name in type position resolves to a type and leaves no IR
   node behind, so an identity is all there is to ask with. Both doors are fed by one walk over what a
   schema declares ([`SchemaSymbols`](src/ProtoCross.Core/Binding/SchemaSymbols.cs)) — the bundle uses
   it to index which file declares each identity, the source index to address each declaration's
   `SourceCodeInfo` path — because two descents disagree first about an enum nested in a message.
   That is what lets go-to-definition and hover cross the file boundary, which is where most of what a
   ProtoCross file talks about lives.
7. **Bind.** [`Binder.Bind`](src/ProtoCross.Core/Binding/Binder.cs) resolves names against the
   descriptors and produces typed IR. It binds several sources, each a
   [`SourceTree`](src/ProtoCross.Core/SourceTree.cs), into one module as readily as one: every source's methods are declared before any body is bound,
   so a call or a test may reach from one source into another, and `IrModule.DeclaredIn` divides the
   module back into what each source declares. Sugar ends here: a compound assignment `x += y` is bound as the
   assignment of `x + y` to `x`, so the IR has no node for one and no backend knows it exists. It does
   **not** throw on bad input: an unresolved name becomes `ErrorType` (`PC0037`) and binding
   continues, a name the parser never saw resolves to `ErrorType`
   in silence, and an extend block whose receiver cannot be resolved is skipped because there is no
   message to bind against. Declarations inside a resolvable receiver are kept as far as possible:
   every local, parameter, loop binding and method carries a
   [`DeclarationSite`](src/ProtoCross.Core/Symbols/DeclarationSite.cs), so a reference can reach the
   declaration it means and say which symbol that is. It also records the other direction as it
   goes: every name it resolves becomes a [`SymbolReference`](src/ProtoCross.Core/Symbols/SymbolReference.cs)
   in `IrModule.References`, spanning the name alone. That has to happen here rather than in a later
   pass, because a type reference resolves to a type and leaves no IR node behind, and because the
   spans the IR does carry are extents — `IrMethodCall` covers its arguments — rather than names. Its
   `Scope` chain is published the same way, as a flat list of
   [`ScopeEntry`](src/ProtoCross.Core/Symbols/ScopeEntry.cs) in `IrModule.Scope`: one entry per name
   that *entered* a scope, carrying the range it can be written over and the offset it starts
   resolving from. Recorded where each name is declared, because whether a name won is decided there
   and nowhere else — a parameter with no name, a second parameter of one name, and a `var` that
   collides with an enclosing one are all still in the IR and all resolve nothing, and the tree does
   not say so.
8. **Result.** `CompilationResult` carries the IR *even when the file did not parse*, the syntax
   tree, the descriptors, the whole `Schema` bundle they came from, the import outcomes, the
   diagnostics, the settled config, and the search paths that were used. When the schemas could not
   be loaded it carries `SchemaFailure` instead — `protoc`'s own report, line by line with positions,
   beside the `PC0003` that renders it as prose. `Module` is null only when the compilation stopped before the binder: an
   unreadable config, an unusable include path, or a schema that could not be found or loaded.
   **`Module` is the partial one. Emit from `EmittableModule`**, which is null unless the
   compilation produced a whole program.
9. **Emit.** Backends consume the IR only, one source at a time:
   [`SourceEmission`](src/ProtoCross.Core/Backend/SourceEmission.cs) hands each backend one source's
   part of the module (`IrModule.DeclaredIn`) with the options that name its files
   (`BackendOptions.For`), and keeps one copy of the runtime file every source's output shares.
   It also divides the output by each source's `SourceRole` (spec 25.3): a production source's
   behavior goes to the behavior output, and a test source's goes to the test output with every
   source's tests. The binder is what makes that division sound: a production method that calls a
   test source's method is `PC0088`, so nothing in the behavior output calls into the test output.
   A production build sets `CompilationOptions.SkipTests`, and the binder never sees a test.

Both trees are **addressable**: [`SemanticModel.For(result)`](src/ProtoCross.Core/Semantics/SemanticModel.cs)
answers "what is at this offset" for the syntax tree and for the IR, hands back the chain of nodes
above the answer, and crosses between the two by span. The rule the awkward positions follow — a
caret at the end of an identifier, between two nodes, on an empty range — is written once in
`PositionSearch` and documented on the query methods.

The same model answers the reference questions: `ReferenceAt` turns a caret into a symbol,
`ReferencesTo` gives every place that symbol is written with its declaration among them and marked,
and `DeclarationOf` gives the two ranges an editor navigates with — null for a field, an enum
constant or a type, whose declaration is in a `.proto` this compiler does not own. Nothing is cached:
a keystroke produces a new compilation and a new model over it, and the index that merges the
binder's references with the declarations is built on the first question that needs it.

What the model deliberately cannot answer is where a schema element is declared, and it says so:
that is a `.proto` this compiler does not own. The other side is `DescriptorBundle.DeclarationOf`,
which takes a descriptor or a `SymbolId`, and `SchemaTypes.Find`, which turns an identity back into
the message or enum it names. A host joins the two — see *Serving an editor* — and exactly one of them
answers for any symbol that resolved.

`ScopeAt` is the third question: what a bare identifier written at this offset could mean, as the
names in scope there with their types and declarations, plus the receiver they are looked up against.
It is narrower than "everything nameable" on purpose — a method resolves only in call position and a
type only in type position, so neither is in the list, and where a local and a field of the receiver
share a spelling only the one that binds is. That contract, *everything offered binds and nothing
that binds is missing*, is what makes it safe for completion to accept an entry blind.

## Key types

| Concern | Type | File |
|---|---|---|
| Location | `SourceSpan`, `SourcePosition` | [Diagnostics/SourceSpan.cs](src/ProtoCross.Core/Diagnostics/SourceSpan.cs) |
| Whether two paths are one path | `PathIdentity` | [PathIdentity.cs](src/ProtoCross.Core/PathIdentity.cs) |
| A document, to an editor and to the compiler | `DocumentUri` | [Workspace/DocumentUri.cs](src/ProtoCross.LanguageServer/Workspace/DocumentUri.cs) |
| What an editor may configure, and where it wins | `WorkspaceConfiguration`, `ProtoCrossSettings` | [Workspace/WorkspaceConfiguration.cs](src/ProtoCross.LanguageServer/Workspace/WorkspaceConfiguration.cs) |
| What one document compiles under | `DocumentConfiguration`, `ConfigurationSource` | [Workspace/DocumentConfiguration.cs](src/ProtoCross.LanguageServer/Workspace/DocumentConfiguration.cs) |
| Which settings an untrusted workspace may not state, and what it stated anyway | `SettingDefinition`, `SettingTrust`, `WorkspaceTrust`, `WithheldSetting` | [Workspace/SettingDefinition.cs](src/ProtoCross.LanguageServer/Workspace/SettingDefinition.cs), [Workspace/WorkspaceTrust.cs](src/ProtoCross.LanguageServer/Workspace/WorkspaceTrust.cs) |
| One JSON-RPC conversation | `JsonRpcConnection`, `MessageReader` | [Protocol/JsonRpcConnection.cs](src/ProtoCross.LanguageServer/Protocol/JsonRpcConnection.cs) |
| The server itself | `LanguageServerHost` | [Hosting/LanguageServerHost.cs](src/ProtoCross.LanguageServer/Hosting/LanguageServerHost.cs) |
| Who is told what is wrong with which file | `DiagnosticRouter`, `DiagnosticContribution` | [Hosting/DiagnosticRouter.cs](src/ProtoCross.LanguageServer/Hosting/DiagnosticRouter.cs) |
| What the compiler tells an editor to colour | `SemanticTokenLegend`, `SemanticTokenEncoder` | [Hosting/SemanticTokenLegend.cs](src/ProtoCross.LanguageServer/Hosting/SemanticTokenLegend.cs) |
| Who serves that, and what this client can paint | `ClassificationProvider`, `ClientLegend`, `SemanticTokenDiff` | [Hosting/ClassificationProvider.cs](src/ProtoCross.LanguageServer/Hosting/ClassificationProvider.cs), [Hosting/ClientLegend.cs](src/ProtoCross.LanguageServer/Hosting/ClientLegend.cs) |
| Where a comment was | `Comment` | [Syntax/Comment.cs](src/ProtoCross.Core/Syntax/Comment.cs) |
| Written or not-yet-written names | `SyntaxName` | [Syntax/SyntaxName.cs](src/ProtoCross.Core/Syntax/SyntaxName.cs) |
| What became of an import | `ImportResolution` | [ImportResolution.cs](src/ProtoCross.Core/ImportResolution.cs) |
| Which file a schema path names | `SchemaLookup` | [Binding/SchemaLookup.cs](src/ProtoCross.Core/Binding/SchemaLookup.cs) |
| Which roots are searched, and what they hold | `SchemaCatalog`, `SchemaCandidate` | [Binding/SchemaCatalog.cs](src/ProtoCross.Core/Binding/SchemaCatalog.cs) |
| What could be typed at a position | `CompletionProvider`, `ImportPathContext` | [Hosting/CompletionProvider.cs](src/ProtoCross.LanguageServer/Hosting/CompletionProvider.cs) |
| What a request is about, and what it owes for leaving the process | `DocumentRequest`, `DeferredAnswers` | [Hosting/DocumentRequest.cs](src/ProtoCross.LanguageServer/Hosting/DocumentRequest.cs), [Hosting/DeferredAnswers.cs](src/ProtoCross.LanguageServer/Hosting/DeferredAnswers.cs) |
| What the caret names, and where that was declared | `DeclaredSymbol` | [Hosting/DeclaredSymbol.cs](src/ProtoCross.LanguageServer/Hosting/DeclaredSymbol.cs) |
| Everywhere that symbol is written | `SymbolOccurrences`, `ReferenceProvider`, `HighlightProvider` | [Hosting/SymbolOccurrences.cs](src/ProtoCross.LanguageServer/Hosting/SymbolOccurrences.cs), [Hosting/ReferenceProvider.cs](src/ProtoCross.LanguageServer/Hosting/ReferenceProvider.cs) |
| Which document a span is in, as the client spells it | `SymbolLocations` | [Hosting/SymbolLocations.cs](src/ProtoCross.LanguageServer/Hosting/SymbolLocations.cs) |
| The call being typed, and what it expects next | `CallSubject`, `SignatureHelpProvider` | [Hosting/CallSubject.cs](src/ProtoCross.LanguageServer/Hosting/CallSubject.cs), [Hosting/SignatureHelpProvider.cs](src/ProtoCross.LanguageServer/Hosting/SignatureHelpProvider.cs) |
| What the pointer resting somewhere says | `HoverProvider`, `HoverCard` | [Hosting/HoverProvider.cs](src/ProtoCross.LanguageServer/Hosting/HoverProvider.cs), [Hosting/HoverCard.cs](src/ProtoCross.LanguageServer/Hosting/HoverCard.cs) |
| Where a name leads | `DefinitionProvider` | [Hosting/DefinitionProvider.cs](src/ProtoCross.LanguageServer/Hosting/DefinitionProvider.cs) |
| The shape of a file | `DocumentOutline` | [Hosting/DocumentOutline.cs](src/ProtoCross.LanguageServer/Hosting/DocumentOutline.cs) |
| Compiler coordinates ↔ editor coordinates | `EditorPositions` | [Protocol/Lsp/EditorPositions.cs](src/ProtoCross.LanguageServer/Protocol/Lsp/EditorPositions.cs) |
| What a descriptor load produced | `DescriptorBundle`, `SchemaFile` | [Binding/DescriptorBundle.cs](src/ProtoCross.Core/Binding/DescriptorBundle.cs) |
| What decides a load, and keys it | `DescriptorRequest` | [Binding/DescriptorRequest.cs](src/ProtoCross.Core/Binding/DescriptorRequest.cs) |
| Whether a load can be reused | `DescriptorCache`, `SchemaClosure` | [Binding/DescriptorCache.cs](src/ProtoCross.Core/Binding/DescriptorCache.cs) |
| Which `protoc` runs, why that one, and what it is | `ProtocSelection`, `ProtocSource`, `ProtocVersion` | [Binding/ProtocSelection.cs](src/ProtoCross.Core/Binding/ProtocSelection.cs) |
| What the server says about itself when asked | `StatusReporter`, `ServerStatus`, `StatusFact` | [Hosting/StatusReporter.cs](src/ProtoCross.LanguageServer/Hosting/StatusReporter.cs), [Hosting/ServerStatus.cs](src/ProtoCross.LanguageServer/Hosting/ServerStatus.cs) |
| How long an answer may take, and how long they have been taking | `PerformanceBudgets`, `RequestTimings`, `LatencySample` | [Hosting/PerformanceBudgets.cs](src/ProtoCross.LanguageServer/Hosting/PerformanceBudgets.cs), [Hosting/RequestTimings.cs](src/ProtoCross.LanguageServer/Hosting/RequestTimings.cs) |
| Which way a load failed | `DescriptorLoadFailureKind`, `SchemaLoadFailure` | [Binding/DescriptorLoadFailureKind.cs](src/ProtoCross.Core/Binding/DescriptorLoadFailureKind.cs) |
| When a document is compiled, and whether the answer still counts | `CompileScheduler` | [Hosting/CompileScheduler.cs](src/ProtoCross.LanguageServer/Hosting/CompileScheduler.cs) |
| Which files on disk move a compilation, and what the client is asked to watch | `WatchedFiles` | [Hosting/WatchedFiles.cs](src/ProtoCross.LanguageServer/Hosting/WatchedFiles.cs) |
| What an editor user with no protoc is told | `MissingProtoc` | [Hosting/MissingProtoc.cs](src/ProtoCross.LanguageServer/Hosting/MissingProtoc.cs) |
| What `protoc` said, and about where | `ProtocDiagnostic`, `SchemaLoadFailure` | [Binding/ProtocDiagnostic.cs](src/ProtoCross.Core/Binding/ProtocDiagnostic.cs), [SchemaLoadFailure.cs](src/ProtoCross.Core/SchemaLoadFailure.cs) |
| Offset ↔ line/column | `LineMap` | [Diagnostics/LineMap.cs](src/ProtoCross.Core/Diagnostics/LineMap.cs) |
| Messages | `Diagnostic`, `DiagnosticBag` | [Diagnostics/Diagnostic.cs](src/ProtoCross.Core/Diagnostics/Diagnostic.cs) |
| Type system | `PlType` and friends | [Types/PlType.cs](src/ProtoCross.Core/Types/PlType.cs) |
| Typed IR | `IrNode`, `IrModule` … `IrLiteral` | [Ir/Ir.cs](src/ProtoCross.Core/Ir/Ir.cs) |
| Position and reference queries | `SemanticModel` | [Semantics/SemanticModel.cs](src/ProtoCross.Core/Semantics/SemanticModel.cs) |
| What is here, and what holds it | `SyntaxLocation`, `IrLocation` | [Semantics/NodePath.cs](src/ProtoCross.Core/Semantics/NodePath.cs) |
| Down through a tree | `SyntaxWalk`, `IrWalk` | [Semantics/SyntaxWalk.cs](src/ProtoCross.Core/Semantics/SyntaxWalk.cs) |
| Where a declaration is | `DeclarationSite` | [Symbols/DeclarationSite.cs](src/ProtoCross.Core/Symbols/DeclarationSite.cs) |
| Where a `.proto` declared it, and what it said | `SchemaDeclaration`, `SchemaSite`, `SchemaComments` | [Symbols/SchemaDeclaration.cs](src/ProtoCross.Core/Symbols/SchemaDeclaration.cs) |
| Everything a schema declares, once | `SchemaSymbols` | [Binding/SchemaSymbols.cs](src/ProtoCross.Core/Binding/SchemaSymbols.cs) |
| Which fields a name reaches on a message, never an extension | `MessageFields` | [Binding/MessageFields.cs](src/ProtoCross.Core/Binding/MessageFields.cs) |
| Which symbol a reference means | `SymbolId` | [Symbols/SymbolId.cs](src/ProtoCross.Core/Symbols/SymbolId.cs) |
| Where a symbol is used | `SymbolReference`, `ReferenceKind` | [Symbols/SymbolReference.cs](src/ProtoCross.Core/Symbols/SymbolReference.cs) |
| What a name is in scope over | `ScopeEntry` | [Symbols/ScopeEntry.cs](src/ProtoCross.Core/Symbols/ScopeEntry.cs) |
| What a bare name may mean here | `ScopeAtPosition`, `VisibleName` | [Semantics/ScopeAtPosition.cs](src/ProtoCross.Core/Semantics/ScopeAtPosition.cs) |
| What kind of symbol it is | `SymbolKind` | [Symbols/SymbolKind.cs](src/ProtoCross.Core/Symbols/SymbolKind.cs) |
| Emission behavior | `ArithmeticBehavior`, `ConversionBehavior` | [Ir/ArithmeticBehavior.cs](src/ProtoCross.Core/Ir/ArithmeticBehavior.cs) |
| Policy → behavior | `NumericPolicy` | [Ir/NumericPolicy.cs](src/ProtoCross.Core/Ir/NumericPolicy.cs) |
| Backend contract | `IBackend`, `ITestBackend`, `ITestProjectScaffold` | [Backend/IBackend.cs](src/ProtoCross.Core/Backend/IBackend.cs) |
| Identifier mapping | `NameConventions` | [Backend/NameConventions.cs](src/ProtoCross.Core/Backend/NameConventions.cs) |

### Diagnostics

`Diagnostic` is `(Code, Severity, Title, Message, Span, Help?)` — very nearly the LSP diagnostic
shape already, `Help` included. Codes are `PC####`, and a raise site names a `DiagnosticDescriptor`
rather than spelling one: the code, the severity and the title belong to the rule and live in
[DiagnosticCodes](src/ProtoCross.Core/Diagnostics/DiagnosticCodes.cs), or in `HostDiagnosticCodes`
for the editor host's own `PC21##` range. The message and the help belong to the occurrence and stay
at the site. Rendering is
`CODE: title` / `file:line:column` / message / optional `help:` line, per spec 26. **That rendering
is published output**; a change to it moves what users see.

Spans are half-open, carry an absolute offset and line/column at both ends, and are 1-based on
line/column, 0-based on offset. `SourceSpan.None` is line 0 — out of band — and `IsNone` is the
question to ask before mapping a span to an editor range.

### Configuration

`protocross.config.xml` (spec 10.4) states the language-dependent policies: overflow, conversion,
divide-by-zero, unset-message reads. Discovery walks up from the source directory, the way
`.editorconfig` does. A command-line flag that contradicts an explicit setting is **refused** unless
`--override-config` is passed — generated code has to mean the same thing however it was built.

An editor adds an axis the command line never had: one process, many documents, one or more
workspace folders, each able to state settings of its own. Spec 10.4.1 settles that in the server
and `WorkspaceConfiguration.Resolve` is the only place it is applied. Configuration is resolved
**per document**, in the order folder → workspace → user setting → `PROTOCROSS_PROTOC` → discovery.
Language policy stays out of settings entirely — a host may name a different `protocross.config.xml`
and may not restate what is in one — and **every setting that is not being used is reported**
(`PC2101`–`PC2105`), because a user who cannot tell a typo from a refusal has nothing to go on. A
`protocross.config.xml` that is found and cannot be read stops the document and is named as *refused*
(`PC2106`), rather than being reported as having supplied the defaults it did not supply.
`DocumentUri` and `PathIdentity` are between them the only places a URI becomes a path and two paths
are compared, which is what makes one file one document and one cache entry however it is spelled.

**Trust** (spec 10.4.1) is the one thing that removes a setting from that order rather than ranking it.
The client reports whether the user trusts the workspace — `workspaceTrusted` at `initialize`,
`protocross/didChangeWorkspaceTrust` afterwards, silence meaning trusted — and while it is untrusted,
every setting that could make the machine run a program is withheld from folder and workspace scope
before the walk begins. Today that is `protocross.protocPath` alone. Which settings require trust is not
a list beside the settings but part of declaring one:
[`ProtoCrossSettings.Definitions`](src/ProtoCross.LanguageServer/Workspace/ProtoCrossSettings.cs) is the
only list of settings there is, and a row cannot be written without its classification. Trust is
applied in one place, as `WorkspaceConfiguration` admits each scope, so compilation, import completion
and the status report cannot disagree about what was withheld. Two consequences are easy to
miss. `workspace/configuration` answers with scopes already merged, so a user-scope protoc path is
withheld too, and protoc is located instead. And nothing withheld is discarded, so granting trust is a
new generation and a recompile like any other change. What was withheld is said once per process as a
message, and listed in the status report — never as a diagnostic, since nothing about the document or
the setting needs editing.

### Projects

A `.pcproj` (spec 5.4) says which sources one compilation is made of and which of them hold its
tests, and names the `protocross.config.xml` its policy comes from: two files, because what is
compiled and what it means are two questions.
[`ProtoCrossProject.Load`](src/ProtoCross.Projects/ProtoCrossProject.cs) reads the project file
alone, through the same [`XmlInput`](src/ProtoCross.Core/Config/XmlInput.cs) the configuration
file is read with, so a position in either is placed the same way.
[`ProjectSources.Expand`](src/ProtoCross.Projects/ProjectSources.cs) is the separate step that walks
directories. A project that states anything it cannot mean is refused whole (`PC2007`–`PC2009`), and
an element whose patterns match nothing is a warning (`PC2010`). Nothing compiles a project yet.

### Serving an editor

[`LanguageServerHost`](src/ProtoCross.LanguageServer/Hosting/LanguageServerHost.cs) is the whole
server: `protocross-server`, LSP over stdin and stdout, driven by VS Code and Visual Studio alike.
There is **no LSP framework**. Everything below
[`JsonRpcConnection`](src/ProtoCross.LanguageServer/Protocol/JsonRpcConnection.cs) is transport —
`Content-Length` framing, correlation, a writer gate — and nothing above it knows how a message is
framed, so the decision is one file wide.

Two rules in the connection are load-bearing and easy to undo. **Reading and handling are separate**:
the read loop parses, completes responses and honours `$/cancelRequest`, and everything else goes to
a queue one worker drains in order. That separation is what lets a handler ask the client a question
— `workspace/configuration` is a request the *server* sends — without waiting for itself. And when
the connection ends, outstanding work is cancelled **before** the dispatcher is awaited; the other
order waits forever for a handler whose answer is never coming.

Draining in order is the default and is right for a handler that is arithmetic over a buffer the
server already holds. A handler that **leaves the process** — one that opens a directory, or waits on
a tool — opts out with `OnRequest(..., concurrent: true)`, because answered in order it holds the
reading worker for as long as the outside world takes, and behind it sit every `didChange`, every
`didClose`, and the `$/cancelRequest` that would have shortened it. Completion was the first such
handler and is where the rules were worked out; hover, go-to-definition, classification,
find-references, occurrence highlighting and signature help have since joined it. What one owes in
return is four things, all of them easy to get wrong:

- **Settle which buffer the request is about before yielding.** `CompletionProvider.Read` runs on the
  ordered worker; only the walk is deferred. Deferring the lookup lets the `didChange` behind the
  request be applied first, and the position is then measured against text the client had not sent —
  after which every staleness check agrees, because they are all asking about the wrong document.
- **Identify the buffer and the configuration as objects, not as a version and a generation.** Both
  are immutable, so holding them holds the question. A version number is unique only within one open
  session: close a document and reopen it and the client starts again at one.
- **Bound its own outstanding work.** A newer completion supersedes the outstanding one for its
  document, exactly as a keystroke supersedes a scheduled compile, and a semaphore bounds how many
  run across documents. A per-walk budget bounds one walk and says nothing about how many there are.
- **Give up the ones nobody is waiting for, before they take a slot.** `didClose` calls
  `CompletionProvider.Forget` beside `CompileScheduler.ForgetAsync`, and a request that reaches the
  front of the queue re-checks freshness before it walks rather than only after — an edit is the one
  reason for abandonment that carries no cancellation to notice. Waiting for a slot is part of the
  request: it sits inside the same cleanup as the walk, so a request that ends while waiting is still
  retired, and gives back only a slot it actually took.

**`concurrent: true` is necessary and is not sufficient.** The dispatcher hands such a handler's task
to `Detach` instead of awaiting it — but it has to *call* the handler to get that task, so a handler
with no `await` in it runs to completion first and the flag changes nothing at all. Whatever blocks
has to be on the far side of a yield. Every provider gets this for free by awaiting the answer gate;
the status report is the one handler that had to be given a yield deliberately, and says so.

Every request an editor sends arrives on the same terms and splits three ways. The outline lexes and
parses and stops, so it alone still answers on the ordered worker, never waits on protoc, and
survives a file that does not parse — which is the point of it, since an outline that vanishes while
you type is worse than a stale one. The status report is about the server rather than about a buffer,
and is described below. Everything else can only be answered by the binder, so each
compiles through `DocumentSemantics` and each is concurrent, and everything the architecture above
demands of a concurrent handler is stated once in
[`DeferredAnswers`](src/ProtoCross.LanguageServer/Hosting/DeferredAnswers.cs) rather than once per
handler: supersession per document, a bounded gate, abandoning work nobody waits for, and the
staleness refusal. One instance per request kind, because a passing mouse must not cancel a
deliberate click — and a caret sliding through a file must not cancel the reference list somebody
asked for.

Classification is the one that moved. #42 answered it from the lexer in the instant it was read and
#50 gave every identifier the category of the symbol the binder resolved it to, which means the
binder and therefore the same treatment as hover. It transcribes rather than asks: the range and the
identity of every name were already recorded as the binder resolved them, so the colour is that
record read back and cannot disagree with completion, with navigation, or with the code that gets
generated. What it may never cost is colour — a name that resolved to nothing, a file that did not
parse and a schema that would not load all keep the lexical answer — and a client that asks for
differences rather than whole answers is sent the integers that changed, one retained answer per open
document, paired with the name it was published under so an answer that was never delivered cannot be
diffed against.

Find-references and occurrence highlighting are one question rendered twice, and they share the
lookup rather than each doing it: `SymbolOccurrences` turns a caret into a symbol and every place that
symbol is written, one sends locations and the other sends ranges to tint. Being semantic rather than
textual falls out of that rather than being implemented — identity is a `SymbolId` and never a
spelling, so two locals of one name in sibling blocks answer separately and a name inside a string
answers not at all. The reference index is consulted directly and nothing is cached on top of it, which
#57 measured and confirmed: 1.2 ms at p95 on a file ten times normal size, against a 20 ms budget.
A cache here would buy latency nobody can perceive.

Signature help is the one that cannot ask the tree. A call being typed has no closing parenthesis, and
the tree records no comma positions, no span for the argument list and no flag saying the parenthesis
was closed — so `CallSubject` finds which call and which argument in the token stream, the way import
path completion does. Which method it names still comes from the binder: the reference to the method
name is recorded before the arity check that a half-written call nearly always fails, so the identity
survives even though the node carrying the signature does not.

What they answer is joined in one place. `DeclaredSymbol` turns a caret into a symbol and then asks
whichever compiler owns the declaration — `SemanticModel.DeclarationOf` for a local, a parameter, a
loop binding or a method, `DescriptorBundle.DeclarationOf` for a field, an enum constant, a message or
an enum — so no surface has to know which side a symbol falls on, and
`SymbolLocations` is the one place a span becomes a document the client recognizes. Hover adds the type,
the `.proto` comment, and the one thing the source text cannot show: the arithmetic policy in force,
read off the behavior the binder stamped on the node rather than off the configuration, and stated
only where a node carries one (spec 10.4). Capabilities are honoured throughout: links carry a
declaration's two ranges only to a client that declared `linkSupport`, and an outline nests only for
one that declared it can show a tree.

The buffer the client sent is the source of truth and the file on disk is never read for an open
document. Edits are applied incrementally, in order, each against the text the one before it
produced. A compile is debounced and coalesced, carries the document version and the configuration
generation it began under, and its result is **discarded rather than published** if either has moved
— the most visible failure a server can have is an old compile putting a fixed error back on screen.
The rule is not only about diagnostics: every answer describes the version it read, and a request
that could only answer about a superseded one refuses instead.

Cancellation reaches the one step that can outlast a keystroke. A superseded or closed document's
compile stops waiting on `protoc` and gives its worker back at once; everything after the load is
milliseconds and simply finishes into the discard. The queue holds one entry per document and a new
request supersedes the last, so it cannot outgrow the number of open documents, and `Pending`,
`InFlight` and `PeakInFlight` publish the backlog, what is running and the high-water mark. The
interval and the concurrency limit were #57's to pin and both stand: a whole-buffer compile is 33 ms
at p95, so the quarter-second debounce is almost all of the delay a reader feels, and four concurrent
cold loads cost roughly two pool threads each — measured on sixteen processors, which is the best
case rather than the typical one.

`protocross/status` is the server answering for itself, and is this server's own method rather than
one of LSP's. [`StatusReporter`](src/ProtoCross.LanguageServer/Hosting/StatusReporter.cs) assembles
which `protoc` was chosen **and by which probe**, what it says its version is, every setting resolved
for the active document **with the layer that supplied it**, what the descriptor cache has done and
how many bytes it is holding, the last error with a timestamp, and what recent requests have cost
against the #57 budgets. The report is data first and one Markdown rendering second, so a client may
draw a panel instead, and the note about file paths is inside the report rather than in a client's
chrome — a warning only one of the two editors remembered to draw is a warning half the users never
see.

Three things about it are deliberate. It is **answered in every lifecycle state**, including before
`initialize` and after `shutdown`, where every other request is refused: a server that never finished
starting is exactly the one somebody runs this against, and a diagnostic that requires a healthy
system diagnoses nothing. It **never throws and never compiles** — a missing executable, a refused
policy file and a document that will not compile each have to produce a report *about* that rather
than an empty one. And it is **concurrent with a real yield**, because it settles a configuration off
disk, stats the directories beside `protoc`, and starts `protoc` to ask its version; answered in
order, a wedged executable would freeze the editor the command exists to diagnose.

The latency budgets live here rather than beside the benchmark that first wrote them, in
[`PerformanceBudgets`](src/ProtoCross.LanguageServer/Hosting/PerformanceBudgets.cs), with the
nearest-rank percentile beside them in `RequestTimings`. Two readers want both now — the benchmark,
and this report — and a status figure whose p95 meant something subtly different from the documented
one would be a number somebody compares and is misled by. Timings are always on: one stopwatch and
one array write per answer, in a fifty-deep ring, and **only answers are recorded**, since a request
refused for staleness produced no answer and folding those in would make a server look faster the
more work it was abandoning.

A compilation rests on two kinds of file the editor does not hold, the schemas it imports and the
policy file it discovers, and a kept compilation already refuses to answer once either has moved. What
nothing did was *ask*: diagnostics are published when a compile runs, and saving a `.proto` in another
tab is not a keystroke in this one. So once initialized the server asks a client that can watch files
to report `**/*.proto` and `**/protocross.config.xml`
([`WatchedFiles`](src/ProtoCross.LanguageServer/Hosting/WatchedFiles.cs)), and a change to either
reschedules every open document. So does the client agreeing to watch, since a save before its watcher
was running was reported to nobody. Each compile asks `DocumentSemantics` first, so a document whose
schemas still stand costs a hash per schema rather than a compile. That includes a schema beside a
source that another directory shadows: protoc never reads it, but `PC0087` was decided from it. The server registers the patterns rather than
an extension choosing them, so a second editor gets the behaviour by speaking the protocol.

When discovery finds no protoc, the editor is not given the command line's sentence, which suggests
restoring a NuGet package. [`MissingProtoc`](src/ProtoCross.LanguageServer/Hosting/MissingProtoc.cs)
says where the server looked, where protoc is published and which setting names one, and the same
sentence goes on the import line, in the one-time message and in the status report. The client reports
the relative `PATH` entries it removed before starting the server, and they are named only when there
were some. A schema failure with no loader behind it is how a missing protoc is recognized, read off the
compilation rather than probed again.

A client's trace value of `off` returns the log to the level the process was started with
(`ServerLog.StartingLevel`), rather than lowering it to errors. VS Code's client says `off` in every
`initialize` and again after every settings change, so reading it as a request for less logging made
`--log-level` impossible to keep turned up.

Diagnostics are published *per file* and produced *per compilation*, and the two stop lining up as
soon as a `.proto` can be blamed, so
[`DiagnosticRouter`](src/ProtoCross.LanguageServer/Hosting/DiagnosticRouter.cs) publishes the union of
what every open document says about a file. Two buffers importing one broken schema both report it,
identical reports collapse, and closing one does not withdraw the other's. Spec 26.1 has the rest:
severities mapped rather than invented, help text kept as its own thing, a locationless diagnostic
published at the start of its document, and a `protoc` failure landing both in the schema it names
and on the import that reached it.

Classification (spec 6.5) is two layers over one fixed legend. The first lexes and nothing more, so it
answers for a file that does not parse; the second gives each identifier the category of the symbol
the binder resolved it to, and wherever it resolved nothing — a half-typed name, a file that did not
parse, a schema that would not load — the first layer's answer stands. The legend is the whole
standard token set, declared once because it is negotiated once and indexed by position, which is
what let the second layer ship by emitting different numbers rather than by changing what the numbers
mean. What the second layer costs, and why it never costs colour, is in *Serving an editor*.

Completion is the same bargain and one step further out. `CompletionProvider` decides which context
the caret is in before it asks what belongs there, and today recognizes one — inside an `import
proto` string, found by `ImportPathContext` in the token stream, because the tree does not carry the
path's own span and the state this is invoked in is one the parser has already recovered from. What
is offered comes from
[`SchemaCatalog`](src/ProtoCross.Core/Binding/SchemaCatalog.cs), which is also where "the roots an
import is resolved against" now lives for everyone who asks: the include paths, then the source's own
directory, then whatever the loader adds. One directory listing per root, on demand, no index and no
cache — so progressive completion falls out of the shape rather than being built, and a schema that
appeared on disk a second ago is offered. Only the include roots are resolved for it, never the
language policy: settling that means searching upward for a `protocross.config.xml` and parsing it,
which decides nothing about where a path resolves and would be paid per keystroke. The listing is
lazy, reads each entry's kind from the same directory scan that found it, and carries a **budget in
entries examined**, because one level bounds depth and not breadth, and a root pointed at a vendored
tree or a network mount is one somebody will point at one. A walk that stops on its budget says so:
completion offers what it saw, since the list is already declared incomplete, and the near match
offers nothing, since the nearest of a partial reading is not the nearest. That figure is one of the
few #57 did **not** measure, and `docs/performance.md` says so: a walk bounded against a vendored
tree or a network mount is not something this repository's own directories can say anything about. The same catalog names that near match on `PC0002`, so the terminal and the editor say the
same thing about a path that resolved to nothing. Nothing here compiles, and this is the first
request that can go stale between reading the buffer and answering, so it re-checks the version and
refuses with `ContentModified` rather than inserting text at an offset that has stopped meaning what
it meant.

### The VS Code extension

[editors/vscode](editors/vscode) is a thin client over the server, and it deliberately brings no
toolchain. The server ships inside it framework-dependent and without an app host, so one package
serves every platform and runs on the user's own .NET 10 or newer. `protoc` is whichever one the server
already finds. A user who does not want .NET at all keeps the grammar, the brackets and the comments, and
is told once. Its README is the user-facing account; what follows is the shape.

- **[`launch.ts`](editors/vscode/src/launch.ts) decides how the server starts, with no VS Code in it**,
  so every rule is a Node test. The rules exist so that nothing the server looks up resolves into the
  workspace. The server is started by absolute path, through a `dotnet` also found by absolute path. It
  runs in the extension's global storage directory, with relative and empty `PATH` entries removed and
  `PROTOCROSS_PROTOC` and `NUGET_PACKAGES` passed only when absolute. Spec 10.4.1 states it as the
  client's obligation.
- **[`serverController.ts`](editors/vscode/src/serverController.ts) owns the process**: launches
  serialized through one queue, crash restarts bounded at four in three minutes, a counted restart for
  the status report, and every reason not to start turned into a named failure with one offer of what to
  do about it. The client's own view of itself is what the state follows, so a restart that fails to
  start says so rather than leaving a report that says "starting" until the window closes. It reports
  trust at `initialize`, when trust is granted, and again whenever the client reaches running while the
  workspace is trusted -- a grant during a start lands between the other two.
- **The extension's own settings never reach the server.** `logLevel`, `server.*` and `dotnetPath`
  live under `protocross` beside the settings the server reads, and the server reports anything in that
  section it does not understand, which is how a typo gets noticed. A `workspace/configuration`
  middleware takes the extension's keys out of each answer. The list is in
  [`contract.json`](editors/vscode/src/contract.json), with the privacy note a report the extension
  writes itself must share with the server's.
- **[`status.ts`](editors/vscode/src/status.ts) asks the server for its report and writes one when it
  cannot**, whether the server failed to start or did not answer in time. The report is copied only
  after the privacy note has been shown.
- **The grammar colours every token the server's lexical layer classifies with the TextMate scope VS
  Code maps that classification to.** When semantic tokens arrive, the only change a reader sees is the
  server's refinement.

What the two sides must agree on is checked from the server's suite in `VsCodeExtensionTests`, since
nothing at build time connects a C# constant to a JSON file: the manifest declares every server setting
and restricts exactly what the server withholds, the contract lists every other setting, the privacy
note matches, and the grammar is swept against `SemanticTokenEncoder` over every ProtoCross source in the
repository.

### Backends

Per spec 23 a backend consumes only the typed IR, never the AST, and rejects what it cannot support
rather than emitting something that quietly differs. A backend **cannot branch on policy**: how an
operation is emitted comes from the behavior annotation the binder stamped on the IR node. Policy
reaches a backend only as prose for the generated file's header.

A backend is handed one source's part of the module, and a call in it may name a method another
source declares. C# reaches it by the receiver's `partial` extension class, whichever file declares
the part. A C++ header includes the headers of the sources it calls, after its own declarations and
before its definitions, so two sources that call each other compile whichever header comes first;
one that calls none is laid out as it always was.

## Tests

One project, [tests/ProtoCross.Tests](tests/ProtoCross.Tests), roughly organized by layer:
`LexerTests`, `ParserTests`, `ParserResilienceTests` and `BinderResilienceTests` (fuzz),
`SourceSpanTests`, `CompilationTests`, `InMemoryCompilationTests`, `PartialBindingTests`,
`SymbolIdentityTests`, `PositionQueryTests`, `ReferenceIndexTests`, `ScopeQueryTests`,
`DescriptorCacheTests`, `SchemaDeclarationTests`, `ProcessSupervisionTests`, `CompileSupervisionTests`,
`WorkspaceConfigurationTests`, `WorkspaceTrustTests`, `ServerStatusTests`, `LanguageServerTests`,
`WatchedFileTests`, `MissingProtocTests`, `LogLevelTests`, `VsCodeExtensionTests`, `SemanticTokenTests`,
`SemanticRefinementTests`, `SchemaCatalogTests`,
`ImportCompletionTests`, `SchemaCompletionTests`, `HoverTests`, `DefinitionTests`,
`DocumentSymbolTests`, `ReferenceTests`, `SignatureHelpTests`,
`TreeWalkTests`, `IrContractTests`, `ImportResolutionTests`, `ProjectConfigTests`, `ProjectFileTests`,
`ProjectSourcesTests`, `XmlInputTests`, `TestSourceTests`, `GeneratedNameTests`, `BackendTests`, `NameMappingTests`,
`CliTests` (which runs the built `protocross` as a process), and the scaffolding and smoke suites.

- **Conformance corpus** — [tests/conformance/vectors](tests/conformance/vectors) holds `.pcross`
  files whose `test` blocks *are* the vectors, compiled and executed in both backends. This is the
  semantic gate: spec 25.2 left the vector format open and this repository answers it with the
  language's own `test` declaration, so a vector with a wrong-typed expectation is a compile error.
  A directory under `multi/` is one vector written across several files and compiled as one
  program, which is where what happens between sources is pinned.
- **Harness** — [tests/ProtoCross.Tests/Harness](tests/ProtoCross.Tests/Harness) builds and runs real
  generated projects. Needs `protoc`, the .NET SDK, and a C++ toolchain.
- **Paths** — [TestPaths.cs](tests/ProtoCross.Tests/TestPaths.cs) finds the repository root and the
  fixture protos; use it rather than hand-rolling paths.
- **The server is driven over the wire** — [LanguageServerClient.cs](tests/ProtoCross.Tests/LanguageServerClient.cs)
  speaks framed JSON-RPC at a real host over a pair of in-memory streams, so the framing, the
  lifecycle gate and the dispatch order are under test rather than bypassed.

- **The extension** has three suites of its own, run from `editors/vscode`. `npm run test:unit` covers
  the launch rules and the settings filter. `npm run test:server` starts the staged server under those
  rules, pairing each defence with the same launch made without it. `npm run test:e2e` runs VS Code with
  the extension loaded and a fixture workspace open. The VS Code release is pinned in
  `test/e2e/vscode-version.json`, so a release that breaks the extension turns the suite red in the
  commit that moves that number rather than on the day it ships.

`dotnet test` locally is the gate. [.github/workflows/ci.yml](.github/workflows/ci.yml) runs the same
suite, with both gated switches thrown, on every pull request to `main`. It also runs the extension's
three suites on Windows, Linux and macOS, before and after a `protoc` is installed.

## Invariants that constrain a change

1. **Generated output is byte-for-byte stable.** Any change that could move it gets diffed against
   the base commit, not asserted about.
2. **Rendered diagnostics are published output.** Format, codes, and positions are user-visible.
3. **Core stays free of CLI and editor coupling**, and dependencies keep running one way.
4. **The compiler does not throw on user input.** Bad source, bad config, and bad include paths are
   diagnostics. A long-lived host must survive all of them, through binding as well as parsing.
   Neither stage may throw, hang, or recurse without bound on any input at all.
5. **Backends see the IR only**, and cannot branch on policy.
6. **A compilation is a set of sources.** Anything keyed by an offset is keyed by a document as well,
   because an offset names a place only within one source: `SemanticModel.For(result, document)`
   answers position questions for one source, and `IrModule.DeclaredIn` is one source's part of the
   module.
7. **The IR keeps the contract in spec 22.2**, invariants included: a node lies inside the node
   holding it, an expression's type is an error type only after an error, a reference resolves or is
   no reference, and one walk reaches every construct. `IrContractTests` sweeps the corpus for each,
   because the backends and the editor are written against them and neither checks.

## Where the editor-support epic lands

Epic [#47](https://github.com/IsaacSherman/ProtoCross/issues/47) makes the semantic model
*addressable* ("what is at line 12, column 7?") and *durable* (the binder discarded everything it
knew about a declaration as it went), then builds a language server on top. Both properties are in:
#35, #36, #37 and #39 were the four sub-issues expected to touch existing compiler code, and three
since have had to as well. #38 gave the IR an `IrNode` base so a path through it is expressible, and
stopped `BindInvocation` discarding the arguments of a call it could not resolve. #40 added a
recording line at each of the fifteen points the binder resolves a name, and turned
`IrAssignment.Target` into an `IrLocalReference` — the first change in this epic to reach a backend
file, and the reason `EmitStatement` now asks the expression emitter for the target it used to spell
itself. #49 gave `Scope` an extent and a recording line on each of the three branches where a
declaration is accepted, so that what the binder knew about visibility outlives the descent that
knew it. #48 made the descriptor load cacheable and stopped it discarding the descriptor set, which
reached `Compilation` twice: it now holds the loader it resolved rather than locating `protoc` again
per keystroke, and it publishes the bundle on the result. #53 opened the server project and settled
the configuration model in it before #42, #45 and #46 could each invent part of one; it reached Core
only to give "are these two paths the same path?" a single home, which is what collapses the
duplicate cache entries #48 left behind. #42 built the server itself — lifecycle, document sync,
diagnostics and lexical semantic tokens over a base protocol this repository owns — and reached Core
only to have the lexer keep the comment spans it was already walking past. #41 closed the second
wave by making the retained source info answerable, so a schema element's declaration and its doc
comment are reachable from a descriptor. #54 made abandoned work stop costing anything: a
cancellable wait on `protoc`, an expiry that says it is one, a stated queue bound, and counters that
turn "no leak over a working day" into a soak test. #56 opened the completion surface on the first
line anybody writes, and reached Core to give the include roots one home rather than the two
expressions that had been agreeing by coincidence. #43 answered what a dot can reach and what a bare
name may mean, from a compilation kept between keystrokes. #44 built the first navigation milestone —
hover, the document outline, go-to-definition — and reached Core twice, both times to open a door that
was missing rather than to reshape one: a schema declaration is now reachable by identity and not only
by descriptor, which is what a caret on a type name produces, and the walk that finds it is the walk
the source index already performed. #50 finished the semantic token work #42 left half done, and
reached Core once, additively: the reference index already held every name a file resolved, in order,
and had no way to hand over the whole sequence at once. #51 answered the other direction — who uses
this, what else is this name, what does this call expect next — and reached Core only for renderings
and lookups over what was already there: the method behind an identity, and where each parameter sits
inside the signature line a hover already shows. #57 gave interactive latency written budgets and
measured them, finding one to two orders of magnitude of headroom on four of the five, and reached
Core only through remarks. #58 made the server able to answer for itself, and reached Core three
times, each additively and each because a fact the server knew could not be asked for: the locator
walks its probes and now says which one answered, a loader can report what its `protoc` says it is,
and the descriptor cache states the bytes it holds and the last entry to go stale. #55 opened the
fourth wave, the one that ships to people, by deciding what a repository nobody has trusted may make
the server do: withhold the one setting that names an executable, keep serving everything else, and
say so once. It did not reach Core, and it made the settings a table so that classifying a new one
is part of adding it. #45 put the server in front of people as a VS Code extension that brings no
toolchain of its own. It did not reach Core. It gave the server three things a real client turned out to
need: watching the files a compilation rests on, an editor's account of a missing protoc, and a trace
`off` that no longer silences the log. Everything from
here should be additive: new types, new projects. Rewriting the binder is the signal to stop and
re-scope.
