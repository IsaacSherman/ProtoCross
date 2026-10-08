# Editor
[Feature index](Feature-Index.md) · [Architecture](ARCHITECTURE.md) · [Compilation and projects](Compilation-and-Projects.md) · [Language](Language.md) · [IR](IR.md)

| Topic | Source entry points | Relevant tests |
|---|---|---|
| LSP dispatch/sync | [JsonRpcConnection][rpc]: `RunAsync`, `DispatchAsync`; [Host][host]: `Register`, `Initialize`, `DidChange` | [Language server][srv-t] |
| Compilation reuse and project sources | [DocumentSemantics][sem]: `For`, `SourcesFor` | [Document semantics][sem-t] |
| Debounce, cancel, publish | [CompileScheduler][sched]: `Schedule`, `CompileAsync` | [Compile supervision][sup-t] |
| Deferred answers and staleness | [DeferredAnswers][defer]: `AnswerAsync`, `Require` | [Compile supervision][sup-t] |
| Completion: context capture and imports | [CompletionProvider][comp]: `Read`, `Schemas` | [Import completion][imp-t] |
| Completion: scope, dotted members, presence | [CompletionProvider][comp]: `Symbols`, `InScope`, `Members` | [Schema completion][comp-t] |
| Completion: types, extends, test targets | [CompletionProvider][comp]: `TypesAt`, `ExtendedAt`, `Targeted` | [Type edits][type-t] |
| Completion: arguments, literals, map entries | [CompletionProvider][comp]: `Arguments`, `LiteralFields`, `EntryFields` | [Calls][comp-t], [Literals][lit-t], [Map entries][entry-t] |
| Completion: switch keywords | [CompletionProvider][comp]: `Keywords`, `ArmsThatCanBeginAt` | [Switch completion][sw-t] |
| Hover and go-to-definition | [HoverProvider][hover], [DefinitionProvider][def]: `AnswerAsync` | [Hover][hover-t], [Definition][def-t] |
| References, highlights, signature help | [Host][host]: `FindReferences`, `AtPosition` | [References][refs-t], [Signatures][sig-t] |
| Document outline | [Host][host]: `Outline` | [Document symbols][out-t] |
| Semantic tokens and deltas | [ClassificationProvider][classify]: `AnswerAsync`, `Publish`; [SemanticTokenEncoder][encode]: `Encode` | [Semantic tokens][tok-t] |
| Config, membership, trust | [WorkspaceConfiguration][config]: `Resolve`, `WithTrust`, `WithheldSettings`; [Host][host]: `ConfigurationChanged`, `TrustChanged` | [Configuration][config-t], [Trust][trust-t] |
| File watches | [Host][host]: `WatchFiles`, `WatchedFilesChanged`; [WatchedFiles][watch]: `MoveAnyCompilation`, `SourcesIn` | [Watched files][watch-t] |
| VS Code launch and trust | [extension.ts][ext]: `activate`; [ServerController][control]: `start`, `restart`; [launch.ts][launch]: `sanitizeEnvironment` | [Extension contract][ext-t], [Launch unit tests][launch-t] |
| Performance and status | [Host][host]: `Timed`, `Status`; [performance notes][perf] | [Performance costs][cost-t] |

[rpc]: ../src/ProtoCross.LanguageServer/Protocol/JsonRpcConnection.cs
[host]: ../src/ProtoCross.LanguageServer/Hosting/LanguageServerHost.cs
[sem]: ../src/ProtoCross.LanguageServer/Hosting/DocumentSemantics.cs
[sched]: ../src/ProtoCross.LanguageServer/Hosting/CompileScheduler.cs
[defer]: ../src/ProtoCross.LanguageServer/Hosting/DeferredAnswers.cs
[comp]: ../src/ProtoCross.LanguageServer/Hosting/CompletionProvider.cs
[hover]: ../src/ProtoCross.LanguageServer/Hosting/HoverProvider.cs
[def]: ../src/ProtoCross.LanguageServer/Hosting/DefinitionProvider.cs
[classify]: ../src/ProtoCross.LanguageServer/Hosting/ClassificationProvider.cs
[encode]: ../src/ProtoCross.LanguageServer/Hosting/SemanticTokenEncoder.cs
[config]: ../src/ProtoCross.LanguageServer/Workspace/WorkspaceConfiguration.cs
[watch]: ../src/ProtoCross.LanguageServer/Hosting/WatchedFiles.cs
[ext]: ../editors/vscode/src/extension.ts
[control]: ../editors/vscode/src/serverController.ts
[launch]: ../editors/vscode/src/launch.ts
[perf]: ../docs/performance.md
[srv-t]: ../tests/ProtoCross.Tests/LanguageServerTests.cs
[sem-t]: ../tests/ProtoCross.Tests/DocumentSemanticsTests.cs
[sup-t]: ../tests/ProtoCross.Tests/CompileSupervisionTests.cs
[imp-t]: ../tests/ProtoCross.Tests/ImportCompletionTests.cs
[comp-t]: ../tests/ProtoCross.Tests/SchemaCompletionTests.cs
[type-t]: ../tests/ProtoCross.Tests/SchemaCompletionTests.TypeEditReview.cs
[lit-t]: ../tests/ProtoCross.Tests/SchemaCompletionTests.Literals.cs
[entry-t]: ../tests/ProtoCross.Tests/MapEntryCompletionReviewTests.cs
[sw-t]: ../tests/ProtoCross.Tests/SchemaCompletionTests.Switch.cs
[hover-t]: ../tests/ProtoCross.Tests/HoverTests.cs
[def-t]: ../tests/ProtoCross.Tests/DefinitionTests.cs
[refs-t]: ../tests/ProtoCross.Tests/ReferenceTests.cs
[sig-t]: ../tests/ProtoCross.Tests/SignatureHelpTests.cs
[out-t]: ../tests/ProtoCross.Tests/DocumentSymbolTests.cs
[tok-t]: ../tests/ProtoCross.Tests/SemanticTokenTests.cs
[config-t]: ../tests/ProtoCross.Tests/WorkspaceConfigurationTests.cs
[trust-t]: ../tests/ProtoCross.Tests/WorkspaceTrustTests.cs
[watch-t]: ../tests/ProtoCross.Tests/WatchedFileTests.cs
[ext-t]: ../tests/ProtoCross.Tests/VsCodeExtensionTests.cs
[launch-t]: ../editors/vscode/test/unit/launch.test.ts
[cost-t]: ../tests/ProtoCross.Tests/Performance/PerformanceCostTests.cs
