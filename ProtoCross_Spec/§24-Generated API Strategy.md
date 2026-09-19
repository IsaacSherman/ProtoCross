## 24. Generated API Strategy

This section captures backend-specific integration choices.

### 24.1 C#

Potential strategies:

- Partial classes.
- Extension methods.
- Generated companion classes.
- Wrapper/adaptor classes.

**Decided for the current implementation: extension methods**, in a
`{Message}ProtoCrossExtensions` static class per receiver. This imposes nothing on the protobuf
codegen: the generated messages may live in a different assembly, and nothing depends on their
being partial. Method names are PascalCased to match the C# protobuf generator, so
`line_total_cents` becomes `LineTotalCents` and reads the same as a hand-written member.

**Fields are reached through the properties protoc declares, spelled as protoc spells them.** protoc's
C# generator derives one property name per field: the field is read as `Name`, a test fixture
initializes `Name`, and a scalar with explicit presence is tested with `HasName`. That name is the
field's name PascalCased: the first letter, every letter after an underscore and every letter after
a digit are capitalized, the underscores are dropped, and every other character keeps its case, except
that an underscore is kept in front of a name that would otherwise begin with a digit. An underscore
is then appended when the result is the name of the message that declares the field, or one of the
members every generated message declares or overrides: `Types`, `Descriptor`, `Equals`, `ToString`,
`GetHashCode`, `WriteTo`, `Clone`, `CalculateSize`, `MergeFrom`, `OnConstruction` and `Parser`. So
`field1a` is `Field1A`, `_1st` is `_1St`, a field `descriptor` is read as `Descriptor_` and tested
with `HasDescriptor_`, and a field `probe` in a message `Probe` is `Probe_`. Both checks see the
PascalCased result, so a field `Descriptor` is escaped as well, and inherited members that are not on
the list, such as `GetType`, are not escaped at all.

A group is named after its type rather than its field: `group MyGroup` declares a field `mygroup` and
a property `MyGroup`. An editions field with delimited encoding is named the same way when it has a
group's shape, meaning it is named for its message type lowercased and the type is declared in the
same scope as the field. Any other delimited field is named after the field.

Keywords need nothing, because every C# keyword is lowercase and a PascalCased name never spells one:
`class` is `Class`. The rule is protoc's `GetPropertyName`, reproduced rather than approximated for
the same reason as the C++ rule in [24.2](#242-c), and it is the same in protoc 31.1 and 33.4.

This choice may need revisiting if mutation ([18](./§18-Mutability.md#18-mutability)) is allowed, since extension methods cannot access
anything the public surface does not already expose.

Questions:

- Are generated protobuf C# classes safe to extend directly?
- Should mutable methods require partial class integration?
- How should virtual behavior be represented?

### 24.2 C++

Potential strategies:

- Free functions.
- Generated helper namespaces.
- Protobuf insertion points.
- Wrapper/adaptor classes.
- Policy-based override hooks.

**Decided for the current implementation: header-only free functions** in the message's own
protobuf namespace, taking the receiver as `const T&`. This subclasses nothing, needs no protoc
insertion points, and behaves the same whether the protobuf codegen is regenerated or vendored.
All declarations are emitted before any definition so methods may call one another in any order.

Const-correctness follows from the read-only method model: every receiver is `const T&` and every
message-typed parameter is `const T&`. If mutation ([18](./§18-Mutability.md#18-mutability)) is allowed, that decision has to be
revisited along with the free-function shape.

**Fields are reached through the accessors protoc declares, spelled as protoc spells them.** protoc's
C++ generator derives one name per field and builds every accessor from it: the getter is `name()`,
and the presence test and the setters a test fixture uses are `has_name()`, `set_name()`,
`mutable_name()` and `add_name()`. That name is the field's name lowercased, with an underscore
appended when the result is on protoc's list of C++ keywords and macros (`class`, `new`, `assert`) or
names a nullary member every generated message declares (`descriptor`, `default_instance`,
`unknown_fields`, `mutable_unknown_fields`). So a field `class` is read with `class_()` and written
with `set_class_()`, since the prefix is added to the escaped name rather than restoring the bare one,
and a field `Friend` is read with `friend_()`. The one exception is a message that sets
`no_standard_descriptor_accessor`, which has no `descriptor()` of its own and keeps a field of that
name bare.

The rule is protoc's `FieldName`, reproduced rather than approximated, because any other spelling
names an accessor that does not exist and fails in the consumer's build rather than in this compiler.
It is the same in protoc 31.1 and 33.4. The backend escapes the names it chooses itself -- methods,
parameters and locals -- against the same keyword list, so a name protoc will not use as an accessor
is not used as an identifier either.

Implementation Note:

- The rule has changed over protobuf's history, and the backend follows the current one. protoc 21.x
  escaped keywords only, from a list without `assert`, `char8_t` or `constinit`, and no generated
  member names. Where the two disagree, the older header mostly fails to compile on its own, because
  the field collides with a member or a macro. The exceptions are `char8_t` and `constinit` under
  C++17, where they are not keywords, so C++ generated by protoc 21.x or earlier pairs with this
  backend only in a C++20 build, which is what the generated test scaffold uses.

Questions:

- Should generated methods be added to message classes when insertion points are available?
- What is the ABI compatibility strategy? Header-only inline functions sidestep this for now, at
  the cost of recompiling consumers on every regeneration.

### 24.3 Python

No Python backend exists in the current implementation.

Potential strategies:

- Helper functions.
- Monkey-patched methods.
- Mixins.
- Wrapper classes.

Questions:

- Should generated Python behavior modify generated protobuf classes at import time?
- Should wrappers be preferred for predictability?
- How should type hints be generated?

### 24.4 Future Backends

Future backends must document:

- Type mappings.
- Numeric behavior.
- Presence behavior.
- Collection behavior.
- Error handling mapping.
- Generated API shape.
- Unsupported features.
