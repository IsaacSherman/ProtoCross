# C++ backend reference

[Feature index](Feature-Index.md) · [Architecture](ARCHITECTURE.md) · [IR](IR.md) · [C# backend](CSharp-Backend.md)

`CppBackend` consumes typed IR only. Follow IR behavior annotations, shared `IrMutation` / `IrFlow`, and protobuf name mapping in `NameConventions`. Preserve byte-for-byte output compatibility when refactoring. See [spec §23](../ProtoCross_Spec/§23-Backend%20Conformance%20Requirements.md) and [API strategy §24](../ProtoCross_Spec/§24-Generated%20API%20Strategy.md).

| Topic | Source and entry symbols | Relevant tests |
| --- | --- | --- |
| Production files | [Emitter]: `CppBackend.Emit`, `WriteDeclarations`, `WriteDefinitions`; inline functions in `<stem>.pc.h`, plus `protocross_runtime.h`. [Dispatch]: `SourceEmission.Emit` collects files per source. | [Backend tests] |
| Calls and placement | [Emitter]: `Expression`, `EmitCall`, `WriteAroundSiblings`, `SiblingHeadersCalledFrom`; [Context]: `Placement.QualifiedFunctionOf`, `IncludeGuardOf`; sibling includes follow declarations. | [multiple sources]; [project placement] |
| Statements and control flow | [Statements]: `EmitStatement`, `EmitForEach`, `EmitIf`, `EmitSwitch`; [flow]: `IrFlow.NeverFallsThrough` decides section breaks. | [Backend tests]; [switches] |
| Numeric operations and conversions | [Numeric]: `EmitBinary`, `EmitIntegerDivision`, `EmitUnary`, `EmitConversion`, `EmitNumberToEnum`, `UsesFloatingRemainder`, `UsesFoldableFloatingDivision`; [Formatting]: `EmitLiteral`, `FormatDouble`, `FormatInteger`, `FormatString`. | [Backend tests]; [arithmetic sweep]; [conformance] |
| Message literals and fixtures | [Literals]: `MessageLiteral`, `EmitConstruction`, `EmitFields`; [Context]: `NamesFor`, `NameAllocator`; immediate lambdas or construction in place use shared name allocation. | [literal emission] |
| Mutation, copies and append | [Emitter]: `Signature`; [Statements]: `EmitFieldAssignment`, `EmitAppend`, `EmitFieldWrite`, `StoredValue`, `MutableMessage`; [mutation]: `IrMutation.ChangesElementsOf`, `IsPassedAsACopy`. | [mutation emission]; [append emission] |
| Map reads and writes | [Maps]: `EmitMapLookup`, `EmitMapQuery`, `EmitMapEquality`, `EmitElementAssignment`, `EmitMapUpdate`, `MutableElement`. | [map emission] |
| Runtime support | [Runtime]: `CppRuntime.Source`, `Stem`, `EmitFloatToIntegerHelper`, `EmitEnumHelpers`, `EmitMapHelpers`, `EmitFailHelper`. | [Backend tests]; [conformance] |
| Schema includes and type names | [Schemas]: `SchemasNamedBeyond`, `SchemasNamedBy`, `ProtoHeadersOf`; [Names]: `NameConventions.GetCppProtoHeader`, `GetCppTypeName`, `GetCppFieldName`, `GetCppValueName`; [Context]: `TypeName`, `QualifiedTypeName`, `QualifiedEnumName`, `Escape`. | [schema includes]; [type names]; [field names] |
| Tests and projects | [Tests]: `EmitTests`, `HeadersTestedBy`, `EmitCppTest`, `EmitExpectFailHelpers`, `EmitTestProject`, `UniqueTestFunctionName`; [Project]: `CppTestProject.Build` writes `CMakeLists.txt` for `<stem>.tests.cc`. | [Backend tests]; [scaffolds]; [scaffold execution]; [syntax smoke] |

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
[Runtime]: ../src/ProtoCross.Backend.Cpp/CppRuntime.cs
[Schemas]: ../src/ProtoCross.Backend.Cpp/CppBackend.Schemas.cs
[Names]: ../src/ProtoCross.Core/Backend/NameConventions.cs
[Project]: ../src/ProtoCross.Backend.Cpp/CppTestProject.cs
[Backend tests]: ../tests/ProtoCross.Tests/BackendTests.cs
[multiple sources]: ../tests/ProtoCross.Tests/BackendTests.MultiFile.cs
[project placement]: ../tests/ProtoCross.Tests/BackendTests.ProjectNamespace.cs
[switches]: ../tests/ProtoCross.Tests/BackendTests.Switch.cs
[arithmetic sweep]: ../tests/ProtoCross.Tests/Conformance/ArithmeticSweepTests.cs
[conformance]: ../tests/ProtoCross.Tests/Conformance/ConformanceTests.cs
[literal emission]: ../tests/ProtoCross.Tests/BackendTests.CppLiterals.cs
[mutation emission]: ../tests/ProtoCross.Tests/BackendTests.Mutation.cs
[append emission]: ../tests/ProtoCross.Tests/BackendTests.Append.cs
[map emission]: ../tests/ProtoCross.Tests/BackendTests.Maps.cs
[schema includes]: ../tests/ProtoCross.Tests/BackendTests.CppSchemaIncludes.cs
[type names]: ../tests/ProtoCross.Tests/NameMappingTests.CppTypes.cs
[field names]: ../tests/ProtoCross.Tests/NameMappingTests.CppFields.cs
[scaffolds]: ../tests/ProtoCross.Tests/ScaffoldTests.cs
[scaffold execution]: ../tests/ProtoCross.Tests/ScaffoldExecutionTests.cs
[syntax smoke]: ../tests/ProtoCross.Tests/CppSyntaxSmokeTests.MultiFile.cs
