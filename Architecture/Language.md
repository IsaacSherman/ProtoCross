# Language implementation lookup

Start with the [feature index](Feature-Index.md). This page maps syntax and binding; [IR.md](IR.md) maps shared semantics and consumers. Linked specs own the rules.

| Topic | Source and entry symbols | Focused tests / spec |
|---|---|---|
| Lexing | [Lexer.cs](../src/ProtoCross.Core/Syntax/Lexer.cs): `Lexer.Tokenize`; [TokenKind.cs](../src/ProtoCross.Core/Syntax/TokenKind.cs): `TokenKind` | [LexerTests](../tests/ProtoCross.Tests/LexerTests.cs); [§6](../ProtoCross_Spec/§6-Lexical%20Structure.md) |
| Syntax and recovery | [Parser.cs][parser]: `ParseCompilationUnit`, `MaxNestingDepth` | [ParserResilienceTests](../tests/ProtoCross.Tests/ParserResilienceTests.cs); [§7](../ProtoCross_Spec/§7-Grammar%20and%20Syntax.md) |
| Contextual words | [ContextualKeywords.cs](../src/ProtoCross.Core/Syntax/ContextualKeywords.cs): `BeginsAMessageLiteral`, `MarksAMutatingMethod`, `BeginsAnOnUnknownClause` | [ContextualKeywordTests](../tests/ProtoCross.Tests/ContextualKeywordTests.cs); [§6](../ProtoCross_Spec/§6-Lexical%20Structure.md) |
| Names, types and calls | [Binder.cs][binder]: `Bind`, `ResolveTypeReference`, `BindInvocation` | [MultiSourceBindingTests](../tests/ProtoCross.Tests/MultiSourceBindingTests.cs); [§8](../ProtoCross_Spec/§8-Type%20System.md) |
| Operators and compound assignment | [Parser.cs][parser]: `GetBinaryPrecedence`; [Binder.cs][binder]: `BindBinary`, `BindIntegerDivision`, `BindCompoundAssignment` | [OperatorPrecedenceTests](../tests/ProtoCross.Tests/OperatorPrecedenceTests.cs), [CompoundAssignmentTests](../tests/ProtoCross.Tests/CompoundAssignmentTests.cs); [§9](../ProtoCross_Spec/§9-Expressions%20and%20Operators.md) |
| Numeric literals and casts | [Binder.cs][binder]: `BindCast`; [Binder.Literals.cs](../src/ProtoCross.Core/Binding/Binder.Literals.cs): `BindIntegerLiteral`, `BindFloatLiteral` | [NumericLiteralTests](../tests/ProtoCross.Tests/NumericLiteralTests.cs), [ConversionTests](../tests/ProtoCross.Tests/ConversionTests.cs); [§10](../ProtoCross_Spec/§10-Numeric%20Semantics.md) |
| Enum conversions, membership and openness | [Binder.Enums.cs](../src/ProtoCross.Core/Binding/Binder.Enums.cs): `BindEnumToNumber`, `BindNumberToEnum`, `BindEnumMembership`; [EnumOpenness.cs](../src/ProtoCross.Core/Types/EnumOpenness.cs): `IsClosed` | [EnumNumberTests](../tests/ProtoCross.Tests/EnumNumberTests.cs), [EnumMembershipTests](../tests/ProtoCross.Tests/EnumMembershipTests.cs); [§12](../ProtoCross_Spec/§12-Enums.md) |
| Maps | [Binder.Maps.cs](../src/ProtoCross.Core/Binding/Binder.Maps.cs): `BindLookup`, `BindMapUpdate`, `BindMapValue` | [MapTests](../tests/ProtoCross.Tests/MapTests.cs), [MapParsingTests](../tests/ProtoCross.Tests/MapParsingTests.cs); [§14](../ProtoCross_Spec/§14-Repeated%20Fields%20and%20Collections.md) |
| Mutation and append | [Binder.Mutation.cs](../src/ProtoCross.Core/Binding/Binder.Mutation.cs): `BindFieldAssignment`, `CheckMutatingCall`, `BindAppend` | [MutationTests](../tests/ProtoCross.Tests/MutationTests.cs); [§18](../ProtoCross_Spec/§18-Mutability.md) |
| Message literals | [Parser.cs][parser]: `ParseMessageLiteral`; [Binder.MessageLiterals.cs](../src/ProtoCross.Core/Binding/Binder.MessageLiterals.cs): `BindMessageLiteral`, `BindFieldInitializers` | [MessageLiteralTests](../tests/ProtoCross.Tests/MessageLiteralTests.cs); [§13](../ProtoCross_Spec/§13-Messages.md) |
| Blocks and ordinary statements | [Parser.cs][parser]: `ParseBlock`; [Binder.cs][binder]: `BindStatementItself`, `BindBlock` | [CompilationTests](../tests/ProtoCross.Tests/CompilationTests.cs); [§15](../ProtoCross_Spec/§15-Control%20Flow.md) |
| Field presence | [Binder.cs][binder]: `BindHas`, `PresenceFacts` | [PresenceTests](../tests/ProtoCross.Tests/PresenceTests.cs); [§13.1](../ProtoCross_Spec/§13-Messages.md#131-field-access) |
| Switch syntax and binding | [Parser.cs][parser]: `ParseSwitchStatement`, `ParseSwitchArm`; [Binder.Switch.cs](../src/ProtoCross.Core/Binding/Binder.Switch.cs): `BindSwitch` | [SwitchParsingTests](../tests/ProtoCross.Tests/SwitchParsingTests.cs), [SwitchTests](../tests/ProtoCross.Tests/SwitchTests.cs); [§15.3](../ProtoCross_Spec/§15-Control%20Flow.md#153-switch) |
| Test syntax and fixtures | [Parser.cs][parser]: `ParseTestDeclaration`, `ParseTestExpectation`; [Binder.cs][binder]: `BindTest`, `BindTestArguments` | [FixtureLiteralTests](../tests/ProtoCross.Tests/FixtureLiteralTests.cs); [§25.3](../ProtoCross_Spec/§25-Testing%20and%20Conformance%20Vectors.md#253-author-written-protocross-unit-tests) |

`Binder.Bind` declares all sources' methods before bodies. Recovery reaches binding; see [PartialBindingTests](../tests/ProtoCross.Tests/PartialBindingTests.cs).

Follow binder output through [IR.md](IR.md) to backends and editor queries; [§22](../ProtoCross_Spec/§22-IR%20and%20Compiler%20Architecture.md) defines the handoff.

[parser]: ../src/ProtoCross.Core/Syntax/Parser.cs
[binder]: ../src/ProtoCross.Core/Binding/Binder.cs
