## 22. IR and Compiler Architecture

This section is implementation-facing, not source-language syntax.

### 22.1 Implemented Pipeline

```text
ProtoCross source
    -> lexer/parser with recovery
    -> syntax tree
    -> import resolution
    -> protobuf descriptor loading
    -> binder: descriptor binding, name resolution, and type checking
    -> typed IR
    -> backend code generation
    -> target-language source
    -> target-language tests/conformance
```

Pipeline gates:

- Configuration-file errors stop before lexing or binding.
- Parse errors do not stop binding. The parser recovers and the binder produces a partial semantic
  model where it has descriptors to bind against.
- Unusable include paths, zero imports, unwritten imports, unresolved imports, missing default
  descriptor loader, and descriptor-load failures stop before binding.
- `CompilationResult.Module` is the partial semantic model. `CompilationResult.EmittableModule` is
  non-null only when the module exists and there are no errors.

### 22.2 Typed IR Requirements

The IR preserves:

- Source locations for diagnostics.
- Declaration sites and stable symbol identities for ProtoCross-declared methods, parameters, locals,
  and loop bindings.
- Descriptor identities for protobuf fields, enum values, message types, and enum types.
- Resolved protobuf type references.
- **Every place a name was written, and which symbol it resolved to**, spanning the name alone rather
  than the construct around it. Recorded as the binder resolves, because that is the only point
  holding both the identity and the range of the name: a type reference resolves to a type and leaves
  no node behind, and the spans a node does carry are extents. A reference that did not resolve is
  not recorded; it refers to nothing.
- **What was in scope at each point of a method body**: one entry per name that entered a scope, with
  the range it can be written over and the offset it starts resolving from. Recorded where each name
  is declared, because whether a name won is decided there and nowhere else.
- Exact numeric operation kinds.
- **How each operation behaves, stamped where it was bound.** Arithmetic, integer division and
  explicit conversion each carry the behavior the project's policy
  ([10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy)) selected, so a backend emits what
  the project asked for and never consults a policy of its own. **Carrying an annotation is not
  being governed by one.** Every arithmetic node carries an overflow behavior and only integer
  arithmetic is governed by it; a shift, a bitwise operator, a comparison and anything in floating
  point carry one that decides nothing. A consumer asks the node whether the annotation applies
  before describing it, which is 10.4's rule, and the node answers rather than each consumer
  re-deriving it.
- **What a name identifies, as an identity that outlives the compilation.** Identity is the
  declaration and never the spelling: for anything ProtoCross declares it is the source the
  declaring name is in together with its offset, and for anything the schema declares it is the
  protobuf fully qualified name. So two locals of one name in sibling blocks are two symbols, one
  field reached bare and through a receiver is one, and the same text recompiled yields the same
  identities. Every source in one compilation carries a distinct identity of its own -- a path, or
  the name its caller gave an unsaved buffer -- because a declaration site is only unique within a
  source.
- **A literal's value in one stated representation**: a 64-bit signed integer for a signed type and
  a 64-bit unsigned one for an unsigned type, whatever the width; a double-precision value for both
  floating-point types, and for a `float` literal the value its decimal rounds to in one rounding;
  a boolean or a string for those types; and no value at all where none could be bound. A negative
  integer literal is one literal rather than a negation of a positive one
  ([6.6](./§6-Lexical%20Structure.md#66-numeric-literals)), which is what lets the minimum of a signed type
  be written.
- **What was written, and where what was not written would go.** A node standing for a construct
  nobody wrote spans the empty point where it would be written: the receiver fixture or the
  expectation a `test` is missing stands just inside that test's braces. A node standing for a
  construct written in part spans what was written and *ends* at that point, so a member access
  whose name has not been typed runs from its receiver to the empty point after the dot. Widening
  either over text the author did write makes it the innermost node at every offset of that text,
  and a position query there answers with a construct nobody wrote -- which is what once told a
  caret on a test's header that it stood in the receiver fixture that test was missing.
- Presence checks. `IrFieldPresence` carries the field descriptor rather than a lowered boolean,
  because the two targets spell the test in unrelated ways.
- Field access semantics.
- Local assignment intent. A compound assignment is not a node of its own: `x += y` is the
  assignment of `x + y` to `x`, whose operation reads the target at the target's own span. It is
  the one place two nodes share a span without one standing inside the other, and a position query
  there answers with the target, which comes first. Which form was written is the syntax tree's to
  say.
- Terminal-failure behavior for `on_zero fail`.
- Evaluation order.
- Error placeholder nodes and types so one failed bind does not necessarily suppress later useful
  diagnostics.

**Invariants a consumer may rely on.** The list above says what is kept; these say what is true of
all of it, which is what a backend or a host may write code against instead of checking. Each is
asserted over the conformance corpus rather than only stated here, because a promise nothing tests
is one the next change breaks silently.

- **Every node is somewhere, and inside what holds it.** A node carries a source range, and that
  range lies within the range of the node that holds it. So a caret's innermost node can be found by
  descending, and no node claims text belonging to something it is not part of. Two nodes may share
  one range without either standing inside the other -- one pair does, the target of a compound
  assignment and the read of it -- which is a question for the position rules in 22.3 and not for
  this one.
- **Every expression has a type, and an error type is the trace of an error.** A bind that failed
  produces a node of the error type rather than a guess or a hole, so a consumer never meets a typed
  node that is quietly wrong. Nothing else produces one: a compilation that reported no error holds
  no error-typed expression, which is what makes it safe for a backend -- handed a module only when
  the compilation succeeded -- never to ask about one.
- **Every reference is resolved or is not a reference.** A node naming something ProtoCross declares
  carries the identity of a declaration in the same module; a name that resolved to nothing binds to
  an error-typed node instead of a stand-in symbol, and is not recorded as a use. A consumer
  therefore never holds an identity that answers nothing.
- **Every construct is reachable by one walk.** Each node the compiler can produce is yielded by the
  walk of the module that holds it, so a sweep over the IR -- an editor's, a backend's, a test's --
  sees all of it. A construct added to the language without being added to the walk leaves every
  such sweep silently passing over it.

### 22.3 What a Compilation Answers

**Decided: the model is addressable by position and by identity, and a schema element is answerable
from either side of the file boundary.**

22.2 says what the IR keeps. This says what a caller may ask of it, because a host that cannot ask is
in the same position as one handed nothing. Every question here is answered by walking what the
binder already produced; none of it is cached, and a keystroke produces a new compilation and a new
set of answers over it.

Normative Requirements:

- **What is at this offset**, in the syntax tree and in the typed IR, with the chain of nodes above
  the answer and a correspondence between the two trees by span. Containment includes both ends, so a
  caret that has just finished typing a name still finds it.
- **What a bare identifier written here could mean**: the names in scope, with their types and
  declarations, and the receiver they are looked up against. Everything offered binds and nothing that
  binds is missing, which is what makes the answer safe to accept without re-checking.
- **A part nobody wrote answers only where it would be written.** 22.2's rule about recovered nodes,
  read from the caller's side: every offset is answered, and an offset over text the author did write
  is answered with something the author did write. A caret on the target of a `test` missing its
  receiver fixture stands in the test's header, so it is not inside a value-bearing part of that test
  and no bare name is looked up there.
- **Which symbol a name means, where it is declared, and everywhere it is used.** Identity is the
  declaration, never a spelling: two locals of one name in sibling blocks are two symbols, and one
  field reached bare and through a receiver is one.
- **Every name a file resolved, as one sequence in source order**, each spanning the name alone and
  saying what it resolved to and whether that use declared, read or wrote it. The same facts as the
  question above, asked of the file instead of of a symbol, because a caller describing the whole
  file -- classifying it ([6.5](./§6-Lexical%20Structure.md#65-source-classification)) -- would
  otherwise ask about a position once per name in it and scan the same answer each time.
- **Where a schema element is declared and what was written about it**, reachable both from the
  descriptor and from the identity the IR carries for it. The second is not a convenience: a name in
  type position leaves no IR node, so an identity is the only handle a caret there produces.
- A declaration answers with **two ranges** -- the whole construct and the name inside it -- on both
  sides of the file boundary, because an editor asks for both and derives neither. The name always
  lies inside the construct.
- **Absence is ordinary and is not an error.** A schema with no comments, a descriptor set built
  without source info, a well-known type `protoc` resolved from descriptors compiled into itself, a
  `.proto` that cannot be read, one edited since the descriptors were built: each yields a
  declaration with no site, or no documentation, or both. A caller that wants to navigate asks about
  the site and a caller that wants to explain asks about the documentation.

Open Questions:

- Should the IR be serialized as JSON, protobuf, or an internal compiler structure?
- Should backends consume a stable IR format?
- Should third-party backends be supported in version 1?
