# C# backend reference

[Feature index](Feature-Index.md) · [Architecture](ARCHITECTURE.md) · [IR](IR.md) · [C++ backend](Cpp-Backend.md)

`CSharpBackend` consumes typed IR only. Follow IR behavior annotations, shared `IrMutation` / `IrFlow`, and protobuf name mapping in `NameConventions`. Preserve byte-for-byte output compatibility when refactoring. See [spec §23](../ProtoCross_Spec/§23-Backend%20Conformance%20Requirements.md) and [API strategy §24](../ProtoCross_Spec/§24-Generated%20API%20Strategy.md).

| Topic | Source and entry symbols | Relevant tests |
| --- | --- | --- |
| Production entry and files | [Emitter]: `CSharpBackend.Emit`, `EmitExtensionClass`, `EmitFactoryClass`, `EmitMethod`; extension methods and factory classes in `<stem>.g.cs`, plus `ProtoCrossArithmetic.g.cs`. [Dispatch]: `SourceEmission.Emit` partitions sources and collects `GeneratedFile`. | [Backend tests]; [multiple sources] |
| Statements and control flow | [Statements]: `EmitStatement`, `EmitIf`, `EmitSwitch`; [flow]: `IrFlow.NeverFallsThrough` decides section breaks. | [Backend tests]; [switches] |
| Calls and placement | [Emitter]: `Expression`, `EmitCall`; [Context]: `Placement.NamespaceOf` (from `NameConventions.GetCSharpBehaviorNamespace`), `ClassOf`, `FactoryClassOf`, `MethodNameOf`; project namespace and partial class select call targets, and a factory's static class its methods'. | [project placement]; [multiple sources] |
| Numeric operations and conversions | [Numeric]: `EmitBinary`, `EmitIntegerDivision`, `EmitUnary`, `EmitConversion`, `EmitNumberToEnum`; [Formatting]: `EmitLiteral`, `FormatFloatingPoint`, `FormatInteger`, `FormatString`. | [Backend tests]; [arithmetic sweep]; [conformance] |
| Message literals and fixtures | [Literals]: `MessageLiteral`, `FieldInitializer`, `StoredValue`; object initializers preserve field order and copies. | [literal emission]; [fixture order] |
| Mutation, copies and append | [Context]: `Body.ForLocal`, `Body.Collection`, `NamesDeclaredIn`; [Statements]: `WritableMessage`, `WritableField`, `WritableCollection`; [mutation]: `IrMutation.ChangesAMessage`, `IsPassedAsACopy`. | [mutation emission]; [append emission] |
| Methods that may refuse, and calls to them | [Refusals]: `EmitRefusableCall`, `EmitStandingRefusableCall`, `EmitPassedOn`, `EmitBuilt`, `EmitRefused`, `EmitClauseRefusal`, `OrNull`; [Context]: `Outs`, `BuiltNamesIn`, `HeldStemOf`, `Placement.Built`; [Wrappers]: `EmitRefusableChangeThroughACopy`; a refusing method returns `bool` with `out` parameters, each call's `out var` and each refusing clause's local is named once per method, and a clause that refuses is matched against its `…OrNull` helper before its statement. | [refusal emission]; [clause refusal emission]; [conformance] |
| Map reads and writes | [Maps]: `EmitMapLookup`, `EmitMapQuery`, `EmitMapEquality`, `EmitElementAssignment`, `EmitMapUpdate`, `MapEntries`. | [map emission] |
| Wrapper fields (`Int64Value` and the rest) | [Wrappers]: `WrappedScalar`, `IsHeldAsItsValue`, `HeldValue`, `MessageOf`, `FieldValue`, `HeldPlace`, `StoresThroughAReach`, `EmitStoreThroughAReach`, `EmitChangeThroughACopy`; [Statements]: `Traversed` binds each element of a repeated wrapper as a place, through `ProtoCrossWrappers` in [Runtime]. protoc's C# holds a wrapper field as its value, and the language as a message. | [wrapper emission]; [wrapper review]; [conformance] |
| Runtime support | [Runtime]: `CSharpRuntime.Source`, `Stem`, `EmitDivision`, `EmitMaps`, `EmitEnums`, `EmitWrappers`, `EmitRefusals`, `EmitFloatToInteger`, `EmitFail`; IR behavior selects helpers, and a fallback follows a helper that gives null after `??`, so it is evaluated only where needed. | [Backend tests]; [conformance] |
| Schema type and property names | [Names]: `NameConventions.GetCSharpNamespace`, `GetCSharpTypeName`, `GetCSharpPropertyName`, `GetCSharpValueName`; [Context]: `TypeName`, `EnumValue`, `Escape`. | [namespaces]; [properties] |
| Generated tests and projects | [Tests]: `EmitTests`, `EmitTest`, `EmitRefusingTest`, `EmitRefusalExpectations`, `EmitFailTestDispatcher`, `EmitTestProject`, `UniqueTestMethodName`; [Test runtime]: `CSharpTestRuntime.Source`; [Project]: `CSharpTestProject.Build` writes `ProtoCrossTests.csproj`. | [Backend tests]; [scaffolds]; [scaffold execution] |

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
[Refusals]: ../src/ProtoCross.Backend.CSharp/CSharpBackend.Refusals.cs
[Wrappers]: ../src/ProtoCross.Backend.CSharp/CSharpBackend.Wrappers.cs
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
[refusal emission]: ../tests/ProtoCross.Tests/RefusalTests.cs
[clause refusal emission]: ../tests/ProtoCross.Tests/ClauseRefusalTests.cs
[wrapper emission]: ../tests/ProtoCross.Tests/BackendTests.Wrappers.cs
[wrapper review]: ../tests/ProtoCross.Tests/WrapperReviewRegressionTests.cs
[namespaces]: ../tests/ProtoCross.Tests/NameMappingTests.CSharpNamespaces.cs
[properties]: ../tests/ProtoCross.Tests/NameMappingTests.CSharpProperties.cs
[scaffolds]: ../tests/ProtoCross.Tests/ScaffoldTests.cs
[scaffold execution]: ../tests/ProtoCross.Tests/ScaffoldExecutionTests.cs
