# C# backend reference

[Feature index](Feature-Index.md) · [Architecture](ARCHITECTURE.md) · [IR](IR.md) · [C++ backend](Cpp-Backend.md)

`CSharpBackend` consumes typed IR only. Follow IR behavior annotations, shared `IrMutation` / `IrFlow`, and protobuf name mapping in `NameConventions`. Preserve byte-for-byte output compatibility when refactoring. See [spec §23](../ProtoCross_Spec/§23-Backend%20Conformance%20Requirements.md) and [API strategy §24](../ProtoCross_Spec/§24-Generated%20API%20Strategy.md).

| Topic | Source and entry symbols | Relevant tests |
| --- | --- | --- |
| Production entry and files | [Emitter]: `CSharpBackend.Emit`, `EmitExtensionClass`, `EmitMethod`; extension methods in `<stem>.g.cs`, plus `ProtoCrossArithmetic.g.cs`. [Dispatch]: `SourceEmission.Emit` partitions sources and collects `GeneratedFile`. | [Backend tests]; [multiple sources] |
| Statements and control flow | [Statements]: `EmitStatement`, `EmitIf`, `EmitSwitch`; [flow]: `IrFlow.NeverFallsThrough` decides section breaks. | [Backend tests]; [switches] |
| Calls and placement | [Emitter]: `Expression`, `EmitCall`; [Context]: `Placement.NamespaceOf`, `ClassOf`, `MethodNameOf`; project namespace and partial class select call targets. | [project placement]; [multiple sources] |
| Numeric operations and conversions | [Numeric]: `EmitBinary`, `EmitIntegerDivision`, `EmitUnary`, `EmitConversion`, `EmitNumberToEnum`; [Formatting]: `EmitLiteral`, `FormatFloatingPoint`, `FormatInteger`, `FormatString`. | [Backend tests]; [arithmetic sweep]; [conformance] |
| Message literals and fixtures | [Literals]: `MessageLiteral`, `FieldInitializer`, `StoredValue`; object initializers preserve field order and copies. | [literal emission]; [fixture order] |
| Mutation, copies and append | [Context]: `Body.ForLocal`, `Body.Collection`, `NamesDeclaredIn`; [Statements]: `WritableMessage`, `WritableField`, `WritableCollection`; [mutation]: `IrMutation.ChangesAMessage`, `IsPassedAsACopy`. | [mutation emission]; [append emission] |
| Map reads and writes | [Maps]: `EmitMapLookup`, `EmitMapQuery`, `EmitMapEquality`, `EmitElementAssignment`, `EmitMapUpdate`, `MapEntries`. | [map emission] |
| Runtime support | [Runtime]: `CSharpRuntime.Source`, `Stem`, `EmitMaps`, `EmitEnums`, `EmitFloatToInteger`, `EmitFail`; IR behavior selects helpers. | [Backend tests]; [conformance] |
| Schema type and property names | [Names]: `NameConventions.GetCSharpNamespace`, `GetCSharpTypeName`, `GetCSharpPropertyName`, `GetCSharpValueName`; [Context]: `TypeName`, `EnumValue`, `Escape`. | [namespaces]; [properties] |
| Generated tests and projects | [Tests]: `EmitTests`, `EmitTest`, `EmitFailTestDispatcher`, `EmitTestProject`, `UniqueTestMethodName`; [Test runtime]: `CSharpTestRuntime.Source`; [Project]: `CSharpTestProject.Build` writes `ProtoCrossTests.csproj`. | [Backend tests]; [scaffolds]; [scaffold execution] |

[Emitter]: ../src/ProtoCross.Backend.CSharp/CSharpBackend.cs
[Statements]: ../src/ProtoCross.Backend.CSharp/CSharpBackend.Statements.cs
[Numeric]: ../src/ProtoCross.Backend.CSharp/CSharpBackend.Numeric.cs
[Formatting]: ../src/ProtoCross.Backend.CSharp/CSharpBackend.LiteralFormatting.cs
[Context]: ../src/ProtoCross.Backend.CSharp/CSharpBackend.EmissionContext.cs
[Tests]: ../src/ProtoCross.Backend.CSharp/CSharpBackend.Tests.cs
[Dispatch]: ../src/ProtoCross.Core/Backend/SourceEmission.cs
[flow]: ../src/ProtoCross.Core/Semantics/IrFlow.cs
[mutation]: ../src/ProtoCross.Core/Semantics/IrMutation.cs
[Literals]: ../src/ProtoCross.Backend.CSharp/CSharpBackend.MessageLiterals.cs
[Maps]: ../src/ProtoCross.Backend.CSharp/CSharpBackend.Maps.cs
[Runtime]: ../src/ProtoCross.Backend.CSharp/CSharpRuntime.cs
[Names]: ../src/ProtoCross.Core/Backend/NameConventions.cs
[Test runtime]: ../src/ProtoCross.Backend.CSharp/CSharpTestRuntime.cs
[Project]: ../src/ProtoCross.Backend.CSharp/CSharpTestProject.cs
[Backend tests]: ../tests/ProtoCross.Tests/BackendTests.cs
[multiple sources]: ../tests/ProtoCross.Tests/BackendTests.MultiFile.cs
[switches]: ../tests/ProtoCross.Tests/BackendTests.Switch.cs
[project placement]: ../tests/ProtoCross.Tests/BackendTests.ProjectNamespace.cs
[arithmetic sweep]: ../tests/ProtoCross.Tests/Conformance/ArithmeticSweepTests.cs
[conformance]: ../tests/ProtoCross.Tests/Conformance/ConformanceTests.cs
[literal emission]: ../tests/ProtoCross.Tests/BackendTests.CSharpLiterals.cs
[fixture order]: ../tests/ProtoCross.Tests/BackendTests.FixtureOrder.cs
[mutation emission]: ../tests/ProtoCross.Tests/BackendTests.Mutation.cs
[append emission]: ../tests/ProtoCross.Tests/BackendTests.Append.cs
[map emission]: ../tests/ProtoCross.Tests/BackendTests.Maps.cs
[namespaces]: ../tests/ProtoCross.Tests/NameMappingTests.CSharpNamespaces.cs
[properties]: ../tests/ProtoCross.Tests/NameMappingTests.CSharpProperties.cs
[scaffolds]: ../tests/ProtoCross.Tests/ScaffoldTests.cs
[scaffold execution]: ../tests/ProtoCross.Tests/ScaffoldExecutionTests.cs
