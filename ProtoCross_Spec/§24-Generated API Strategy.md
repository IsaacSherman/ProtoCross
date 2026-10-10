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

**A map is protoc's `MapField`, and what it does not say as the language does goes through
`ProtoCrossMaps` in the support file** ([14.2](./§14-Repeated%20Fields%20and%20Collections.md#142-maps)). A lookup with a fallback is
`ProtoCrossMaps.FindValue(map, key) ?? fallback`, or `FindReference` for a value that is not a C# value
type: each gives the value or null, so `??` evaluates the fallback only where the key is missing,
without a delegate or a local the call site would have to name. `on_missing fail` is
`FoundOrFail(map, key, name)`, which writes the field's protobuf name and ends the program as
`on_zero fail` does. Whether a key is there is `ContainsKey`, `count()` is `Count`, and a store is
`map[key] = value`, which C# evaluates in the language's order. A message written through at a key is
`ProtoCrossMaps.Entry(map, key)`, which puts a new one there first where the key is missing. A
compound store written through an element evaluates its value into a local first, since C# reaches an
assignment's place before its value, and the read must not find the message reaching put there.
`add_if_absent`, `replace_if_present` and `merge` are helpers of those names, and a merge of messages
copies each one. Two maps are compared by `AreEqual`, with overloads for `double` and `float` that
compare by `==`, because their `Equals`, which `MapField.Equals` uses, calls two NaNs equal. A literal's
entries are index initializers, `Prices = { ["a"] = 1L, }`, stored in the order written.

**A field of one of protobuf's wrapper types is the value the wrapper holds, because that is what
protoc declares.** protoc's C# generator gives a field whose type is a message from
`google/protobuf/wrappers.proto` (`Int64Value`, `StringValue` and the other seven) the type of the
value the wrapper holds rather than the message: `long?`, `string`, `ByteString`, nullable where it is a
struct, so that null is an unset field. A member of a `oneof` is the same, a repeated field is a
`RepeatedField<long?>`, and a map's values are `long?`. A repeated value or a map of wrappers held in a
local or a parameter is protoc's type too. The language keeps the message the schema declares, which
is what C++ holds, so C# changes a wrapper's shape where it moves between a field and a message:

- `has limit` is `self.Limit != null`, as for any message field.
- `limit.value` is `self.Limit.GetValueOrDefault()`, and a string or bytes wrapper's value is the
  field itself, `self.Label`. It is `GetValueOrDefault()` rather than `.Value` because generated code
  builds with warnings as errors, and C# warns at `.Value` wherever it cannot see the guard, which for a
  loop's binding or a lookup is everywhere. The value is read only where it is set
  ([13.1](./§13-Messages.md#131-field-access)), and were it ever unset, the zero is what C++ reads.
- A wrapper stored in a field, an element or a map's value is stored as its value: `Limit = 5L` for a
  literal, `Limit = 0L` for one that leaves its value out, `Limit = wrapper.Value` for a message.
  Writing `limit.value` stores the field itself, `self.Limit = n`, which sets it where it was unset, as
  writing through any message field does.
- Writing `limit.value` reaches `limit` before the value is evaluated, as any assignment reaches its
  target ([9.3](./§9-Expressions%20and%20Operators.md#93-evaluation-order)). Where the value could tell, because it reads a message, asks a map or
  calls a method, the wrapper is reached by a statement of its own first: `self.Limit ??= 0L;`, which
  also unsets the other member of its `oneof`, or `ProtoCrossMaps.AddIfAbsent(map, key, 0L);` for a
  missing key. A value that could not tell, `limit.value = n * 2`, is a single store. A compound
  assignment reads its target first and needs no reach.
- A loop over wrappers binds each element as a place in the list,
  `foreach (var each in ProtoCrossWrappers.Elements(self.Limits))`, whose `each.Value` reads and
  writes the element where it is. A `foreach` over the values would bind a copy, and a binding is the
  element: a store through it changes the list, and a change made to the element any other way, by a
  loop inside it over the same field, is seen through it. Nothing in the loop may change the field's
  membership or order ([18](./§18-Mutability.md#18-mutability)), so the place stays sound, and the loop is still a
  `foreach`, so a body that always returns or breaks leaves nothing unreachable.
- A wrapper used as a message anywhere else, held in a local, passed, returned, or called on, is made
  into one: `new Int64Value { Value = self.Limit.GetValueOrDefault() }`. It is new, so it is already
  the copy storing it would make.
- A `mut fn` called on a wrapper held as its value is called on such a message, and the message's value
  is stored back as soon as the call returns. No argument may share the receiver
  ([18](./§18-Mutability.md#18-mutability)), so nothing can tell this from C++, which changes the field in place.
- A lookup in a map of wrappers is `ProtoCrossMaps.FindNullable(map, key) ?? fallback`, the fallback
  given as its value, because a `long?` meets neither `FindValue`'s constraint nor `FindReference`'s.
  A merge is `Merge`, since the values need no `Clone`.

A wrapper is asked for by file, as protoc asks: every message in `wrappers.proto` is one, and no
message anywhere else is, whatever its name.

**Generated C# suspends CS0162, C#'s warning about unreachable code, around one line only: the
`break;` that ends a switch arm C# may judge unreachable.** Each arm of a switch is a section of C#'s own `switch` ([15.3](./§15-Control%20Flow.md#153-switch)), and C# refuses
a section whose end can be reached (CS0163), so a section ends in `break;` wherever its arm can reach
its end. Whether it can is asked of the IR, by the rule that decides whether a method needs a return
([15.1](./§15-Control%20Flow.md#151-conditional-statements)), and that rule does not look at what a condition's value is. C# does. It works out an
expression built from constants before the method runs, and decides what can be reached from the
answer. So in

```protocross
case 1 {
    if true {
        return 1;
    }
}
```

ProtoCross finds that the arm can reach its end, since an `if` with no `else` may not be taken, and C#
finds that it cannot, since `if (true)` always is. The `break;` ProtoCross needs there is one C# warns
will never run (CS0162), and a consumer building with warnings as errors, as the conformance harness
does, cannot build the output. So wherever an arm holds a branch or a loop whose condition is built
from literals and enum values alone, at any depth, that `break;` is written with the warning
suspended around it, and around nothing else:

```csharp
case 1L:
{
    if (true)
    {
        return 1L;
    }
    #pragma warning disable CS0162 // A constant condition above may end the arm first.
    break;
    #pragma warning restore CS0162
}
```

Every other arm ends in a plain `break;`, or in nothing where ProtoCross already finds its end
unreachable. C++ never needs the guard, because it does not warn about a `break` it cannot reach.

**The `break;` is guarded rather than left out, because leaving it out is right only where C# folds
the condition and the fold ends the arm, and the IR cannot say which arms those are.** A condition
built from constants is not one C# always works out, nor one whose answer always ends the arm:

- `if 1 == 2 { return 1; }` is folded, to false. The branch is never taken, and the arm reaches its
  end.
- `if 2 * 3 == 6 { return 1; }` is folded under the default wrapping policy, where C# writes
  `unchecked(2L * 3L)`, and is not under the checked or saturating policy, where it writes a call to
  the runtime's `CheckedMultiply` or `SaturatingMultiply`. One source ends its arm under one
  `protocross.config.xml` and reaches its end under another ([10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy)).
- `if true { if large > 1 { return 1; } }` is folded, and the arm still reaches its end through the
  inner `if`.

In each of these, a section C# can leave with no `break;` is CS0163, which is an error in every
consumer's build that no setting turns off. A `break;` C# cannot reach is a warning, and the guard
suspends it for one line. With the guard, the output is right whichever way C# decides: a `break;`
C# can reach is there and leaves the switch, and one it cannot reach is never run and never
reported. The backend never has to predict C#. Predicting it would mean reproducing C#'s constant
folding and its reachability rules exactly, and consulting the overflow policy, which a backend
never does ([22.2](./§22-IR%20and%20Compiler%20Architecture.md#222-typed-ir-requirements)). Any mistake in that prediction would be an error rather than a warning.

Both halves of the guard carry weight, so neither is tidied away. Without the `#pragma`, a consumer
building with warnings as errors cannot build a method whose arm holds such a condition. Without the
`break;`, nobody can build one whose arm C# can leave. The `switch_statement` conformance vector runs
arms ended by a constant condition in both backends, under warnings as errors
([25.2](./§25-Testing%20and%20Conformance%20Vectors.md#252-conformance-vector-format)).

Three alternatives were rejected:

- **Teaching ProtoCross's reachability to fold constants.** It would change which methods need a
  return, which is the language rather than a backend's concern. Anywhere it folded something C# does
  not, it would also leave out a `break;` that C# requires.
- **Suspending CS0162 for the whole file**, as the header suspends CS1718 for a comparison written
  `x != x`. That would also hide what C# reports about code the
  author wrote and a constant condition makes unreachable, and it would change the header of every
  generated file.
- **Refusing a constant condition inside an arm.** That would turn one target's warning into a
  language rule.

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

**A map is protoc's `Map`, read through `protocross_runtime.h` and written through `mutable_x()`**
([14.2](./§14-Repeated%20Fields%20and%20Collections.md#142-maps)). `operator[]` puts a default at a missing key, which the language means only where an element
is written to or through, so no read uses it. A lookup with a fallback is
`protocross_runtime::value_or(map, key, [&] { return fallback; })`, which calls the lambda only where
the key is missing, and gives the value by value, since the fallback is a temporary. `on_missing fail`
is `found_or_fail`, which gives the value by value too. What a lookup gives is a value, and a
reference into the map passed to a `mut fn` would change under it as the method changed the map, so
a lookup is a temporary of its own wherever it is passed. Whether a key is there is `contains`,
`count()` is `size()` as an `int32_t`, and a store is `(*self.mutable_x())[key] = value`. C++17
evaluates the value before that element, so a value that asks about the map finds it as it was. Where
the map is reached through a message field or an element, which reaching sets, it is bound by
reference in a block of its own first, as a message field written through links is: the right side of
`=` is evaluated before the left, and a call's arguments in no order, so neither would set the links
first. A compound store written through an element evaluates its value into a local before either,
because the read must not find the message reaching puts at a missing key. `merge` takes its source
by value, since the source may be a map inside one of the target's own elements, which the merge
replaces while it reads. The helpers the support header adds are templates over the map, so it
still includes no protobuf header.

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
