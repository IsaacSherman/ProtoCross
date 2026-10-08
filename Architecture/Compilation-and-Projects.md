# Compilation and projects
[Feature index](Feature-Index.md) · [Architecture](ARCHITECTURE.md) · [Language](Language.md) · [IR](IR.md) · [Editor](Editor.md)

| Topic | Source entry points | Relevant tests |
|---|---|---|
| Pipeline and partial results | [Compilation][comp]: `Compilation.Compile`, `CompilationResult.Module` / `EmittableModule` | [CompilationTests][comp-t] |
| Files and unsaved buffers | [SourceDocument][source]: `SourceIdentity`, `SourceDocument.ReadFrom` | [Buffers][memory-t] |
| Shared numeric/enum policy | [Compilation][comp]: `ResolveSharedConfig`; [ProjectConfig][config]: `Discover`, `Load` | [Config][config-t] |
| Imports, roots, nearest schema | [Compilation][comp]: `GetSearchPaths`, `Resolve`; [SchemaCatalog][catalog]: `RootsFor`, `NearestTo`; [ImportResolution][imports] | [Imports][import-t] |
| protoc selection and descriptor loads | [ProtocLocator][protoc]: `Select`; [DescriptorLoader][loader]: `LoadBundle` | [Process supervision][process-t] |
| Descriptor reuse and schema declarations | [DescriptorCache][cache]: `GetOrLoad`; [DescriptorBundle][bundle]: `DeclarationOf` | [Descriptor cache][cache-t], [Schema declarations][schema-t] |
| Several sources as one program | [Compilation][comp]: list-of-sources overloads of `Compile`; [SourceTree][tree]: `SourceTree` | [Multiple sources][multi-t] |
| Production/test schema boundary | [ProductionSchemaClosure][closure]: `Of`, `ReportSchemasTheProductionBuildLoadsDifferently`; [Compilation][comp]: `CompilationOptions.SkipTests` | [Production schemas][closure-t], [Source roles][roles-t] |
| Project XML and generated namespace | [ProtoCrossProject][project]: `Load`, `ReadName` | [Project names][names-t] |
| Project discovery and cached expansion | [ProjectDiscovery][discovery]: `Find`; [ProjectCatalog][projects]: `FilesOf` | [Discovery][discovery-t] |
| Source/test globs and membership | [ProjectSources][members]: `ExpandForBuild`, `RoleOf` | [Source patterns][members-t], [Build refusal][refusal-t] |
| Project-wide policy | [ProjectPolicy][policy]: `Resolve` | [CLI projects][cli-t] |
| CLI arguments, source/project builds | [Program][cli]: `CommandLineOptions.Parse`, `CompilationOfSources`, `CompilationOfProject` | [CLI projects][cli-t] |
| Diagnostic codes and CLI rendering | [Diagnostic][diagnostics]: `DiagnosticBag.Report`, `Diagnostic.ToString`; [Program][cli]: `PrintDiagnostics` | [Diagnostic codes][diagnostic-t] |
| Attribute and publish editor diagnostics | [CompilationDiagnostics][routing]: `Build`; [DiagnosticRouter][router]: `PublishAsync`, `ClearAsync` | [Diagnostic routing][routing-t] |

[comp]: ../src/ProtoCross.Core/Compilation.cs
[source]: ../src/ProtoCross.Core/SourceDocument.cs
[config]: ../src/ProtoCross.Core/Config/ProjectConfig.cs
[catalog]: ../src/ProtoCross.Core/Binding/SchemaCatalog.cs
[imports]: ../src/ProtoCross.Core/ImportResolution.cs
[protoc]: ../src/ProtoCross.Core/Binding/ProtocLocator.cs
[loader]: ../src/ProtoCross.Core/Binding/DescriptorLoader.cs
[cache]: ../src/ProtoCross.Core/Binding/DescriptorCache.cs
[bundle]: ../src/ProtoCross.Core/Binding/DescriptorBundle.cs
[tree]: ../src/ProtoCross.Core/SourceTree.cs
[closure]: ../src/ProtoCross.Core/ProductionSchemaClosure.cs
[project]: ../src/ProtoCross.Projects/ProtoCrossProject.cs
[discovery]: ../src/ProtoCross.Projects/ProjectDiscovery.cs
[projects]: ../src/ProtoCross.Projects/ProjectCatalog.cs
[members]: ../src/ProtoCross.Projects/ProjectSources.cs
[policy]: ../src/ProtoCross.Projects/ProjectPolicy.cs
[cli]: ../src/ProtoCross.Cli/Program.cs
[diagnostics]: ../src/ProtoCross.Core/Diagnostics/Diagnostic.cs
[routing]: ../src/ProtoCross.LanguageServer/Hosting/CompilationDiagnostics.cs
[router]: ../src/ProtoCross.LanguageServer/Hosting/DiagnosticRouter.cs
[comp-t]: ../tests/ProtoCross.Tests/CompilationTests.cs
[memory-t]: ../tests/ProtoCross.Tests/InMemoryCompilationTests.cs
[config-t]: ../tests/ProtoCross.Tests/ProjectConfigTests.cs
[import-t]: ../tests/ProtoCross.Tests/ImportResolutionTests.cs
[process-t]: ../tests/ProtoCross.Tests/ProcessSupervisionTests.cs
[cache-t]: ../tests/ProtoCross.Tests/DescriptorCacheTests.cs
[schema-t]: ../tests/ProtoCross.Tests/SchemaDeclarationTests.cs
[multi-t]: ../tests/ProtoCross.Tests/MultiFileCompilationTests.cs
[closure-t]: ../tests/ProtoCross.Tests/ProductionSchemaClosureTests.cs
[roles-t]: ../tests/ProtoCross.Tests/TestSourceTests.cs
[names-t]: ../tests/ProtoCross.Tests/ProjectFileTests.Names.cs
[discovery-t]: ../tests/ProtoCross.Tests/ProjectDiscoveryTests.cs
[members-t]: ../tests/ProtoCross.Tests/ProjectSourcesTests.cs
[refusal-t]: ../tests/ProtoCross.Tests/ProjectBuildRefusalTests.cs
[cli-t]: ../tests/ProtoCross.Tests/CliTests.Projects.cs
[diagnostic-t]: ../tests/ProtoCross.Tests/DiagnosticCodeTests.cs
[routing-t]: ../tests/ProtoCross.Tests/LanguageServerDiagnosticRoutingTests.cs
