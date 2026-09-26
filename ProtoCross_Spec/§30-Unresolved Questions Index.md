## 30. Unresolved Questions Index

This section should be maintained as the authoritative list of open decisions.

- ~~File extension.~~ Decided: `.pcross` ([5.1](./§5-Source%20Organization.md#51-files)).
- ~~Direct import model.~~ Decided: `import proto "file.proto";` resolves `.proto` files through
  include paths and the source directory ([5.2](./§5-Source%20Organization.md#52-relationship-to-proto)). Descriptor-set input remains open.
- ~~Compilation units of more than one file.~~ Decided: a compilation is one or more sources bound as
  one program, each generated into files of its own, under one policy ([5.3](./§5-Source%20Organization.md#53-compilation-unit)).
  A project names its sources, and which of them hold tests, in a `.pcproj` ([5.4](./§5-Source%20Organization.md#54-projects)).
- ~~Package and namespace model for current source.~~ Decided: no independent ProtoCross package
  declaration; names come from protobuf descriptors ([5.2](./§5-Source%20Organization.md#52-relationship-to-proto)). Future embedded-in-proto design remains
  open.
- ~~Semicolon requirement.~~ Decided: semicolons are mandatory for declarations/statements listed
  in 7.1.
- ~~Type inference policy.~~ Decided: local variables may state an explicit type or infer from the
  initializer ([7.1](./§7-Grammar%20and%20Syntax.md#71-implemented-grammar), [8](./§8-Type%20System.md#8-type-system)).
- Helper functions and whether top-level functions belong in the language.
- Math intrinsics such as `abs`, `min` and `max`. **Post-1.0.** Until then each is written with a
  comparison ([9.1](./§9-Expressions%20and%20Operators.md#91-expression-categories)).
- ~~Complete scalar type support.~~ Decided: all protobuf scalar spellings map into the supported
  ProtoCross value domains ([8.2](./§8-Type%20System.md#82-protobuf-scalar-mapping)).
- 8- and 16-bit integer types. **Post-1.0.** Protobuf has no scalar of either width, so no field
  could hold one ([8.2](./§8-Type%20System.md#82-protobuf-scalar-mapping)).
- Decimal support.
- ~~Nullability and presence syntax.~~ Decided: `has <field>`, with the field's own presence
  rules taken from the protobuf descriptor ([8.4](./§8-Type%20System.md#84-nullability-and-presence)).
- ~~What reading an unset message field means.~~ Decided: it requires an established presence
  test, so the two backends have nothing to disagree about ([13.1](./§13-Messages.md#131-field-access)).
- ~~Which protobuf syntax versions are supported.~~ Decided: proto2, proto3, and editions, with
  no version check in the compiler ([21.3](./§21-Interoperability%20With%20Protobuf.md#213-protobuf-editions-and-syntax-versions)).
- ~~Whether arithmetic behavior is selectable per project.~~ Decided: `protocross.config.xml`,
  with the file winning over command-line flags ([10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy)).
- ~~What a repository may configure in an untrusted workspace.~~ Decided: a host withholds every
  setting that could run a program, today `protocross.protocPath`, from workspace and folder scope until
  the user trusts the workspace, and keeps serving everything else ([10.4.1](./§10-Numeric%20Semantics.md#1041-host-configuration)).
- ~~Boolean operator spelling.~~ Decided: both word and symbolic forms are accepted ([9.2](./§9-Expressions%20and%20Operators.md#92-operators)).
- ~~Assignment expression vs statement.~~ Decided: assignment is statement-only ([9.2](./§9-Expressions%20and%20Operators.md#92-operators)).
- ~~Bitwise and shift operators.~~ Decided: `&`, `|`, `^`, `~`, `<<` and `>>` on integers only, with a
  shift count of any integer type ([9.2](./§9-Expressions%20and%20Operators.md#92-operators)), whose low bits are used, and no
  overflow policy governing any of them ([10.1](./§10-Numeric%20Semantics.md#101-integer-overflow)).
- ~~Operator precedence.~~ Decided: the C-family order, C#'s and C++'s ([9.2](./§9-Expressions%20and%20Operators.md#92-operators)).
- Evaluation order details for non-short-circuit binary operators.
- ~~Integer overflow model.~~ Decided: wrapping ([10.1](./§10-Numeric%20Semantics.md#101-integer-overflow)).
- ~~Division and modulo by zero.~~ Decided: mandatory `on_zero` clause, with `fail` for the case
  where no substitute value is correct ([10.2.1](./§10-Numeric%20Semantics.md#1021-the-on_zero-clause)). `Result` ([19](./§19-Error%20Handling%20Without%20Exceptions.md#19-error-handling-without-exceptions)) is explicitly deferred, not blocked.
- ~~Explicit cast syntax.~~ Decided: `x as int64`, numeric scalars only ([10.3](./§10-Numeric%20Semantics.md#103-numeric-conversions)).
- ~~Numeric conversion rules.~~ Decided: integer targets wrap, floating point to integer
  truncates and saturates with NaN mapping to zero ([10.3](./§10-Numeric%20Semantics.md#103-numeric-conversions)).
- ~~Numeric literal forms.~~ Decided: `0x` and `0b` integers, `_` between digits, exponents, and
  `__INF` and `__NAN`, with no type suffixes ([6.6](./§6-Lexical%20Structure.md#66-numeric-literals)); a `-` written on an integer literal is
  part of it, and a literal with no expected type is `int64` or else `uint64` ([10.3](./§10-Numeric%20Semantics.md#103-numeric-conversions)).
  Whether there is a `bytes` literal remains open ([8.2](./§8-Type%20System.md#82-protobuf-scalar-mapping)).
- String indexing and comparison semantics.
- ~~How protobuf enum values are referenced.~~ Decided: `EnumType.VALUE_NAME` ([12](./§12-Enums.md#12-enums)).
- Enum unknown-value behavior, and whether an enum converts to or from an integer.
- Message construction support.
- Message equality semantics.
- ~~Repeated field mutation rules for current implementation.~~ Decided: no repeated mutation;
  only locals can be assigned ([14](./§14-Repeated%20Fields%20and%20Collections.md#14-repeated-fields-and-collections), [18](./§18-Mutability.md#18-mutability)). Future mutation syntax remains open.
- Map support and map iteration order.
- Reading protobuf extensions. **Post-1.0.** Until then an extension is not a field of any message,
  and no name reaches one ([13.4](./§13-Messages.md#134-extensions)).
- Switch support.
- ~~Method overloading.~~ Decided: not supported ([16.1](./§16-Methods.md#161-method-attachment)).
- Receiver mutation and possible const/mut method split.
- ~~Virtual method inclusion in version 1.~~ Decided: no virtual methods; `virtual` is an ordinary
  identifier ([17](./§17-Virtual%20and%20Override%20Semantics.md#17-virtual-and-override-semantics)).
- ~~Portable override registration model.~~ Decided: there is no overriding, so there is nothing to
  register ([17](./§17-Virtual%20and%20Override%20Semantics.md#17-virtual-and-override-semantics)).
- Error result model.
- ~~External function support.~~ Decided: hard no for current language; methods call only ProtoCross
  methods ([20](./§20-I-O,%20Threading,%20and%20Side%20Effects.md#20-io-threading-and-side-effects)).
- `protoc` plugin and Buf integration strategy.
- ~~ProtoCross unit test declaration syntax.~~ Decided: `test` declarations in any `.pcross` file
  ([25.3](./§25-Testing%20and%20Conformance%20Vectors.md#253-author-written-protocross-unit-tests)).
  ~~Separate `.pcrosstest` files.~~ Decided: there are none; the sources a project names only in
  `<Tests>` are its test sources
  ([25.3.1](./§25-Testing%20and%20Conformance%20Vectors.md#2531-test-sources-and-the-two-builds)).
- ~~Whether a test build should refuse a production source that names a type only a test source
  imports.~~ Decided: yes; production behavior names only types in the production schema closure
  ([25.3.1](./§25-Testing%20and%20Conformance%20Vectors.md#2531-test-sources-and-the-two-builds)).
- Generated test output framework options and future `protoc` plugin flag names.
- Stable IR format.
- Third-party backend support.
- ~~Generated API shape for implemented backends.~~ Decided: C# extension methods and C++ header-only
  free functions ([24](./§24-Generated%20API%20Strategy.md#24-generated-api-strategy)). Python remains open because no backend exists.
- Diagnostic compatibility.
- Language version declaration.
- Generated API compatibility.
- ~~Recursion and resource limits.~~ Decided for the compiler: nesting is bounded at 128 levels,
  and a chain counts one level per link
  ([28](./§28-Security%20and%20Determinism.md#28-security-and-determinism)). Runtime limits on
  generated methods, and whether recursion is allowed, remain open.
- Partial semantic model policy after descriptor-load failures and unresolved imports. Current
  implementation does not bind without usable descriptors; whether to produce a lighter semantic
  model for unresolved imports remains open.
