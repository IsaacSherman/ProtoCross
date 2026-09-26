namespace ProtoCross.Diagnostics;

/// <summary>
/// Every diagnostic the compiler raises: the front end's <c>PC0###</c> codes and the driver and
/// configuration file's <c>PC20##</c> codes, with the severity and title each one always carries.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the allocation list.</b> Codes are published output (spec 26) and the spec names a
/// couple of dozen of them in prose, so the set has to be knowable. Before this table, allocating the
/// next code meant grepping for <c>PC0</c> and taking the maximum, and keeping two sites that share a
/// code in agreement meant noticing that they did -- which is how PC0015 came to render two different
/// titles, one of them wrong about the rule it was naming. The fields are in numeric order for that
/// reason: the next free code in a range is the one after its last field, and
/// <c>DiagnosticCodeTests</c> fails if a range ever acquires a gap or a duplicate.
/// </para>
/// <para>
/// <b>What is not here.</b> The <c>PC21##</c> host-configuration codes live in
/// <c>HostDiagnosticCodes</c> in the language server, because they are raised by an editor host and
/// this assembly carries nothing editor-specific. Backend codes have no table at all: PC1001 and
/// PC1101 were the only two and #10 removed them with <c>virtual</c>. See <see cref="Retired"/>.
/// </para>
/// </remarks>
public static class DiagnosticCodes
{
    /// <summary>
    /// Codes that were raised by a released compiler and no longer exist, and which must never be
    /// allocated to anything else.
    /// </summary>
    /// <remarks>
    /// A code is how a reader finds an explanation and how an editor client filters, so reusing one
    /// makes every older account of it wrong. Spec 17 still names PC1001 and PC1101 as the diagnostics
    /// the two backends raised for <c>virtual</c> before #10 dropped the keyword, which is history
    /// worth keeping; this set is what lets the test that every code the documentation names still
    /// exists tell that reference apart from a stale one.
    /// </remarks>
    public static readonly IReadOnlySet<string> Retired =
        new HashSet<string>(StringComparer.Ordinal) { "PC1001", "PC1101" };

    // ------------------------------------------------------- sources and schemas

    /// <summary>A ProtoCross file imports no protobuf schema at all (spec 5.2).</summary>
    public static readonly DiagnosticDescriptor NoProtoImports =
        new("PC0001", DiagnosticSeverity.Error, "no proto imports");

    /// <summary>An imported schema is in none of the include directories (spec 5.2).</summary>
    public static readonly DiagnosticDescriptor ProtoFileNotFound =
        new("PC0002", DiagnosticSeverity.Error, "proto file not found");

    /// <summary>protoc refused the imported schemas, or could not be run at all (spec 21.1).</summary>
    public static readonly DiagnosticDescriptor SchemaLoadFailed =
        new("PC0003", DiagnosticSeverity.Error, "protobuf schema could not be loaded");

    // ------------------------------------------------------- lexical

    /// <summary>A block comment reached the end of the file unclosed (spec 6.2).</summary>
    public static readonly DiagnosticDescriptor UnterminatedBlockComment =
        new("PC0004", DiagnosticSeverity.Error, "unterminated block comment");

    /// <summary>A numeric literal spelled no way spec 6.6 allows, such as <c>0x</c> or <c>5u</c>.</summary>
    public static readonly DiagnosticDescriptor InvalidNumericLiteral =
        new("PC0005", DiagnosticSeverity.Error, "invalid numeric literal");

    /// <summary>An integer literal larger than uint64 MAX, which no integer type can hold (spec 6.6).</summary>
    public static readonly DiagnosticDescriptor IntegerLiteralOutOfRange =
        new("PC0006", DiagnosticSeverity.Error, "integer literal out of range");

    /// <summary>An escape sequence in a string literal that the language does not define (spec 11.1).</summary>
    public static readonly DiagnosticDescriptor UnrecognizedEscapeSequence =
        new("PC0007", DiagnosticSeverity.Error, "unrecognized escape sequence");

    /// <summary>A string literal that reached the end of its line unclosed (spec 11.1).</summary>
    public static readonly DiagnosticDescriptor UnterminatedStringLiteral =
        new("PC0008", DiagnosticSeverity.Error, "unterminated string literal");

    /// <summary>A character that begins no token in the language (spec 6.1).</summary>
    public static readonly DiagnosticDescriptor UnexpectedCharacter =
        new("PC0009", DiagnosticSeverity.Error, "unexpected character");

    // ------------------------------------------------------- syntax

    /// <summary>The token the grammar requires here is not the one that is here (spec 7.1).</summary>
    public static readonly DiagnosticDescriptor UnexpectedToken =
        new("PC0010", DiagnosticSeverity.Error, "unexpected token");

    /// <summary>A top-level declaration that is not an import, an extend or a test (spec 7.1).</summary>
    public static readonly DiagnosticDescriptor UnexpectedTopLevelDeclaration =
        new("PC0011", DiagnosticSeverity.Error, "unexpected top-level declaration");

    /// <summary>An extend block contains something that is not a method (spec 7.1).</summary>
    public static readonly DiagnosticDescriptor UnexpectedExtendMember =
        new("PC0012", DiagnosticSeverity.Error, "unexpected member in extend block");

    /// <summary>A type was required and the token there cannot begin one (spec 7.1).</summary>
    public static readonly DiagnosticDescriptor ExpectedType =
        new("PC0013", DiagnosticSeverity.Error, "expected a type");

    /// <summary>An expression was required and the token there cannot begin one (spec 7.1).</summary>
    public static readonly DiagnosticDescriptor ExpectedExpression =
        new("PC0014", DiagnosticSeverity.Error, "expected an expression");

    /// <summary>
    /// An <c>on_zero</c> clause on an operator that cannot fail on a zero operand: any operator other
    /// than <c>/</c> and <c>%</c>, and either of those on floating-point operands (spec 10.2.1).
    /// </summary>
    /// <remarks>
    /// One rule and one code covering both conditions is spec 10.2.1's own wording. The title is the
    /// binder's, because it is the one that is true of the rule: <c>on_zero</c> is valid on integer
    /// division and on nothing else. The parser used to raise the same code titled "on_zero is only
    /// valid on division", which told a reader that float division took it.
    /// </remarks>
    public static readonly DiagnosticDescriptor OnZeroOutsideIntegerDivision =
        new("PC0015", DiagnosticSeverity.Error, "on_zero is only valid on integer division");

    /// <summary>A test block contains something other than receiver, arg or expect (spec 25.3).</summary>
    public static readonly DiagnosticDescriptor UnexpectedTestMember =
        new("PC0016", DiagnosticSeverity.Error, "unexpected member in test block");

    /// <summary>A test declares no receiver fixture (spec 25.3).</summary>
    public static readonly DiagnosticDescriptor TestHasNoReceiver =
        new("PC0017", DiagnosticSeverity.Error, "test is missing a receiver");

    /// <summary>A test declares neither an expected return nor an expected failure (spec 25.3).</summary>
    public static readonly DiagnosticDescriptor TestHasNoExpectation =
        new("PC0018", DiagnosticSeverity.Error, "test is missing an expectation");

    /// <summary>An <c>expect</c> followed by something that is not <c>return</c> or <c>fail</c> (spec 25.3).</summary>
    public static readonly DiagnosticDescriptor ExpectedTestExpectation =
        new("PC0019", DiagnosticSeverity.Error, "expected a test expectation");

    // ------------------------------------------------------- declarations

    /// <summary>A simple message name that matches more than one imported message (spec 13).</summary>
    public static readonly DiagnosticDescriptor AmbiguousMessageName =
        new("PC0020", DiagnosticSeverity.Error, "ambiguous message name");

    /// <summary>An extend names a message no imported schema declares (spec 13).</summary>
    public static readonly DiagnosticDescriptor UnknownMessageType =
        new("PC0021", DiagnosticSeverity.Error, "unknown message type");

    /// <summary>Two methods with one name on one receiver; there is no overloading (spec 16.1).</summary>
    public static readonly DiagnosticDescriptor DuplicateMethod =
        new("PC0022", DiagnosticSeverity.Error, "duplicate method");

    /// <summary>A method named like a field of the message it extends (spec 16.1).</summary>
    public static readonly DiagnosticDescriptor MethodNameCollidesWithField =
        new("PC0023", DiagnosticSeverity.Error, "method name collides with a field");

    /// <summary>A parameter or variable declared <c>void</c>, which holds no value (spec 16.2).</summary>
    public static readonly DiagnosticDescriptor VoidIsNotAValueType =
        new("PC0024", DiagnosticSeverity.Error, "void is not a value type");

    /// <summary>A type name that is no protobuf scalar, message or enum (spec 8.1).</summary>
    public static readonly DiagnosticDescriptor UnknownType =
        new("PC0025", DiagnosticSeverity.Error, "unknown type");

    /// <summary>Two parameters of one method share a name (spec 16.2).</summary>
    public static readonly DiagnosticDescriptor DuplicateParameter =
        new("PC0026", DiagnosticSeverity.Error, "duplicate parameter");

    /// <summary>A method with a return type has a path that returns nothing (spec 16.2).</summary>
    public static readonly DiagnosticDescriptor MissingReturnStatement =
        new("PC0027", DiagnosticSeverity.Error, "missing return statement");

    // ------------------------------------------------------- statements and names

    /// <summary>A variable initialized with a value of another type (spec 8).</summary>
    public static readonly DiagnosticDescriptor VariableInitializerTypeMismatch =
        new("PC0028", DiagnosticSeverity.Error, "type mismatch in variable initializer");

    /// <summary>A variable declared where one of that name is already in scope (spec 15).</summary>
    public static readonly DiagnosticDescriptor DuplicateVariable =
        new("PC0029", DiagnosticSeverity.Error, "duplicate variable");

    /// <summary>A bare <c>return</c> in a method that declares a return type (spec 16.2).</summary>
    public static readonly DiagnosticDescriptor MissingReturnValue =
        new("PC0030", DiagnosticSeverity.Error, "missing return value");

    /// <summary>A <c>return</c> with a value in a method that declares no return type (spec 16.2).</summary>
    public static readonly DiagnosticDescriptor UnexpectedReturnValue =
        new("PC0031", DiagnosticSeverity.Error, "unexpected return value");

    /// <summary>A returned value whose type is not the declared one (spec 16.2).</summary>
    public static readonly DiagnosticDescriptor ReturnTypeMismatch =
        new("PC0032", DiagnosticSeverity.Error, "return type mismatch");

    /// <summary>A <c>for</c> over a value that is not a repeated field (spec 14.1).</summary>
    public static readonly DiagnosticDescriptor NotIterable =
        new("PC0033", DiagnosticSeverity.Error, "not iterable");

    /// <summary>An assignment to anything but a local variable (spec 18).</summary>
    public static readonly DiagnosticDescriptor InvalidAssignmentTarget =
        new("PC0034", DiagnosticSeverity.Error, "invalid assignment target");

    /// <summary>An assignment of a value whose type is not the local's (spec 18).</summary>
    public static readonly DiagnosticDescriptor AssignmentTypeMismatch =
        new("PC0035", DiagnosticSeverity.Error, "type mismatch in assignment");

    /// <summary>An integer literal outside the range of the type the context gives it (spec 10.3).</summary>
    public static readonly DiagnosticDescriptor LiteralOutOfRangeForItsType =
        new("PC0036", DiagnosticSeverity.Error, "integer literal out of range");

    /// <summary>A bare name that is no variable, parameter or field of the receiver (spec 13.1).</summary>
    public static readonly DiagnosticDescriptor UnknownName =
        new("PC0037", DiagnosticSeverity.Error, "unknown name");

    /// <summary>A map field, which this version of the compiler does not support (spec 14.2).</summary>
    public static readonly DiagnosticDescriptor MapsAreNotSupported =
        new("PC0038", DiagnosticSeverity.Error, "maps are not supported");

    // ------------------------------------------------------- member access and calls

    /// <summary>A member read from a value that is not a message (spec 13.1).</summary>
    public static readonly DiagnosticDescriptor MemberAccessOnANonMessage =
        new("PC0039", DiagnosticSeverity.Error, "member access on a non-message value");

    /// <summary>A method named where a value is wanted, without calling it (spec 16).</summary>
    public static readonly DiagnosticDescriptor MethodUsedAsAValue =
        new("PC0040", DiagnosticSeverity.Error, "method used as a value");

    /// <summary>A member access or <c>has</c> operand that the message has no field for (spec 13.1).</summary>
    public static readonly DiagnosticDescriptor UnknownField =
        new("PC0041", DiagnosticSeverity.Error, "unknown field");

    /// <summary>A method called on a value that is not a message (spec 16.1).</summary>
    public static readonly DiagnosticDescriptor MethodCallOnANonMessage =
        new("PC0042", DiagnosticSeverity.Error, "method call on a non-message value");

    /// <summary>A call of something that is not a ProtoCross method (spec 16).</summary>
    public static readonly DiagnosticDescriptor ExpressionIsNotCallable =
        new("PC0043", DiagnosticSeverity.Error, "expression is not callable");

    /// <summary>A call naming a method the receiver does not have (spec 16.1).</summary>
    public static readonly DiagnosticDescriptor UnknownMethod =
        new("PC0044", DiagnosticSeverity.Error, "unknown method");

    /// <summary>A call with more or fewer arguments than the method declares (spec 16.2).</summary>
    public static readonly DiagnosticDescriptor WrongNumberOfArguments =
        new("PC0045", DiagnosticSeverity.Error, "wrong number of arguments");

    /// <summary>An argument whose type is not the parameter's (spec 16.2).</summary>
    public static readonly DiagnosticDescriptor ArgumentTypeMismatch =
        new("PC0046", DiagnosticSeverity.Error, "argument type mismatch");

    // ------------------------------------------------------- operators

    /// <summary><c>and</c> or <c>or</c> applied to something that is not a bool (spec 9.2).</summary>
    public static readonly DiagnosticDescriptor LogicalOperatorRequiresBoolOperands =
        new("PC0047", DiagnosticSeverity.Error, "logical operator requires bool operands");

    /// <summary>A binary operator whose two operands are of different types (spec 9.2).</summary>
    public static readonly DiagnosticDescriptor OperandTypeMismatch =
        new("PC0048", DiagnosticSeverity.Error, "operand type mismatch");

    /// <summary>A relational operator on operands that have no ordering (spec 9.2).</summary>
    public static readonly DiagnosticDescriptor OperandsAreNotOrdered =
        new("PC0049", DiagnosticSeverity.Error, "operands are not ordered");

    /// <summary>An arithmetic operator on a type that is not numeric (spec 9.2).</summary>
    public static readonly DiagnosticDescriptor ArithmeticOnANonNumericType =
        new("PC0050", DiagnosticSeverity.Error, "arithmetic on a non-numeric type");

    /// <summary>Unary <c>-</c> on a value that is not numeric (spec 9.2).</summary>
    public static readonly DiagnosticDescriptor NegationRequiresANumericOperand =
        new("PC0051", DiagnosticSeverity.Error, "negation requires a numeric operand");

    /// <summary>Unary <c>-</c> on an unsigned type, which cannot represent the result (spec 10.1).</summary>
    public static readonly DiagnosticDescriptor NegationOfAnUnsignedType =
        new("PC0052", DiagnosticSeverity.Error, "negation of an unsigned type");

    /// <summary><c>not</c> applied to a value that is not a bool (spec 9.2).</summary>
    public static readonly DiagnosticDescriptor LogicalNotRequiresABoolOperand =
        new("PC0053", DiagnosticSeverity.Error, "logical not requires a bool operand");

    /// <summary>Integer <c>/</c> or <c>%</c> by a divisor not proven non-zero, with no fallback (spec 10.2.1).</summary>
    public static readonly DiagnosticDescriptor MissingOnZeroClause =
        new("PC0054", DiagnosticSeverity.Error, "integer division requires an on_zero clause");

    /// <summary>An <c>on_zero</c> fallback of a type the division does not produce (spec 10.2.1).</summary>
    public static readonly DiagnosticDescriptor OnZeroTypeMismatch =
        new("PC0055", DiagnosticSeverity.Error, "on_zero type mismatch");

    /// <summary>An <c>on_zero</c> clause on a division by a non-zero literal, which cannot run (spec 10.2.1).</summary>
    public static readonly DiagnosticDescriptor UnnecessaryOnZeroClause =
        new("PC0056", DiagnosticSeverity.Warning, "unnecessary on_zero clause");

    // ------------------------------------------------------- author-written unit tests

    /// <summary>A test target that does not name a method (spec 25.3).</summary>
    public static readonly DiagnosticDescriptor InvalidTestTarget =
        new("PC0057", DiagnosticSeverity.Error, "invalid test target");

    /// <summary>A test target naming a method the receiver does not have (spec 25.3).</summary>
    public static readonly DiagnosticDescriptor UnknownTestTarget =
        new("PC0058", DiagnosticSeverity.Error, "unknown test target");

    /// <summary>A fixture setting a field the message does not declare (spec 25.3).</summary>
    public static readonly DiagnosticDescriptor UnknownFixtureField =
        new("PC0059", DiagnosticSeverity.Error, "unknown fixture field");

    /// <summary>A fixture setting a map field, which this compiler does not support (spec 25.3).</summary>
    public static readonly DiagnosticDescriptor MapsAreNotSupportedInFixtures =
        new("PC0060", DiagnosticSeverity.Error, "maps are not supported in test fixtures");

    /// <summary>A fixture setting one field more than once (spec 25.3).</summary>
    public static readonly DiagnosticDescriptor DuplicateFixtureField =
        new("PC0061", DiagnosticSeverity.Error, "duplicate fixture field");

    /// <summary>A message-typed fixture field set from an expression instead of a block (spec 25.3).</summary>
    public static readonly DiagnosticDescriptor FixtureFieldRequiresANestedValue =
        new("PC0062", DiagnosticSeverity.Error, "fixture field requires a nested value");

    /// <summary>A fixture value whose type is not the field's (spec 25.3).</summary>
    public static readonly DiagnosticDescriptor FixtureFieldTypeMismatch =
        new("PC0063", DiagnosticSeverity.Error, "fixture field type mismatch");

    /// <summary>A nested fixture block on a field that is not a message (spec 25.3).</summary>
    public static readonly DiagnosticDescriptor FixtureFieldIsNotAMessage =
        new("PC0064", DiagnosticSeverity.Error, "fixture field is not a message");

    /// <summary>A test supplying one argument more than once (spec 25.3).</summary>
    public static readonly DiagnosticDescriptor DuplicateTestArgument =
        new("PC0065", DiagnosticSeverity.Error, "duplicate test argument");

    /// <summary>A test supplying no value for a parameter the method declares (spec 25.3).</summary>
    public static readonly DiagnosticDescriptor MissingTestArgument =
        new("PC0066", DiagnosticSeverity.Error, "missing test argument");

    /// <summary>A test argument whose type is not the parameter's (spec 25.3).</summary>
    public static readonly DiagnosticDescriptor TestArgumentTypeMismatch =
        new("PC0067", DiagnosticSeverity.Error, "test argument type mismatch");

    /// <summary>A test argument naming no parameter of the method (spec 25.3).</summary>
    public static readonly DiagnosticDescriptor UnknownTestArgument =
        new("PC0068", DiagnosticSeverity.Error, "unknown test argument");

    /// <summary>An <c>expect return</c> on a method that returns nothing (spec 25.3).</summary>
    public static readonly DiagnosticDescriptor VoidMethodCannotExpectAReturnValue =
        new("PC0069", DiagnosticSeverity.Error, "void method cannot expect a return value");

    /// <summary>An expected value whose type is not the method's return type (spec 25.3).</summary>
    public static readonly DiagnosticDescriptor TestExpectationTypeMismatch =
        new("PC0070", DiagnosticSeverity.Error, "test expectation type mismatch");

    // ------------------------------------------------------- control flow

    /// <summary>An <c>if</c> or loop condition that is not a bool (spec 15.1).</summary>
    public static readonly DiagnosticDescriptor ConditionMustBeBool =
        new("PC0071", DiagnosticSeverity.Error, "condition must be bool");

    /// <summary>A <c>break</c> with no enclosing loop (spec 15.2).</summary>
    public static readonly DiagnosticDescriptor BreakOutsideALoop =
        new("PC0072", DiagnosticSeverity.Error, "'break' outside a loop");

    /// <summary>A <c>continue</c> with no enclosing loop (spec 15.2).</summary>
    public static readonly DiagnosticDescriptor ContinueOutsideALoop =
        new("PC0073", DiagnosticSeverity.Error, "'continue' outside a loop");

    // ------------------------------------------------------- types, conversions and presence

    /// <summary>A simple type name that matches more than one imported type (spec 12).</summary>
    public static readonly DiagnosticDescriptor AmbiguousTypeName =
        new("PC0074", DiagnosticSeverity.Error, "ambiguous type name");

    /// <summary>An <c>as</c> conversion the language does not define (spec 10.3).</summary>
    public static readonly DiagnosticDescriptor InvalidConversion =
        new("PC0075", DiagnosticSeverity.Error, "invalid conversion");

    /// <summary>A name that is not a value of the enum it is qualified by (spec 12).</summary>
    public static readonly DiagnosticDescriptor UnknownEnumValue =
        new("PC0076", DiagnosticSeverity.Error, "unknown enum value");

    /// <summary>Behavior extended onto a message that comes from the protobuf runtime (spec 21.2).</summary>
    public static readonly DiagnosticDescriptor ExtendingAWellKnownType =
        new("PC0077", DiagnosticSeverity.Warning, "extending a well-known type");

    /// <summary>A message field read where its presence cannot be established (spec 13.1).</summary>
    public static readonly DiagnosticDescriptor MessageFieldMayBeUnset =
        new("PC0078", DiagnosticSeverity.Error, "message field may be unset");

    /// <summary><c>has</c> on a field that protobuf gives no presence (spec 8.4).</summary>
    public static readonly DiagnosticDescriptor FieldHasNoPresence =
        new("PC0079", DiagnosticSeverity.Error, "field has no presence");

    /// <summary><c>has</c> on something that is not a protobuf field (spec 8.4).</summary>
    public static readonly DiagnosticDescriptor HasNeedsAField =
        new("PC0080", DiagnosticSeverity.Error, "'has' needs a field");

    // ------------------------------------------------------- limits and the toolchain

    /// <summary>A construct nested deeper than the parser will descend or build (spec 28).</summary>
    public static readonly DiagnosticDescriptor NestingIsTooDeep =
        new("PC0081", DiagnosticSeverity.Error, "nesting is too deep");

    /// <summary>An include path that is malformed rather than merely missing (spec 5.2).</summary>
    public static readonly DiagnosticDescriptor IncludePathCouldNotBeUsed =
        new("PC0082", DiagnosticSeverity.Error, "include path could not be used");

    /// <summary>protoc was still running when the compiler stopped waiting for it (spec 21.1).</summary>
    public static readonly DiagnosticDescriptor ProtocDidNotFinish =
        new("PC0083", DiagnosticSeverity.Error, "protoc did not finish");

    // ------------------------------------------------------- numeric literals

    /// <summary>
    /// A floating-point literal too large for the type it takes: for a double as soon as it is read,
    /// and for a float where it is used as one (spec 10.3).
    /// </summary>
    public static readonly DiagnosticDescriptor FloatingPointLiteralOutOfRange =
        new("PC0084", DiagnosticSeverity.Error, "floating-point literal out of range");

    // ------------------------------------------------------- bitwise operators

    /// <summary>
    /// <c>&amp;</c>, <c>|</c>, <c>^</c>, <c>&lt;&lt;</c> or <c>&gt;&gt;</c> with an operand that is not
    /// an integer, which for a shift includes its count (spec 9.2).
    /// </summary>
    public static readonly DiagnosticDescriptor BitwiseOperatorRequiresIntegerOperands =
        new("PC0085", DiagnosticSeverity.Error, "bitwise operator requires integer operands");

    /// <summary><c>~</c> applied to a value that is not an integer (spec 9.2).</summary>
    public static readonly DiagnosticDescriptor BitwiseNotRequiresAnIntegerOperand =
        new("PC0086", DiagnosticSeverity.Error, "bitwise not requires an integer operand");

    // ------------------------------------------------------- sources and schemas, continued

    /// <summary>
    /// An import resolved to a schema other than the different one of that path beside its own
    /// source, because another directory in the one search order held it first (spec 5.2).
    /// </summary>
    public static readonly DiagnosticDescriptor SchemaBesideSourceIsShadowed =
        new("PC0087", DiagnosticSeverity.Warning, "schema beside the source is shadowed");

    /// <summary>
    /// A method of a production source that calls a method a test source declares, which is
    /// generated with the tests and not with the program (spec 25.3.1).
    /// </summary>
    public static readonly DiagnosticDescriptor ProductionMethodCallsTestHelper =
        new("PC0088", DiagnosticSeverity.Error, "production method calls a test helper");

    /// <summary>
    /// Production behavior naming a type only schemas the test sources bring declare, which the
    /// production build never loads (spec 25.3.1).
    /// </summary>
    public static readonly DiagnosticDescriptor ProductionNamesATestOnlyType =
        new("PC0089", DiagnosticSeverity.Error, "production behavior names a test-only type");

    // ------------------------------------------------------- the configuration file

    /// <summary>An element <c>protocross.config.xml</c> has no setting for (spec 10.4).</summary>
    public static readonly DiagnosticDescriptor UnknownConfigurationElement =
        new("PC2001", DiagnosticSeverity.Error, "unknown configuration element");

    /// <summary>A configured value that is not one the setting accepts (spec 10.4).</summary>
    public static readonly DiagnosticDescriptor UnknownConfigurationValue =
        new("PC2002", DiagnosticSeverity.Error, "unknown configuration value");

    /// <summary>A configuration file that is missing, malformed, or not a ProtoCross one (spec 10.4).</summary>
    public static readonly DiagnosticDescriptor ConfigurationFileCouldNotBeRead =
        new("PC2003", DiagnosticSeverity.Error, "configuration file could not be read");

    /// <summary>A configuration file stating one setting more than once (spec 10.4).</summary>
    public static readonly DiagnosticDescriptor DuplicateConfigurationSetting =
        new("PC2004", DiagnosticSeverity.Error, "duplicate configuration setting");

    // ------------------------------------------------------- the driver

    /// <summary>
    /// Two sources of one compilation find different configuration files, or one finds a file and
    /// another none (spec 10.4).
    /// </summary>
    public static readonly DiagnosticDescriptor SourcesDisagreeOnPolicy =
        new("PC2005", DiagnosticSeverity.Error, "sources disagree on policy");

    /// <summary>
    /// Two sources of one compilation would be generated under the same names, or one source is
    /// given twice (spec 5.3).
    /// </summary>
    public static readonly DiagnosticDescriptor SourcesShareGeneratedNames =
        new("PC2006", DiagnosticSeverity.Error, "sources share generated names");

    // ------------------------------------------------------- the project file

    /// <summary>
    /// A project file that is missing, malformed, or not a ProtoCross one, or a directory its
    /// patterns search that could not be listed (spec 5.4).
    /// </summary>
    public static readonly DiagnosticDescriptor ProjectCouldNotBeRead =
        new("PC2007", DiagnosticSeverity.Error, "project could not be read");

    /// <summary>An element or attribute a project file has no meaning for (spec 5.4).</summary>
    public static readonly DiagnosticDescriptor UnknownProjectElement =
        new("PC2008", DiagnosticSeverity.Error, "unknown project element or attribute");

    /// <summary>
    /// A project element that is known but unusable: a pattern or a path that is missing, empty, or
    /// cannot be one, or a setting stated twice (spec 5.4).
    /// </summary>
    public static readonly DiagnosticDescriptor InvalidProjectSetting =
        new("PC2009", DiagnosticSeverity.Error, "invalid project setting");

    /// <summary>A <c>&lt;Sources&gt;</c> or <c>&lt;Tests&gt;</c> element that matches no source (spec 5.4).</summary>
    public static readonly DiagnosticDescriptor ProjectPatternMatchesNothing =
        new("PC2010", DiagnosticSeverity.Warning, "project pattern matches no source");
}
