# Feature lookup

Start here to choose a small read set. Open the relevant topic guide, search for its named symbols,
and read their local documentation and callers. Expand to another guide when the change crosses a
contract. [ARCHITECTURE.md](ARCHITECTURE.md) explains the full pipeline and its invariants;
[the specification](../ProtoCross_Spec/README.md) defines the language.

The binder, parser, and backends use partial files grouped by responsibility. The topic guides link
to each group's actual home; central files hold dispatch and shared state rather than every feature.

## Find the feature

| Feature or question | Topic guide | First symbols to find |
|---|---|---|
| Tokens, spelling, keywords, grammar, parser recovery | [Language](Language.md) | `Lexer.Tokenize`, `TokenKind`, `Parser.ParseCompilationUnit` |
| Operators, casts, numeric behavior | [Language](Language.md) | `Binder.BindBinary`, `Binder.BindCast`, `IrBinary`, `IrConversion` |
| Enum values, openness, unknown numbers | [Language](Language.md) | `Binder` in `Binder.Enums.cs`, `EnumOpenness` |
| Message construction, maps, mutation, presence | [Language](Language.md) | `Binder` in `Binder.MessageLiterals.cs`, `Binder.Maps.cs`, `Binder.Mutation.cs` |
| Control flow, switches, method calls, language tests | [Language](Language.md) | `Binder.BindExpression`, `Binder.BindTest`, `Parser.ParseBlock` |
| Node shapes, behavior annotations, copies, flow | [IR](IR.md) | `IrExpression`, `IrCopy.Of`, `IrFlow.NeverFallsThrough`, `IrMutation` |
| Tree walks, references, scopes, source positions | [IR](IR.md) | `SyntaxWalk.ChildrenOf`, `IrWalk.ChildrenOf`, `SemanticModel` |
| C# output, runtime helpers, generated test projects | [C# backend](CSharp-Backend.md) | `CSharpBackend`, `CSharpRuntime`, `CSharpTestProject` |
| C++ output, schema includes, ownership, generated tests | [C++ backend](Cpp-Backend.md) | `CppBackend`, `CppRuntime`, `CppTestProject` |
| Sources, configuration, imports, protobuf descriptors | [Compilation and projects](Compilation-and-Projects.md) | `Compilation.Compile`, `ProjectConfig`, `DescriptorLoader`, `SchemaCatalog` |
| Project discovery/membership, build roles, CLI | [Compilation and projects](Compilation-and-Projects.md) | `ProjectDiscovery.Find`, `ProjectSources.ExpandForBuild`, `Program` |
| Completion, hover, navigation, semantic tokens | [Editor](Editor.md) | `CompletionProvider.Symbols`, `HoverProvider`, `DefinitionProvider`, `ClassificationProvider` |
| Editor compilation, configuration, watching, trust, extension | [Editor](Editor.md) | `DocumentSemantics`, `CompileScheduler`, `WorkspaceConfiguration`, `WatchedFiles` |

## Follow the contracts

- **A new syntax or IR node:** follow [Language](Language.md) into [IR](IR.md). Check both tree
  walkers and position queries, then each backend that consumes the node. `TreeWalkTests` and
  `IrContractTests` cover traversal and retained information.
- **A language behavior change:** settle the binder/IR contract, then consult both backend guides.
  Put executable semantics in the [conformance corpus](../tests/conformance/README.md), which runs
  generated C# and C++. Check the affected editor context if authors can write the feature there.
- **An emission-only change:** start with the target backend guide and its runtime/naming helpers.
  Check generated output against the base; the [architecture invariants](ARCHITECTURE.md#invariants-that-constrain-a-change)
  state the compatibility boundary.
- **An editor-only change:** start with [Editor](Editor.md) and the existing semantic queries before
  expanding into the binder. For configuration or schema freshness, follow its compilation links.

## Keep the lookup small

Update the owning row when a feature, entry symbol, or file moves. Keep rationale beside the code and
normative rules in the spec; these pages are navigation. Symbol names are search anchors, so a line
number change does not stale the lookup. Read the relevant test cases or sweep generator before
opening large generated vectors. Testing requirements live in
[Testing.md](../Agent_Instructions/Testing.md), and spec updates in
[SpecMaintenance.md](../Agent_Instructions/SpecMaintenance.md).
