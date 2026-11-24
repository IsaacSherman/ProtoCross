# C++ backend reference

[Feature index](Feature-Index.md) · [Architecture](ARCHITECTURE.md) · [IR](IR.md) · [C# backend](CSharp-Backend.md)

`CppBackend` consumes typed IR only. Follow IR behavior annotations, shared `IrMutation` / `IrFlow`, and protobuf name mapping in `NameConventions`. Preserve byte-for-byte output compatibility when refactoring. See [spec §23](../ProtoCross_Spec/§23-Backend%20Conformance%20Requirements.md) and [API strategy §24](../ProtoCross_Spec/§24-Generated%20API%20Strategy.md).

| Topic | Source and entry symbols | Relevant tests |
| --- | --- | --- |
| Production files | [Emitter]: `CppBackend.Emit`, `WriteDeclarations`, `WriteFactoryStruct`, `WriteDefinitions`; inline functions and factory structs in `<stem>.pc.h`, plus `protocross_runtime.h`. [Dispatch]: `SourceEmission.Emit` collects files per source. | [Backend tests] |
| Calls and placement | [Emitter]: `Expression`, `EmitCall`, `WriteEveryDeclarationFirst`, `SiblingHeadersCalledFrom`; [Context]: `Placement.NamespaceOf` (from `NameConventions.GetCppBehaviorNamespace`, or `GetCppHelpersNamespace` for a project's helper), `QualifiedFunctionOf`, `FactoryStructOf`, `MemberNameOf`, `IncludeGuardOf`; a header with more than one namespace or a sibling to call declares every namespace before defining any, and sibling includes follow the declarations. | [multiple sources]; [project placement]; [namespace layout]; [conformance] |
| Statements and control flow | [Statements]: `EmitStatement`, `EmitForEach`, `EmitIf`, `EmitSwitch`; [flow]: `IrFlow.NeverFallsThrough` decides section breaks. | [Backend tests]; [switches] |
| Numeric operations and conversions | [Numeric]: `EmitBinary`, `EmitIntegerDivision`, `EmitUnary`, `EmitConversion`, `EmitNumberToEnum`, `UsesFloatingRemainder`, `UsesFoldableFloatingDivision`; [Emitter]: `Deferred` hands every clause's fallback to the runtime as a lambda, so it is evaluated only where needed; [Formatting]: `EmitLiteral`, `FormatDouble`, `FormatInteger`, `FormatString`. | [Backend tests]; [arithmetic sweep]; [conformance] |
| Math functions | [Math]: `EmitMathCall`, a removed call as its argument alone, `<cmath>`'s functions otherwise (`CmathFunctionOf`, and `UsesCmathFunctions` for the include), arguments that may each end the program held in order (`InOrder`); [Runtime]: `EmitAbsHelper`, through the overflow mode's negation, and `EmitMathHelpers`, whose `math_min` and `math_max` give a NaN for a NaN and order the zeros. | [MathFunctionTests](../tests/ProtoCross.Tests/MathFunctionTests.cs); [conformance] |
| Message literals and fixtures | [Literals]: `MessageLiteral`, `EmitConstruction`, `EmitFields`; [Context]: `NamesFor`, `NameAllocator`; immediate lambdas or construction in place use shared name allocation. | [literal emission] |
| Mutation, copies and append | [Emitter]: `Signature`, `PassedArgument`, which copies a list's element a `mut fn` is handed, since `Get` gives a reference into the list; [Statements]: `EmitFieldAssignment`, `EmitAppend`, `EmitFieldWrite`, `StoredValue`, `MutableMessage`; [mutation]: `IrMutation.ChangesElementsOf`, `IsPassedAsACopy`, `IsReadFromAListsElement`. | [mutation emission]; [append emission] |
| A list's count and elements | [Lists]: `EmitListQuery`, `EmitElementLookup` through the runtime's `element_or` under a fallback, a lambda it calls only where there is no element, and `element_or_fail` under `fail`, the list and the index in the order written (`InElementOrder`); `EmitElementRead`, a proven index read with `Get`, and from the end with the runtime's `from_end`, which evaluates the list once; `MutableListElement` and `EmitListElementAssignment`, an element written through `Mutable`, and from the end through `mutable_from_end`, the list bound first where reaching it sets anything (`EmitOnCollection`); an enum element is read back as the enum; [Runtime]: `EmitElementHelpers`, whose `element_position` compares an index of any integer type with the count exactly. | [conformance] |
| Lists | [Literals]: `ListLiteral` builds a list in a called lambda, adding each element as an append to a local adds one; [Context]: `RepeatedTypeName`, `ParameterTypeName` (`const&`), `IsHeldByPointer`; [Statements]: a whole list assigned to a repeated field through `*mutable_x() =`, as a message field is (`FieldWriter.TakesAMessage`); [Tests]: `EmitListHelpers` compares and prints an expected list, only in a driver that expects one. | [conformance] |
| Methods that may refuse, and calls to them | [Refusals]: `EmitRefusableCall`, `EmitPassedOn`, `EmitPassedOnInto`, `EmitClauseRefusal`; [Context]: `Outs`, `Placement.Outs`, `Placement.Held`, `NamesFor`; a refusing method returns `bool` with reference parameters, a call written for its value is a lambda as a literal is, and a clause that refuses hands its fallback helper a lambda that marks it refused (`DividedOr`, `NamedOr`, `ValueOr`). | [refusal emission]; [clause refusal emission]; [conformance] |
| Map reads and writes | [Maps]: `EmitMapLookup`, `EmitMapQuery`, `MapsCompared`, `EmitElementAssignment`, `EmitKeyBeforeValue`, `EmitMapUpdate`, `MutableElement`. | [map emission] |
| Operands left to right | [Ordering]: `InOrder` holds, in the order written, the operands of a call, operator, division, lookup, change or test that may each end the program, as `IrEvaluationOrder.IsObservable` says, in locals of a lambda; `&&` and `\|\|` are never held. [Maps]: `EmitKeyBeforeValue`, `EmitMapEntries` hold a store's key before its value. | [operand order emission]; [conformance] |
| Runtime support | [Runtime]: `CppRuntime.Source`, `Stem`, `EmitFloatToIntegerHelper`, `EmitEnumHelpers`, `EmitMapHelpers`, `EmitRefusalHelper`, `EmitFailHelper`. | [Backend tests]; [conformance] |
| Schema includes and type names | [Schemas]: `SchemasNamedBeyond`, `SchemasNamedBy`, `ProtoHeadersOf`; [Names]: `NameConventions.GetCppProtoHeader`, `GetCppTypeName`, `GetCppFieldName`, `GetCppValueName`; [Context]: `TypeName`, `QualifiedTypeName`, `QualifiedEnumName`, `Escape`. | [schema includes]; [type names]; [field names] |
| Tests and projects | [Tests]: `EmitTests`, `HeadersTestedBy`, `EmitCppTest`, `EmitCppRefusingCall`, `EmitCppComparison`, `EmitListHelpers`, `EmitExpectFailHelpers`, `EmitTestProject`, `UniqueTestFunctionName`; [Project]: `CppTestProject.Build` writes `CMakeLists.txt` for `<stem>.tests.cc`, naming the project and prefixing every target as `ScaffoldOptions.Settings` says (`Prefixed`). | [Backend tests]; [scaffolds]; [scaffold execution]; [syntax smoke] |

[Emitter]: ../src/ProtoCross.Backend.Cpp/CppBackend.cs
[Statements]: ../src/ProtoCross.Backend.Cpp/CppBackend.Statements.cs
[Numeric]: ../src/ProtoCross.Backend.Cpp/CppBackend.Numeric.cs
[Formatting]: ../src/ProtoCross.Backend.Cpp/CppBackend.LiteralFormatting.cs
[Context]: ../src/ProtoCross.Backend.Cpp/CppBackend.EmissionContext.cs
[Tests]: ../src/ProtoCross.Backend.Cpp/CppBackend.Tests.cs
[Dispatch]: ../src/ProtoCross.Core/Backend/SourceEmission.cs
[flow]: ../src/ProtoCross.Core/Semantics/IrFlow.cs
[mutation]: ../src/ProtoCross.Core/Semantics/IrMutation.cs
[Literals]: ../src/ProtoCross.Backend.Cpp/CppBackend.MessageLiterals.cs
[Maps]: ../src/ProtoCross.Backend.Cpp/CppBackend.Maps.cs
[Math]: ../src/ProtoCross.Backend.Cpp/CppBackend.Math.cs
[Lists]: ../src/ProtoCross.Backend.Cpp/CppBackend.Lists.cs
[Ordering]: ../src/ProtoCross.Backend.Cpp/CppBackend.Ordering.cs
[Refusals]: ../src/ProtoCross.Backend.Cpp/CppBackend.Refusals.cs
[Runtime]: ../src/ProtoCross.Backend.Cpp/CppRuntime.cs
[Schemas]: ../src/ProtoCross.Backend.Cpp/CppBackend.Schemas.cs
[Names]: ../src/ProtoCross.Core/Backend/NameConventions.cs
[Project]: ../src/ProtoCross.Backend.Cpp/CppTestProject.cs
[Backend tests]: ../tests/ProtoCross.Tests/BackendTests.cs
[multiple sources]: ../tests/ProtoCross.Tests/BackendTests.MultiFile.cs
[project placement]: ../tests/ProtoCross.Tests/BackendTests.ProjectNamespace.cs
[namespace layout]: ../tests/ProtoCross.Tests/BackendTests.Namespaces.cs
[switches]: ../tests/ProtoCross.Tests/BackendTests.Switch.cs
[arithmetic sweep]: ../tests/ProtoCross.Tests/Conformance/ArithmeticSweepTests.cs
[conformance]: ../tests/ProtoCross.Tests/Conformance/ConformanceTests.cs
[literal emission]: ../tests/ProtoCross.Tests/BackendTests.CppLiterals.cs
[mutation emission]: ../tests/ProtoCross.Tests/BackendTests.Mutation.cs
[append emission]: ../tests/ProtoCross.Tests/BackendTests.Append.cs
[map emission]: ../tests/ProtoCross.Tests/BackendTests.Maps.cs
[operand order emission]: ../tests/ProtoCross.Tests/BackendTests.OperandOrder.cs
[refusal emission]: ../tests/ProtoCross.Tests/RefusalTests.cs
[clause refusal emission]: ../tests/ProtoCross.Tests/ClauseRefusalTests.cs
[schema includes]: ../tests/ProtoCross.Tests/BackendTests.CppSchemaIncludes.cs
[type names]: ../tests/ProtoCross.Tests/NameMappingTests.CppTypes.cs
[field names]: ../tests/ProtoCross.Tests/NameMappingTests.CppFields.cs
[scaffolds]: ../tests/ProtoCross.Tests/ScaffoldTests.cs
[scaffold execution]: ../tests/ProtoCross.Tests/ScaffoldExecutionTests.cs
[syntax smoke]: ../tests/ProtoCross.Tests/CppSyntaxSmokeTests.MultiFile.cs
