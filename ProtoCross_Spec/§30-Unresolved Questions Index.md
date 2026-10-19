## 30. Unresolved Questions Index

Every question this specification leaves open is asked here, and only here. A section that depends
on one links to its entry under the section's "Open Questions" label, and the entry links back to
each section that does, so a question has one wording, and its answer one place to go.

Each entry is numbered after the section that owns its subject, so 30.9.6 is a question about
[9](./§9-Expressions%20and%20Operators.md#9-expressions-and-operators). An entry is never renumbered
or removed, so a link to one keeps working:

- An open entry asks its question, with what is already known about it.
- A deferred one says **Post-1.0.**
- A decided one is struck through, says what was decided, and names the section that now says it.
  [31](./§31-Decision%20Log.md) has the dated row and the rationale.

### 30.5 Source Organization

#### 30.5.1 ~~File extension~~

Decided: `.pcross` ([5.1](./§5-Source%20Organization.md#51-files)).

#### 30.5.2 ~~Direct import model~~

Decided: `import proto "file.proto";` resolves `.proto` files through include paths and the source
directory ([5.2](./§5-Source%20Organization.md#52-relationship-to-proto)). Descriptor-set input
remains open ([30.21.3](#30213-descriptor-set-input)).

#### 30.5.3 ~~Compilation units of more than one file~~

Decided: a compilation is one or more sources bound as one program, each generated into files of its
own, under one policy ([5.3](./§5-Source%20Organization.md#53-compilation-unit)). A project names
its sources, and which of them hold tests, in a `.pcproj`
([5.4](./§5-Source%20Organization.md#54-projects)).

#### 30.5.4 ~~Package and namespace model for current source~~

Decided: no independent ProtoCross package declaration; names come from protobuf descriptors
([5.2](./§5-Source%20Organization.md#52-relationship-to-proto)). Future embedded-in-proto design
remains open ([30.5.5](#3055-protocross-embedded-in-proto-files)).

#### 30.5.5 ProtoCross embedded in `.proto` files

Should ProtoCross ever be embedded directly in `.proto` files?

Asked in [5.2](./§5-Source%20Organization.md#52-relationship-to-proto).

### 30.6 Lexical Structure

#### 30.6.1 ~~Numeric literal forms~~

Decided: `0x` and `0b` integers, `_` between digits, exponents, and `__INF` and `__NAN`, with no
type suffixes ([6.6](./§6-Lexical%20Structure.md#66-numeric-literals)); a `-` written on an integer
literal is part of it, and a literal with no expected type is `int64` or else `uint64`
([10.3](./§10-Numeric%20Semantics.md#103-numeric-conversions)). Whether there is a `bytes` literal
remains open ([30.8.8](#3088-bytes-literals-and-operations)).

#### 30.6.2 ASCII-only identifiers

Should source names be restricted to ASCII before language stabilization to avoid backend-specific
identifier edge cases?

Asked in [6.3](./§6-Lexical%20Structure.md#63-identifiers).

#### 30.6.3 `enum` and `message` without syntax

`enum` and `message` are reserved by the lexer but do not yet have source syntax.

Asked in [6.4](./§6-Lexical%20Structure.md#64-keywords).

### 30.7 Grammar and Syntax

#### 30.7.1 ~~Semicolon requirement~~

Decided: semicolons are mandatory for declarations/statements listed in 7.1.

#### 30.7.2 Top-level functions

Should top-level helper functions be allowed in a later version?

Asked in [7.1](./§7-Grammar%20and%20Syntax.md#71-implemented-grammar), and
[30.9.5](#3095-math-intrinsics) and [30.16.1](#30161-helper-functions) refer to it.

#### 30.7.3 An exact grammar

Should [7](./§7-Grammar%20and%20Syntax.md#7-grammar-and-syntax)'s grammar be replaced with exact
EBNF generated from or checked against the parser?

Asked in [7.1](./§7-Grammar%20and%20Syntax.md#71-implemented-grammar).

### 30.8 Type System

#### 30.8.1 ~~Type inference policy~~

Decided: local variables may state an explicit type or infer from the initializer
([7.1](./§7-Grammar%20and%20Syntax.md#71-implemented-grammar), [8](./§8-Type%20System.md#8-type-system)).

#### 30.8.2 ~~Complete scalar type support~~

Decided: all protobuf scalar spellings map into the supported ProtoCross value domains
([8.2](./§8-Type%20System.md#82-protobuf-scalar-mapping)).

#### 30.8.3 8-bit and 16-bit integer types

8- and 16-bit integer types. **Post-1.0.** Protobuf has no scalar of either width, so no field could
hold one ([8.2](./§8-Type%20System.md#82-protobuf-scalar-mapping)).

#### 30.8.4 Decimal

Decimal support. Until it exists, decimal-like behavior is written over a protobuf message
([8.3](./§8-Type%20System.md#83-non-protobuf-types)).

#### 30.8.5 ~~Nullability and presence syntax~~

Decided: `has <field>`, with the field's own presence rules taken from the protobuf descriptor
([8.4](./§8-Type%20System.md#84-nullability-and-presence)).

#### 30.8.6 Type aliases

Should type aliases be allowed if they resolve only to protobuf scalar, enum, or message types?

Asked in [8.1](./§8-Type%20System.md#81-type-sources).

#### 30.8.7 Helper and result types

Should helper/result types ever be allowed, or must error handling also be represented using
protobuf-defined messages?

Asked in [8.1](./§8-Type%20System.md#81-type-sources). A `Result` a value can hold is
[30.19.2](#30192-a-result-a-value-can-hold).

#### 30.8.8 `bytes` literals and operations

Should version 1 add bytes literals or bytes-specific operations?

Asked in [8.2](./§8-Type%20System.md#82-protobuf-scalar-mapping).

#### 30.8.9 Standard message shapes

Should the standard library eventually provide recommended protobuf message shapes for common
non-scalar concepts such as money, fixed-scale decimal, dates, durations, or UUIDs?

Asked in [8.3](./§8-Type%20System.md#83-non-protobuf-types).

#### 30.8.10 `oneof` cases

- `oneof` has presence per case as well as per field, and nothing in
  [8.4](./§8-Type%20System.md#84-nullability-and-presence) addresses the case discriminator. No
  syntax, no IR node, no diagnostic.
- Oneof fields, which have a case discriminator [13.1](./§13-Messages.md#131-field-access)'s rules
  say nothing about.

Asked in [8.4](./§8-Type%20System.md#84-nullability-and-presence) and
[13.1](./§13-Messages.md#131-field-access). The feature matrix in
[23.1](./§23-Backend%20Conformance%20Requirements.md#231-backend-feature-matrix) waits on it.

### 30.9 Expressions and Operators

#### 30.9.1 ~~Boolean operator spelling~~

Decided: both word and symbolic forms are accepted
([9.2](./§9-Expressions%20and%20Operators.md#92-operators)).

#### 30.9.2 ~~Assignment expression vs statement~~

Decided: assignment is statement-only ([9.2](./§9-Expressions%20and%20Operators.md#92-operators)).

#### 30.9.3 ~~Bitwise and shift operators~~

Decided: `&`, `|`, `^`, `~`, `<<` and `>>` on integers only, with a shift count of any integer type
([9.2](./§9-Expressions%20and%20Operators.md#92-operators)), whose low bits are used, and no overflow
policy governing any of them ([10.1](./§10-Numeric%20Semantics.md#101-integer-overflow)).

#### 30.9.4 ~~Operator precedence~~

Decided: the C-family order, C#'s and C++'s ([9.2](./§9-Expressions%20and%20Operators.md#92-operators)).

#### 30.9.5 Math intrinsics

Math intrinsics such as `abs`, `min` and `max`. **Post-1.0.** Until then each is written with a
comparison ([9.1](./§9-Expressions%20and%20Operators.md#91-expression-categories)). How an intrinsic
is named and found belongs with top-level functions ([30.7.2](#3072-top-level-functions)).

#### 30.9.6 ~~Operand order~~

Decided: every operand is evaluated left to right, in every backend
([9.3](./§9-Expressions%20and%20Operators.md#93-evaluation-order)).

### 30.10 Numeric Semantics

#### 30.10.1 ~~Whether arithmetic behavior is selectable per project~~

Decided: `protocross.config.xml`, with the file winning over command-line flags
([10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy)).

#### 30.10.2 ~~What a repository may configure in an untrusted workspace~~

Decided: a host withholds every setting that could run a program, today `protocross.protocPath`,
from workspace and folder scope until the user trusts the workspace, and keeps serving everything
else ([10.4.1](./§10-Numeric%20Semantics.md#1041-host-configuration)).

#### 30.10.3 ~~Integer overflow model~~

Decided: wrapping ([10.1](./§10-Numeric%20Semantics.md#101-integer-overflow)).

#### 30.10.4 ~~Division and modulo by zero~~

Decided: mandatory `on_zero` clause, with `fail` for the case where no substitute value is correct
([10.2.1](./§10-Numeric%20Semantics.md#1021-the-on_zero-clause)). `Result`
([30.19.2](#30192-a-result-a-value-can-hold)) is explicitly deferred, not blocked.

#### 30.10.5 ~~Whether `on_zero fail` may end the process that hosts generated code (#16)~~

Decided: it may, unless the project forbids it with `Failure/Termination = Forbidden`, which makes
every construct that would end the program a compile error
([10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy),
[19](./§19-Error%20Handling%20Without%20Exceptions.md#19-error-handling-without-exceptions)). A
method that must not end it refuses instead, `on_zero refuse value`
([16.3.1](./§16-Methods.md#1631-refusals)).

#### 30.10.6 ~~Explicit cast syntax~~

Decided: `x as int64`, numeric scalars only ([10.3](./§10-Numeric%20Semantics.md#103-numeric-conversions)).

#### 30.10.7 ~~Numeric conversion rules~~

Decided: integer targets wrap, floating point to integer truncates and saturates with NaN mapping to
zero ([10.3](./§10-Numeric%20Semantics.md#103-numeric-conversions)).

#### 30.10.8 Policy narrower than a project

Whether a non-default behavior should also be declarable per file, per method, or per expression,
rather than only per project. The typed IR already carries a per-operation annotation, so the
plumbing exists; what is missing is a syntax worth having.

Asked in [10.1](./§10-Numeric%20Semantics.md#101-integer-overflow).

#### 30.10.9 A divisor known to be zero

Should division by zero be a compile-time error when the divisor is a statically known zero
expression rather than a literal? Today only literal `0` is caught, and only because it fails the
proven-non-zero test rather than by any dedicated analysis.

Asked in [10.2.1](./§10-Numeric%20Semantics.md#1021-the-on_zero-clause).

#### 30.10.10 Division in a Python backend

Python's `/` produces a float and `//` floors, so neither maps to truncating division, and Python
raises on float division by zero rather than yielding `inf`. The Python backend will need explicit
helpers for both.

Asked in [10.2.1](./§10-Numeric%20Semantics.md#1021-the-on_zero-clause). Python's generated API is
[30.21.4](#30214-pythons-generated-api).

#### 30.10.11 A checked conversion

Whether a second conversion behavior is worth having. A checked conversion -- terminating rather
than clamping, matching 10.1's `Checked` -- is the obvious candidate, and nothing in
[10.3](./§10-Numeric%20Semantics.md#103-numeric-conversions) rules it out.

Asked in [10.3](./§10-Numeric%20Semantics.md#103-numeric-conversions).

#### 30.10.12 A backend's own settings

Whether a backend may add settings of its own, and if so how a third-party backend's settings avoid
colliding with the language's.

Asked in [10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy). Third-party backends are
[30.22.2](#30222-third-party-backends).

### 30.11 Strings

#### 30.11.1 String indexing

Should string indexing be supported at all?

Asked in [11.1](./§11-Strings.md#111-string-model).

#### 30.11.2 String normalization

Should normalization be specified?

Asked in [11.1](./§11-Strings.md#111-string-model).

#### 30.11.3 String equality

Should string equality be specified in terms of Unicode scalar values, UTF-16 code units, or a
protobuf-runtime guarantee?

Asked in [11.1](./§11-Strings.md#111-string-model).

#### 30.11.4 Ordered string comparison

Should string comparison beyond equality be supported?

Asked in [11.1](./§11-Strings.md#111-string-model).

### 30.12 Enums

#### 30.12.1 ~~How protobuf enum values are referenced~~

Decided: `EnumType.VALUE_NAME` ([12](./§12-Enums.md#12-enums)).

#### 30.12.2 ~~Enum unknown-value behavior, and whether an enum converts to or from an integer~~

Decided: an enum converts to and from `int32` with `as`. A number the enum does not name is kept by
an open enum, with a note, and ends the program for a closed one, with a warning, unless an
`on_unknown` clause or the project's `<Enums>` says otherwise
([12.1](./§12-Enums.md#121-an-enums-number),
[10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy)). `value in Enum` asks whether a value
has a name ([12.2](./§12-Enums.md#122-whether-a-value-has-a-name)).

### 30.13 Messages

#### 30.13.1 ~~What reading an unset message field means~~

Decided: it requires an established presence test, so the two backends have nothing to disagree
about ([13.1](./§13-Messages.md#131-field-access)).

#### 30.13.2 ~~Message construction support~~

Decided: a literal, `new T { field: value, … }`, which is an expression wherever one may be written
and is how a fixture's fields are written too; its values evaluate in the order written, and storing
a message that is not a literal stores a copy ([13.2](./§13-Messages.md#132-message-construction),
[9.3](./§9-Expressions%20and%20Operators.md#93-evaluation-order)). ~~Whether a repeated field may be
given a whole repeated value, `items: other.items`.~~ Not for now: only a list, and accepting one
later breaks nothing ([13.2](./§13-Messages.md#132-message-construction)).

#### 30.13.3 Message equality

Message equality semantics. Until they are decided, `==` and `!=` refuse two messages and two
repeated values ([13.3](./§13-Messages.md#133-equality)). A test compares an expected message literal
by the fields it sets
([25.3](./§25-Testing%20and%20Conformance%20Vectors.md#253-author-written-protocross-unit-tests)),
which leaves `==` undecided.

- Does message equality mean identity, field-wise equality, or backend-defined equality?
- Is deep equality part of version 1?
- Should a comparison of identity exist, spelled apart from `==` (`===`, say)?

Asked in [13.3](./§13-Messages.md#133-equality). A map of messages has equality when a message does
([14.2](./§14-Repeated%20Fields%20and%20Collections.md#142-maps)).

#### 30.13.4 Reading extensions

Reading protobuf extensions. **Post-1.0.** Until then an extension is not a field of any message,
and no name reaches one ([13.4](./§13-Messages.md#134-extensions)).

Reading an extension needs its own syntax, because an extension is named by its full name on the
message it *extends*. Protobuf's text format writes that as `[pkg.Host.scoped]` and its option syntax
as `(pkg.Host.scoped)`. It also needs presence and repeated rules, and `GetExtension` in both
backends.

### 30.14 Repeated Fields and Collections

#### 30.14.1 ~~Repeated field mutation rules for current implementation~~

Decided: an element changes through the name a `for` binds; a repeated field cannot be assigned,
cleared or have an element removed; an element is appended with `place.append(value);`, to a field
or to a local holding a repeated value; and nothing inside a `for` may change the field it traverses,
an append to it included
([14.1](./§14-Repeated%20Fields%20and%20Collections.md#141-supported-operations),
[18](./§18-Mutability.md#18-mutability)).

#### 30.14.2 ~~Map support and map iteration order~~

Decided: maps are read and changed by key, and never iterated
([14.2](./§14-Repeated%20Fields%20and%20Collections.md#142-maps)).

#### 30.14.3 Collection helpers

Should filtering, mapping, sorting, or aggregation helpers exist? `No. Basics only. ~IS`

Asked in [14.1](./§14-Repeated%20Fields%20and%20Collections.md#141-supported-operations).

#### 30.14.4 Bounds-checked indexing

Should collection indexing be bounds-checked with explicit error results if indexing is added?
`Ugh... probably. I really want to say no, but... I have a feeling that not doing this could lead to security concerns in some language or another. ~IS`

Asked in [14.1](./§14-Repeated%20Fields%20and%20Collections.md#141-supported-operations).

#### 30.14.5 A map built from repeated entries

A repeated field of entries made into a map, the last of a key winning, may be added later.

Asked in [14.2](./§14-Repeated%20Fields%20and%20Collections.md#142-maps).

### 30.15 Control Flow

#### 30.15.1 ~~Switch support~~

Decided: a statement over an integer or an enum, with braced arms listing constants that never fall
into the next, and an optional `default` arm written last. A `break` leaves the innermost switch or
loop, and no switch is checked for exhaustiveness ([15.3](./§15-Control%20Flow.md#153-switch),
[15.2](./§15-Control%20Flow.md#152-loops)).

### 30.16 Methods

#### 30.16.1 ~~Helper functions~~

Decided: a factory's `helper fn`, which only that factory may call, is the only helper, and there are
no access modifiers ([16.3](./§16-Methods.md#163-factories)). Whether top-level functions belong in
the language remains open ([30.7.2](#3072-top-level-functions)).

#### 30.16.2 ~~Where a factory lives, and what it generates~~

Decided: a `factory Message { … }` block, whose methods build the message, are called as
`Message.factory.method(…)`, and are generated into a `MessageFactory` class in the namespace the
message's behavior goes in ([16.3](./§16-Methods.md#163-factories),
[24](./§24-Generated%20API%20Strategy.md#24-generated-api-strategy)). Injection and mocking are not
supported in 1.0.

#### 30.16.3 ~~Method overloading~~

Decided: not supported ([16.1](./§16-Methods.md#161-method-attachment)).

### 30.17 Virtual and Override Semantics

#### 30.17.1 ~~Virtual method inclusion in version 1~~

Decided: no virtual methods; `virtual` is an ordinary identifier
([17](./§17-Virtual%20and%20Override%20Semantics.md#17-virtual-and-override-semantics)).

#### 30.17.2 ~~Portable override registration model~~

Decided: there is no overriding, so there is nothing to register
([17](./§17-Virtual%20and%20Override%20Semantics.md#17-virtual-and-override-semantics)).

### 30.18 Mutability

#### 30.18.1 ~~Receiver mutation and possible const/mut method split~~

Decided: a method declared `mut fn` may change its receiver, every other method is read-only, and
nothing may change a parameter ([18](./§18-Mutability.md#18-mutability),
[16.1](./§16-Methods.md#161-method-attachment)).

### 30.19 Error Handling Without Exceptions

#### 30.19.1 ~~Error result model~~

Decided: any method declares a refusal of the author's type after its return type, ends with
`refuse`, and every call answers it with a fallback, `fail`, or `refuse` to pass it on. A division,
a conversion to an enum and a lookup refuse with `on_zero refuse`, `on_unknown refuse` and
`on_missing refuse` ([16.3.1](./§16-Methods.md#1631-refusals)). Whether a value may hold a refusal
remains open ([30.19.2](#30192-a-result-a-value-can-hold)).

#### 30.19.2 A `Result` a value can hold

Does ProtoCross define a built-in `Result` type? `I think we should- it doesn't have to be used, but it should be there for ease of use. ~IS`
Note: a refusal ([16.3.1](./§16-Methods.md#1631-refusals)) is that `Result` at the one place it is
needed, a method's boundary, with the author's type for the error and no generics, and every method
may declare one. A `Result` a value can hold remains open.

Asked in [19](./§19-Error%20Handling%20Without%20Exceptions.md#19-error-handling-without-exceptions),
and in [16.3.1](./§16-Methods.md#1631-refusals) as whether a value may hold a refusal.

#### 30.19.3 Arithmetic errors in the type system

Are arithmetic errors expressible in the type system? `Yes, it should be a fairly expansive enum. ~IS`
Note: overflow is no longer one of them. Since 10.1 defines overflow as wrapping, it is a defined
result rather than a failure, and nothing about it needs to reach the type system.
Note: no built-in enum of them exists. A method that must not end the program for a zero divisor
refuses with its own type, `on_zero refuse value` ([16.3.1](./§16-Methods.md#1631-refusals)).
`on_zero fail` does not become a refusal by itself, since only the author can say what the refusal
is.

Asked in [19](./§19-Error%20Handling%20Without%20Exceptions.md#19-error-handling-without-exceptions).

#### 30.19.4 Total methods

Can methods be declared as total, meaning they cannot fail at runtime? `I lean toward no, but this might be a nice optimization at some point. ~IS`
Note: a project may declare all of its methods so, `Failure/Termination = Forbidden`
([10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy)). A `fails` marker on each method that
may end the program was considered and lost: it makes the capability visible but lets no host opt
out, and the project setting does both.

Asked in [19](./§19-Error%20Handling%20Without%20Exceptions.md#19-error-handling-without-exceptions).

#### 30.19.5 Error results in each target

How do target backends map error results idiomatically while preserving semantics?  `Error handling is heavily on the user to define; we'll provide the Result type with primitive error enums for primitive "exceptions" such as dividing by zero. More advanced stuff?  Users can (and should!) tailor that to their specific needs. ~IS`
Note: a refusal is mapped to each target's try pattern, a method that returns whether it built its
result and gives the result or the refusal through a parameter
([24.1](./§24-Generated%20API%20Strategy.md#241-c), [24.2](./§24-Generated%20API%20Strategy.md#242-c)).
The refusal's type is the author's, as this asks.

Asked in [19](./§19-Error%20Handling%20Without%20Exceptions.md#19-error-handling-without-exceptions).

### 30.20 I/O, Threading, and Side Effects

#### 30.20.1 ~~External function support~~

Decided: hard no for current language; methods call only ProtoCross methods
([20](./§20-I-O,%20Threading,%20and%20Side%20Effects.md#20-io-threading-and-side-effects)).

#### 30.20.2 Marking pure functions

Should deterministic pure helper functions be explicitly marked? `Seems unnecessary. ~IS`

Asked in [20](./§20-I-O,%20Threading,%20and%20Side%20Effects.md#20-io-threading-and-side-effects).

### 30.21 Interoperability With Protobuf

#### 30.21.1 ~~Which protobuf syntax versions are supported~~

Decided: proto2, proto3, and editions, with no version check in the compiler
([21.3](./§21-Interoperability%20With%20Protobuf.md#213-protobuf-editions-and-syntax-versions)).

#### 30.21.2 Running as a `protoc` plugin, and Buf

`protoc` plugin and Buf integration strategy.

- Should the compiler run as a `protoc` plugin?  `Eventually. I don't think it's necessary right away, but should happen before v1. ~IS`
- Should it also support Buf plugin workflows?
- What should the eventual `protoc` plugin flag names be?

Asked in [21.1](./§21-Interoperability%20With%20Protobuf.md#211-descriptor-input), and in
[25.3](./§25-Testing%20and%20Conformance%20Vectors.md#253-author-written-protocross-unit-tests) for
the flag names, which [30.25.3](#30253-generated-test-output-framework-options) left open.

#### 30.21.3 Descriptor-set input

- Should descriptor-set input exist in addition to direct `.proto` imports?
- Should descriptor-set input be accepted directly, and how would it report per-import diagnostics?

Asked in [5.2](./§5-Source%20Organization.md#52-relationship-to-proto) and
[21.1](./§21-Interoperability%20With%20Protobuf.md#211-descriptor-input).

#### 30.21.4 Python's generated API

Python and any future backend must define its generated API shape before it can be conforming.

Asked in [21.2](./§21-Interoperability%20With%20Protobuf.md#212-generated-code-integration).
Python's division is [30.10.10](#301010-division-in-a-python-backend).

#### 30.21.5 Future editions

Whether a future edition could change a rule this specification states, and how the compiler would
notice. Nothing in this specification reads edition-specific features other than through
`HasPresence`.

Asked in [21.3](./§21-Interoperability%20With%20Protobuf.md#213-protobuf-editions-and-syntax-versions).

#### 30.21.6 Target protobuf versions

Which protobuf versions the specification targets, which its [front page](./README.md) gives as TBD.

### 30.22 IR and Compiler Architecture

#### 30.22.1 A stable IR format

Stable IR format.

- Should the IR be serialized as JSON, protobuf, or an internal compiler structure?
- Should backends consume a stable IR format?

Asked in [22.3](./§22-IR%20and%20Compiler%20Architecture.md#223-what-a-compilation-answers).

#### 30.22.2 Third-party backends

Third-party backend support. Should third-party backends be supported in version 1?

Asked in [22.3](./§22-IR%20and%20Compiler%20Architecture.md#223-what-a-compilation-answers). A
backend's own settings are [30.10.12](#301012-a-backends-own-settings).

#### 30.22.3 A semantic model without descriptors

Partial semantic model policy after descriptor-load failures and unresolved imports. Current
implementation does not bind without usable descriptors; whether to produce a lighter semantic model
for unresolved imports remains open.

### 30.24 Generated API Strategy

#### 30.24.1 ~~Generated API shape for implemented backends~~

Decided: C# extension methods and C++ header-only free functions
([24](./§24-Generated%20API%20Strategy.md#24-generated-api-strategy)). Python remains open because
no backend exists ([30.21.4](#30214-pythons-generated-api)).

#### 30.24.2 ~~Which namespace generated behavior is declared in, and who owns it~~

Decided: a project's is declared in a namespace named after the project, whatever message it
extends, and behavior compiled without a project beside each message it extends
([24](./§24-Generated%20API%20Strategy.md#24-generated-api-strategy),
[5.4](./§5-Source%20Organization.md#54-projects)).

### 30.25 Testing and Conformance Vectors

#### 30.25.1 ~~ProtoCross unit test declaration syntax~~

Decided: `test` declarations in any `.pcross` file
([25.3](./§25-Testing%20and%20Conformance%20Vectors.md#253-author-written-protocross-unit-tests)).
~~Separate `.pcrosstest` files.~~ Decided: there are none; the sources a project names only in
`<Tests>` are its test sources
([25.3.1](./§25-Testing%20and%20Conformance%20Vectors.md#2531-test-sources-and-the-two-builds)).

#### 30.25.2 ~~Whether a test build should refuse a production source that names a type only a test source imports~~

Decided: yes; production behavior names only types in the production schema closure
([25.3.1](./§25-Testing%20and%20Conformance%20Vectors.md#2531-test-sources-and-the-two-builds)).

#### 30.25.3 ~~Generated test output framework options~~

Decided: a project's `<Scaffold>` or a flag names each test project and, for C#, its target
framework, and the test framework is not an option in 1.0
([25.3.2](./§25-Testing%20and%20Conformance%20Vectors.md#2532-test-projects)). Future `protoc`
plugin flag names remain open ([30.21.2](#30212-running-as-a-protoc-plugin-and-buf)).

#### 30.25.4 Embedding or copying fixtures

Should the compiler embed fixtures in generated source, copy fixture files beside the generated
tests, or support both?

Asked in [25.3](./§25-Testing%20and%20Conformance%20Vectors.md#253-author-written-protocross-unit-tests).

#### 30.25.5 Checking in generated tests

Should generated tests be stable enough to check in, or treated as build artifacts only?

Asked in [25.3](./§25-Testing%20and%20Conformance%20Vectors.md#253-author-written-protocross-unit-tests).

### 30.26 Diagnostics

#### 30.26.1 Diagnostic compatibility

Diagnostic compatibility.

- Should diagnostic codes be part of the compatibility contract?
- Warnings exist today, so which warnings are compatibility-stable and which remain advisory?

Asked in [26](./§26-Diagnostics.md#26-diagnostics).

### 30.27 Versioning and Compatibility

#### 30.27.1 Language version

Language version declaration.

- Is the version declaration required?
- Can a compilation unit mix language versions?
- Whether a language version belongs in `protocross.config.xml`
  ([10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy)) rather than in each source file.

The specification's [front page](./README.md) gives its own version as TBD.

Asked in [27.1](./§27-Versioning%20and%20Compatibility.md#271-language-versioning) and
[10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy).

#### 30.27.2 The compatibility policy

What the compatibility policy is. [27.2](./§27-Versioning%20and%20Compatibility.md#272-compatibility-policy)
lists the rules it has to state, for source, the IR, backends, the generated API and runtime
behavior, and a candidate policy, and decides none of them.

Asked in [27.2](./§27-Versioning%20and%20Compatibility.md#272-compatibility-policy).

#### 30.27.3 Generated API compatibility

Generated API compatibility. Is ProtoCross intended to preserve generated API compatibility across
compiler versions?

Asked in [27.2](./§27-Versioning%20and%20Compatibility.md#272-compatibility-policy).

### 30.28 Security and Determinism

#### 30.28.1 ~~Recursion and resource limits~~

Decided for the compiler: nesting is bounded at 128 levels, and a chain counts one level per link
([28](./§28-Security%20and%20Determinism.md#28-security-and-determinism)). Runtime limits on
generated methods, and whether recursion is allowed, remain open
([30.28.2](#30282-runtime-limits), [30.28.3](#30283-recursion)).

#### 30.28.2 Runtime limits

Should resource limits be specified for generated methods?

Asked in [28](./§28-Security%20and%20Determinism.md#28-security-and-determinism).

#### 30.28.3 Recursion

- Should recursion be allowed?
- Should the compiler reject potentially unbounded recursion?

Asked in [28](./§28-Security%20and%20Determinism.md#28-security-and-determinism).
