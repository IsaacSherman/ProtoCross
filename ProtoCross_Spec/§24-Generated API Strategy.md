## 24. Generated API Strategy

This section captures backend-specific integration choices.

**Decided: a project's behavior is declared in a namespace the project owns, in every backend.** The
namespace is the project's name ([5.4](./§5-Source%20Organization.md#54-projects)), spelled as protoc
spells a package of that name: the behavior of `acme.billing.pcproj` is declared in `Acme.Billing` in
C# and in `acme::billing` in C++. Every receiver's behavior goes there alike, whether its message
comes from the project's own schemas, from a well-known type, or from a third party's package, so
there is no rule for telling those apart. A project named after the package of its own schemas
therefore declares its behavior beside their messages, and a consumer that imports their namespace
for the messages has the behavior too.

Sources compiled without a project are not a library, and have no name to own a namespace by. Their
behavior is declared beside each message it extends, in the namespace protoc declares that message
in, as it always has been.

A library that declares its symbols in a namespace someone else owns shares it with every other
library that does the same. Two libraries extending one message there declare the same names: in C#
two classes a consumer referencing both cannot tell apart, and in C++ two `inline` functions the
linker merges without a diagnostic, keeping one body for both. Protobuf's own extensions are placed
the way a project's behavior is: an `extend` is declared in the package of the file that writes it,
not the package of the message it extends.

### 24.1 C#

Potential strategies:

- Partial classes.
- Extension methods.
- Generated companion classes.
- Wrapper/adaptor classes.

**Decided for the current implementation: extension methods.** A project's are declared in one
static class, `ProtoCrossExtensions`, in the project's namespace, where every receiver's methods are
overloads told apart by the type of `this`. A consumer writes `using Acme.Billing;` and then
`order.LineTotalCents()`. Sources compiled without a project declare them in a
`{Message}ProtoCrossExtensions` static class per receiver, in the receiver's own namespace. This
imposes nothing on the protobuf codegen: the generated messages may live in a different assembly,
and nothing depends on their being partial. The extension class is `partial` itself, because it
belongs to the project or the receiver rather than to the source that extends it: two sources
extending one message each emit a part of it, and a consumer compiles both into one assembly. Each
source's part lists its methods by the message they extend. One class rather than one per receiver,
because the namespace is the project's rather than a schema's, and two messages of one name from
two packages would otherwise need two classes of one name in it. Method names are PascalCased to
match the C# protobuf generator, so `line_total_cents` becomes `LineTotalCents` and reads the same
as a hand-written member. A method whose name would then be the name of the class it is declared in,
which C# does not allow, has an underscore appended wherever it is declared or called, as protoc
appends one to a property named after its message: `proto_cross_extensions` is
`ProtoCrossExtensions_` in a project, and `timestamp_proto_cross_extensions` on `Timestamp` is
`TimestampProtoCrossExtensions_` beside it.

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

**A message's namespace is the one protoc declares for its file.** Every message and enum is
qualified with the namespace protoc's C# generator declares its file in, and a source compiled
without a project declares each extension class there too. That namespace is the file's
`csharp_namespace` option whenever the file sets it, even to the empty string, which is the global
namespace. Otherwise it is the file's package, converted by the same rule as a field name but in one
pass over the whole package, with each period kept and starting a new word: `acme.v1beta1` is
`Acme.V1Beta1`, `snake_case.pkg` is `SnakeCase.Pkg`, and a file with no package is in the global
namespace. A project's namespace is its name converted by the same rule, as though it were a
package: `acme.billing` is `Acme.Billing`. The underscore kept in front of a leading
digit is therefore kept only at the start of the package, so `_1x.acme` is `_1X.Acme` and `acme._1x`
is `Acme.1X`. That last is not a C# namespace, and protoc's own output for such a package does not
compile, but it is what protoc names, and no other spelling would name a namespace its classes are
declared in. Method names and extension classes are not converted this way: their rule only has to
agree with itself. The namespace rule is protoc's `GetFileNamespace`, and it is the same in protoc
31.1 and 33.4.

**A change is made through the public surface protoc already declares, so mutation needs nothing
else** ([18](./§18-Mutability.md#18-mutability)). A `mut fn` is an extension method like any other, and its receiver
is the message itself, since a C# message is a reference. A field is assigned through its property:
`self.Total = 5`. A message field written through is set first where it is unset,
`(self.Customer ??= new Customer()).Name = x`, which is what a C++ mutable accessor does: protoc gives
an unset message field as null, and setting a member of a `oneof` this way switches the case. The
target is reached before the value is evaluated, as C# evaluates any assignment. An append is `Add`
on the field's `RepeatedField`, reached the same way, `(self.Review ??= new Review()).Scores.Add(x)`,
and C# evaluates what `Add` is called on before its argument.

A message stored in a field, or appended to one, is `Clone`d unless it is a literal, as it is in a
literal's field ([13.2](./§13-Messages.md#132-message-construction)). A message or repeated value stored in a local is `Clone`d only in a method that
changes a message, and an append to a local counts: in any other, nothing can change through the local or through what it was copied from, so a
copy and a share cannot be told apart, and every method written before mutation is generated as it
was. A message passed to a `mut fn` that is a call's result, or read from one, is `Clone`d too,
because the call may have returned part of the very receiver being changed. For the same reason, in
a method that changes a message, a loop over a field of a call's result traverses a `Clone` of the
result, which is what C++ does by keeping the result in a local ([24.2](#242-c)).

Questions:

- Are generated protobuf C# classes safe to extend directly?
- ~~Should mutable methods require partial class integration?~~ Decided: no. Everything a change
  needs is public ([18](./§18-Mutability.md#18-mutability)).

### 24.2 C++

Potential strategies:

- Free functions.
- Generated helper namespaces.
- Protobuf insertion points.
- Wrapper/adaptor classes.

**Decided for the current implementation: header-only free functions**, taking the receiver as
`const T&`, or as `T&` for a `mut fn`, in the project's namespace, or in the message's own protobuf
namespace for sources compiled without a project. A consumer calls
`acme::billing::line_total_cents(order)`. This subclasses nothing, needs no protoc insertion points,
and behaves the same whether the protobuf codegen is regenerated or vendored. All declarations are
emitted before any definition so methods may call one another in any order. Every call a generated
function or test makes to another is qualified with the callee's namespace, so argument-dependent
lookup cannot find another library's function of the same name.

A project's namespace is its name spelled as protoc spells a package: `acme.billing` is
`acme::billing`, and a component on the keyword list below is escaped, so `acme.new` is
`acme::new_`. A project's header is guarded by a macro built from the project's name and the
header's own, so `pricing.pc.h` in `acme.billing` is guarded by
`PROTOCROSS_ACME_BILLING_PRICING_PC_H_`, and a translation unit can include the `pricing.pc.h` of two
libraries. Outside a project it is `PROTOCROSS_PRICING_PC_H_`.

Every source of a compilation ([5.3](./§5-Source%20Organization.md#53-compilation-unit)) is generated
into a header of its own, and a method may call one another source declares. A header whose methods
do includes each such source's header after its own declarations and before its definitions. Two
sources may therefore call each other, and whichever of their headers a translation unit includes
first, the other's definitions find the functions they call already declared, while the include
guard stops the inclusion going round again. A header that calls no other source includes none, and
is laid out as it always was. A generated test driver includes the header of each source whose
methods its tests call. That is the method a test targets, and any method its fixture, arguments or
expectation call on a literal.

A header includes the protobuf header of each schema its methods' receivers are declared in. Each of
those includes the header of every schema its own schema imports. Any other schema a header names a
type from also gets its header included: through a literal, a parameter, a return value, a local or
an enum value. So does any such schema a test driver names beyond its targets' schemas. A schema
that is already declared by those includes is not included a second time.

**A `mut fn` takes its receiver as `T&`; every other receiver, and every parameter, stays `const`**
([18](./§18-Mutability.md#18-mutability)). A method that changes nothing keeps the signature it always had, so a caller holding a
const message can call it. The free-function shape needs nothing else: a mutating method is handed
the message to change, as `self`, as a local, or through the mutable accessors,
`touch(*self.mutable_last())`.

A change goes through protoc's accessors. A scalar, a string or an enum is set with `set_x()`, whose
call evaluates what it is called on before its argument, so the target is reached before the value
and the field is set after it, the order spec 9.3 gives. A field written through is reached with
`mutable_x()`, which sets it when it is unset and switches a `oneof`'s case. A message field has no
setter, and `mutable_x()` sets the field as it returns it, so it is assigned with `=`, whose value
C++17 evaluates before its target: `*self.mutable_last() = T(value);`. When the target has links,
the message they reach is bound first, `auto& owner = *self.mutable_audit();`, and the field is
assigned through it in the next statement, inside a block of their own. The value is copied before
anything is set: a message as `T(value)`, and a string or bytes read from a field as
`std::string(value)`. Either may be inside what the assignment replaces or unsets, as `node =
node.next` and `after = before` between members of one `oneof` are, and protobuf destroys that
before it copies. A literal is assigned as the temporary its lambda returns.

An append goes through protoc's `add_x`. A number, an enum, a string or bytes is added with
`add_x(value)`, which is called as a setter is, after its target and its argument. A message has only
the `add_x()` that adds an empty element and returns it, so it is assigned with `=`, as a message
field is: `*self.add_entries() = T(value);`, which evaluates the value first, and a target with links
is reached through `auto& owner` in a block of its own first, in the same way. A local holds a
`RepeatedField` or a `RepeatedPtrField` of its own, and is added to with `Add(value)`, or with
`*Add() = value` for an element held by pointer.

A loop whose body changes the element it is given, an append to it included, binds it as `auto&`,
over the field's mutable accessor; every other loop binds `const auto&` as it always has. protobuf
holds a repeated enum as `int`, so a loop over one binds an `int`, and each use of the element reads
it as the enum, `static_cast<E>(kind)`: nothing converts an `int` to the enum that a setter, `add_x`,
a parameter, a return value and a local of the enum's type all take.

**Fields are reached through the accessors protoc declares, spelled as protoc spells them.** protoc's
C++ generator derives one name per field and builds every accessor from it: the getter is `name()`,
and the presence test and the setters a message literal uses are `has_name()`, `set_name()`,
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

**A message literal is a lambda, called where it is written.** protobuf's C++ API has no
initializer syntax: a message is built by declaring one and calling its setters. So
`new Order { number: 1, featured: given }` is written as

```cpp
[&] {
  ::acme::Order message;
  message.set_number(1LL);
  *message.mutable_featured() = given;
  return message;
}()
```

The lambda is an expression, so the literal is evaluated exactly where it is written
([9.3](./§9-Expressions%20and%20Operators.md#93-evaluation-order)), and it returns the message by
value.
- A nested literal is built in place, through the pointer that `mutable_x()` or `add_x()`
  returns. One that sets nothing only asks for that pointer, which gives the field its presence.
- A message that is not a literal is assigned through the same pointer, and assignment copies it
  ([13.2](./§13-Messages.md#132-message-construction)).
- The lambda captures by reference, so the names it declares are chosen to differ from every name
  its literal reads.
- A literal that sets nothing is `T()`.
- A test's receiver fixture is written by the same writer, into the local `receiver`.

**A loop over a field of a temporary message keeps that message alive.** In
`for line in with_lines().lines`, the accessor returns a reference into a message that C++20
destroys before the loop's first iteration. So the message is held in a local declared in a block
around the loop, and the field is read from that local. A loop over anything else is written as it
always was.

Implementation Note:

- The rules have changed over protobuf's history, and the backend follows the current ones. protoc
  21.x checked a shorter keyword list, without `assert`, `char16_t`, `char32_t` or any keyword C++20
  added. It checked no generated member names, and it did not escape package components at all. Where
  an older header and this backend disagree, the header mostly fails to compile on its own, because
  the name it kept is a keyword or collides with a member or a macro. Not always, though. The
  keywords C++20 added, such as `char8_t`, `constinit`, `concept` and `requires`, are ordinary
  identifiers under C++17. `assert` is expanded only before `(`, so a top-level enum or an enum value
  of that name compiles, where a message does not, because its constructor puts `(` after the name. A
  top-level enum named after a generated member, such as `enum Swap`, collides with nothing, so protoc
  21.x kept it bare. C++ generated by protoc 21.x or earlier pairs with this backend only in a C++20
  build, which is what the generated test scaffold uses, and only for a schema with no top-level enum
  or enum value called `assert` and no top-level enum named after a generated member.

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
