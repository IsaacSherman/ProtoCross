# Compilation and projects
[Feature index](Feature-Index.md) · [Architecture](ARCHITECTURE.md) · [Language](Language.md) · [IR](IR.md) · [Editor](Editor.md)

| Topic | Source entry points | Relevant tests |
|---|---|---|
| Pipeline and partial results | [Compilation][comp]: `Compilation.Compile`, `CompilationResult.Module` / `EmittableModule` | [CompilationTests][comp-t] |
| Files and unsaved buffers | [SourceDocument][source]: `SourceIdentity`, `SourceDocument.ReadFrom` | [Buffers][memory-t] |
| Shared numeric/enum policy, and termination | [Compilation][comp]: `ResolveSharedConfig`; [ProjectConfig][config]: `Discover`, `Load`, `ReportWhatEndsTheProgram`, `ReportContradictions`, `TryOverrideOverflow` | [Config][config-t], [Termination][termination-t] |
| Imports, roots, nearest schema | [Compilation][comp]: `GetSearchPaths`, `Resolve`; [SchemaCatalog][catalog]: `RootsFor`, `NearestTo`; [ImportResolution][imports] | [Imports][import-t] |
| protoc selection and descriptor loads | [ProtocLocator][protoc]: `Select`, `DescribeNotFound`; [DescriptorLoader][loader]: `LoadBundle` | [Process supervision][process-t] |
| Build tools for generated code, and why one is missing | [ToolLocator][tools]: `FindDotnet`, `FindCMake`, `FindCppCompiler`, `FindProtobufCpp`; [ToolLookup][lookup]; [ProcessRunner][runner]: `Run` | [Toolchain][tools-t] |
| What generated tests reported, and the verdict on it | [TrxLog][trx]: `ReadLatest`; [TestReports][reports]: `FromTrx`, `FromDriverOutput`; [TestVerdict][verdict]: `Judge` | [Verdict][verdict-t] |
| Descriptor reuse and schema declarations | [DescriptorCache][cache]: `GetOrLoad`; [DescriptorBundle][bundle]: `DeclarationOf` | [Descriptor cache][cache-t], [Schema declarations][schema-t] |
| Several sources as one program | [Compilation][comp]: list-of-sources overloads of `Compile`; [SourceTree][tree]: `SourceTree` | [Multiple sources][multi-t] |
| Production/test schema boundary | [ProductionSchemaClosure][closure]: `Of`, `ReportSchemasTheProductionBuildLoadsDifferently`; [Compilation][comp]: `CompilationOptions.SkipTests` | [Production schemas][closure-t], [Source roles][roles-t] |
| Project XML and generated namespace | [ProtoCrossProject][project]: `Load`, `ReadName` | [Project names][names-t] |
| Project discovery and cached expansion | [ProjectDiscovery][discovery]: `Find`; [ProjectCatalog][projects]: `FilesOf` | [Discovery][discovery-t] |
| Source/test globs and membership | [ProjectSources][members]: `ExpandForBuild`, `RoleOf` | [Source patterns][members-t], [Build refusal][refusal-t] |
| Project-wide policy | [ProjectPolicy][policy]: `Resolve` | [CLI projects][cli-t] |
| CLI verbs, arguments, source/project builds | [CommandLine][cli]: `Run`, `All`; [CommandLineOptions][options]: `Parse`; [BuildCommand][build]: `Run`, `Generate`, `Write`, `CompilationOfSources`, `CompilationOfProject` | [CLI projects][cli-t], [Verbs][verbs-t] |
| Test project settings: `<Scaffold>` and its flags | [ScaffoldSettings][scaffold]: `ScaffoldSetting.All`, `ProblemWith`, `Flag`, `ScaffoldSettings.OverriddenBy`; [ProtoCrossProject][project]: `ReadScaffold`; [BuildCommand][build]: `ScaffoldSettingsFor`; [CommandLineOptions][options]: `AreScaffoldFlagsUsed` | [Settings][scaffold-t], [Project files][scaffold-project-t], [CLI][scaffold-cli-t], [Builds][scaffold-run-t] |
| Diagnostic codes and CLI rendering | [Diagnostic][diagnostics]: `DiagnosticBag.Report`, `Diagnostic.ToString`; [BuildCommand][build]: `PrintDiagnostics` | [Diagnostic codes][diagnostic-t] |
| Attribute and publish editor diagnostics | [CompilationDiagnostics][routing]: `Build`; [DiagnosticRouter][router]: `PublishAsync`, `ClearAsync` | [Diagnostic routing][routing-t] |

[comp]: ../src/ProtoCross.Core/Compilation.cs
[source]: ../src/ProtoCross.Core/SourceDocument.cs
[config]: ../src/ProtoCross.Core/Config/ProjectConfig.cs
[catalog]: ../src/ProtoCross.Core/Binding/SchemaCatalog.cs
[imports]: ../src/ProtoCross.Core/ImportResolution.cs
[protoc]: ../src/ProtoCross.Core/Binding/ProtocLocator.cs
[loader]: ../src/ProtoCross.Core/Binding/DescriptorLoader.cs
[tools]: ../src/ProtoCross.Toolchain/ToolLocator.cs
[lookup]: ../src/ProtoCross.Toolchain/ToolLookup.cs
[runner]: ../src/ProtoCross.Toolchain/ProcessRunner.cs
[trx]: ../src/ProtoCross.Toolchain/TrxLog.cs
[reports]: ../src/ProtoCross.Toolchain/TestReports.cs
[verdict]: ../src/ProtoCross.Toolchain/TestVerdict.cs
[cache]: ../src/ProtoCross.Core/Binding/DescriptorCache.cs
[bundle]: ../src/ProtoCross.Core/Binding/DescriptorBundle.cs
[tree]: ../src/ProtoCross.Core/SourceTree.cs
[closure]: ../src/ProtoCross.Core/ProductionSchemaClosure.cs
[project]: ../src/ProtoCross.Projects/ProtoCrossProject.cs
[discovery]: ../src/ProtoCross.Projects/ProjectDiscovery.cs
[projects]: ../src/ProtoCross.Projects/ProjectCatalog.cs
[members]: ../src/ProtoCross.Projects/ProjectSources.cs
[policy]: ../src/ProtoCross.Projects/ProjectPolicy.cs
[cli]: ../src/ProtoCross.Cli/CommandLine.cs
[options]: ../src/ProtoCross.Cli/CommandLineOptions.cs
[build]: ../src/ProtoCross.Cli/BuildCommand.cs
[scaffold]: ../src/ProtoCross.Core/Backend/ScaffoldSettings.cs
[diagnostics]: ../src/ProtoCross.Core/Diagnostics/Diagnostic.cs
[routing]: ../src/ProtoCross.LanguageServer/Hosting/CompilationDiagnostics.cs
[router]: ../src/ProtoCross.LanguageServer/Hosting/DiagnosticRouter.cs
[comp-t]: ../tests/ProtoCross.Tests/CompilationTests.cs
[memory-t]: ../tests/ProtoCross.Tests/InMemoryCompilationTests.cs
[config-t]: ../tests/ProtoCross.Tests/ProjectConfigTests.cs
[termination-t]: ../tests/ProtoCross.Tests/TerminationTests.cs
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
[verbs-t]: ../tests/ProtoCross.Tests/CliTests.Verbs.cs
[tools-t]: ../tests/ProtoCross.Tests/ToolchainTests.cs
[verdict-t]: ../tests/ProtoCross.Tests/TestVerdictTests.cs
[scaffold-t]: ../tests/ProtoCross.Tests/ScaffoldSettingsTests.cs
[scaffold-project-t]: ../tests/ProtoCross.Tests/ProjectFileTests.Scaffold.cs
[scaffold-cli-t]: ../tests/ProtoCross.Tests/CliTests.Scaffold.cs
[scaffold-run-t]: ../tests/ProtoCross.Tests/ScaffoldExecutionTests.Settings.cs
[diagnostic-t]: ../tests/ProtoCross.Tests/DiagnosticCodeTests.cs
[routing-t]: ../tests/ProtoCross.Tests/LanguageServerDiagnosticRoutingTests.cs
