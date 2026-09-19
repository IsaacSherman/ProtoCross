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

**Namespaces, types and enum values are spelled as protoc spells them too.** Each component of the
package is one C++ namespace, with an underscore appended when the component is on the keyword list,
so `acme.new.default` is `acme::new_::default_`. A component is never checked against member names:
a namespace called `Swap` collides with nothing and stays `Swap`.

A message's class is its name joined to its enclosing messages' with underscores, where the enclosing
part is the parent's class as this rule spells it. An underscore is then appended to the whole name
when it is on the keyword list or names a member every generated message declares: `New`, `Swap`,
`UnsafeArenaSwap`, `Clear`, `CopyFrom`, `MergeFrom`, `IsInitialized`, `GetDescriptor`,
`GetReflection`, `GetMetadata` or `default_instance`. So `New` is `New_` and a message nested in it
is `New__Inner`, keeping its parent's underscore. `thread.local` is `thread_local_`, a keyword only
once flattened, and `Plain.Swap` is `Plain_Swap`, which collides with nothing once flattened. A
top-level enum is escaped the same way as a message, so `Swap` is `Swap_`. A nested enum is its
parent's class, an underscore and its own name, with nothing appended: `New.Kind` is `New__Kind`.

An enum value's own name is escaped against the keyword list before any prefix is added. A top-level
enum's value `new` is `new_`, and the value `class` of `New.Kind` is `New__Kind_class_`, keeping an
underscore the prefixed name would not have needed. This is the same shape as `set_class_()`.

These are protoc's `Namespace`, `ClassName` and `EnumValueName`, reproduced for the same reason as
`FieldName`, and they are the same in 31.1 and 33.4.

Implementation Note:

- The rules have changed over protobuf's history, and the backend follows the current ones. protoc
  21.x checked a shorter keyword list, without `assert`, `char16_t`, `char32_t` or any keyword C++20
  added. It checked no generated member names, and it did not escape package components at all. Where
  an older header and this backend disagree, the header mostly fails to compile on its own, because
  the name it kept is a keyword or collides with a member or a macro. Not always, though. The
  keywords C++20 added, such as `char8_t`, `constinit`, `concept` and `requires`, are ordinary
  identifiers under C++17. `assert` is expanded only before `(`, so an enum value of that name
  compiles. A top-level enum named after a generated member, such as `enum Swap`, collides with
  nothing, so protoc 21.x kept it bare. C++ generated by protoc 21.x or earlier pairs with this backend
  only in a C++20 build, which is what the generated test scaffold uses, and only for a schema with no
  enum value called `assert` and no top-level enum named after a generated member.

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
