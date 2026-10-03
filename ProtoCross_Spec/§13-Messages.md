## 13. Messages

### 13.1 Field Access

```protocross
customer.name
order.customer.address.city
```

**Decided: using the value of a singular message field requires established presence.**

This is the one place the initial backends disagreed silently. Reading an unset `Timestamp` field
raises `NullReferenceException` in C# and returns the default instance -- so, zero -- in C++. Both
are the correct idiomatic translation for their runtime. Neither can be made to match the other
without a runtime check in every target, so the situation is made unrepresentable instead, which is
the same choice `on_zero` makes for a zero divisor ([10.2.1](./§10-Numeric%20Semantics.md#1021-the-on_zero-clause)).

Normative Requirements:

- Using the **value** of a singular message-typed field is an error (`PC0078`) unless its presence
  has been established on every path reaching the use.
- "Using the value" is reading a field through it, calling a method on it, passing it as an
  argument, or binding it to a local. Each launders the same divergence, so the rule is stated once
  about the value rather than four times about its uses.
- Presence is established by `has` ([8.4](./§8-Type%20System.md#84-nullability-and-presence)), in any of these shapes:
  - inside `if has f { ... }`;
  - in the `else` of `if not has f { ... } else { ... }`;
  - after `if not has f { return ...; }`, or any guard whose branch cannot complete normally;
  - in the right operand of `and` when the left proved it, and after `or` on the false side.
- A fact is about the message a guard tested. Nothing in the language unsets a field except setting
  another member of its `oneof` ([18](./§18-Mutability.md#18-mutability)), but a change can replace the
  message a fact is about, and what was shown about the old one says nothing about the new one. A fact
  holds until a change that could make it false:
  - Assigning a local ends every fact reached through it, however deep: `c = b;` ends what was shown
    about `c.inner` and about `c.inner.stamp` alike.
  - Assigning a field ends every fact reached through it, since the message there is a new one, and
    every fact about another member of its `oneof`, which it unsets. Each message it writes through
    is set too, so the same goes for the other members of that message's `oneof`. What was shown
    about the field itself still holds: the assignment sets it.
  - The messages an assignment writes through are set before its value is evaluated
    ([9.3](./§9-Expressions%20and%20Operators.md#93-evaluation-order)), so the facts setting them ends are already ended for the value: after
    `if has disputed`, `pending.cents = disputed.cents;` is `PC0078`. The field itself is set after
    its value, so `pending = disputed;` still reads the `disputed` the guard tested.
  - Appending ends every fact about another member of each `oneof` it writes through, which it
    unsets, before its value is evaluated, as an assignment's links do. It ends nothing else: no
    element that was there changes, and each message it writes through is set.
  - Calling a `mut fn` ends every fact reached through the message it is called on, since it may
    assign anything inside it.
  - A change through the name a `for` binds ends the same facts about every such name, since two
    of them may be one element.
  - Nothing may change a parameter, so a fact about one holds for the remainder of the method, and
    so does a fact about the receiver of a method that is not `mut`.
  - A statement that makes a change anywhere inside it, in any branch or loop body, ends those facts
    for everything after the statement. That includes a branch that cannot complete normally,
    which costs a guard written again, never a read let through.
  - A loop's body, and a `while` loop's condition, run again after the body may have made a change,
    so they see no fact the body could end. What a `while` condition itself proves still holds
    inside the body, because it is proved afresh on every pass.
- A field written through needs no guard: `customer.name = "x";` and `customer.tags.append("x");`
  set `customer` when it is unset ([18](./§18-Mutability.md#18-mutability)). Neither establishes a fact, though, so reading
  `customer` after it still needs one.
- A message field reached through a value that has no name -- a method result, or a message literal
  ([13.2](#132-message-construction)) -- cannot be guarded, and is `PC0078`. Binding the intermediate to a local first gives it
  the name a guard needs.
- The receiver, parameters, locals, and `for` bindings are present by construction and are never
  guarded. Every message value in the language comes from one of those, from a guarded read, or
  from a literal, which is a message whatever its fields hold.
- Reading a **scalar** field never requires a guard. An unset proto3 scalar reads as the type's
  zero, an unset proto2 scalar as its declared default, and both targets have always agreed.
- Reading a **repeated** field never requires a guard. An unset one is empty.
- Because the guard is a compile-time requirement, a guarded read emits the plain accessor chain in
  every backend. The rule costs nothing at runtime.

`Presence/UnsetMessageRead` in `protocross.config.xml` ([10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy)) names this behavior. It has one legal
value today, `RequireGuard`.

Open Questions:

- Oneof fields, which have a case discriminator this says nothing about.
- Map fields, which are not supported at all ([14.2](./§14-Repeated%20Fields%20and%20Collections.md#142-maps)).

### 13.2 Message Construction

**Decided: a message is built by a literal, `new T { field: value, … }`, which is an expression.**

```protocross
new Invoice {
    number: 42,
    customer: new Customer { name: "unknown" },
    items: [
        new InvoiceItem { quantity: 2, unit_price_cents: 300 },
        new InvoiceItem { quantity: 4, unit_price_cents: 125 },
    ],
}
```

A literal and a test fixture ([25.3](./§25-Testing%20and%20Conformance%20Vectors.md#253-author-written-protocross-unit-tests)) are one idea, so they have one spelling: a fixture's
`receiver { … }` body is a literal's list of fields.

Normative Requirements:

- A message literal is `new`, the message's type name, and its fields between braces. Each field is
  `name: value`. Fields are separated by commas, and a comma after the last is allowed.
- A literal is an expression, and may be written wherever one may: a local's initializer, a returned
  value, a call's argument or a test's, a field's value, or something read from,
  `new Invoice { … }.number`.
- `new` is not reserved ([6.4](./§6-Lexical%20Structure.md#64-keywords)). It begins a literal only when a type name follows it, so a field or a
  local named `new` keeps its meaning: `new.this` and `has new` read a field.
- The type is resolved as a type name is anywhere else ([8.1](./§8-Type%20System.md#81-type-sources)): by its full name, or by a simple name no other
  message or enum shares. A type that is not a message is `PC0091`.
- A field's value is any value of the field's type. For a message field that is a literal of the
  field's own type, or any other message of that type: a parameter, a local, a field read, a call's
  result. A nested literal always writes its type, and the field never supplies it:
  `customer: new Customer { … }`, never `customer: { … }`. A value of another type is `PC0063`, a
  literal of another message included, and a literal given to a field that is not a message is
  `PC0064`.
- A repeated field's value is a list, `[first, second]`, holding every element in order. A comma after
  the last element is allowed. A list is only ever a repeated field's value, and lists do not nest.
  A repeated field given anything but a list is `PC0063`, a whole repeated value such as
  `items: other.items` included, and so is a list given to a singular field.
- Each field is written at most once, a repeated one included (`PC0061`): a repeated field's list is
  the one place its contents are read.
- A map field is refused (`PC0060`), as maps are everywhere else ([14.2](./§14-Repeated%20Fields%20and%20Collections.md#142-maps)).
- A field that is left out is unset. There is no required field and no check that a message is
  complete: protobuf has neither, and this compiler does not invent one.
- The values are evaluated in the order their fields are written ([9.3](./§9-Expressions%20and%20Operators.md#93-evaluation-order)), and each field is set in that order.
  The order can be seen: each member of a oneof is a field of its own here ([8.4](./§8-Type%20System.md#84-nullability-and-presence) leaves oneofs
  open), so a literal may give two members of one oneof a value, and the message keeps the one
  written last, as protobuf keeps whichever member was set last.
- **Storing a message stores a copy.** A field or a local given a message that is not a literal holds
  a message of its own rather than one shared with wherever the value came from, so nothing done
  through either can show through the other ([18](./§18-Mutability.md#18-mutability)). A local given a repeated value
  holds copies of its elements in the same way. A literal is exempt because it is built where it is
  stored and nothing else can hold it. A method's result is not: it may be a field of the method's
  receiver, which is still the receiver's.
- **A literal establishes no presence** ([13.1](#131-field-access)). What a message field is given is not something a
  guard has tested, so a message field read straight off a literal is `PC0078`, and a local holding
  a literal is guarded like any other local.

Current Status:

- Both backends generate a literal wherever one is written, a test's receiver fixture included, and
  the `message_literals` conformance vector runs one in each place in both
  ([25.2](./§25-Testing%20and%20Conformance%20Vectors.md#252-conformance-vector-format)).
- C# writes an object initializer, and copies a stored message that is not a literal with protoc's
  `Clone`. Into a local it copies only in a method that changes a message, since nowhere else can a
  copy and a share be told apart ([24.1](./§24-Generated%20API%20Strategy.md#241-c)).
- C++ writes a lambda that declares the message, sets its fields and returns it, called where the
  literal is written ([24.2](./§24-Generated%20API%20Strategy.md#242-c)). Assigning a message there
  copies it.

### 13.3 Equality

Open Questions:

- Does message equality mean identity, field-wise equality, or backend-defined equality?
- Is deep equality part of version 1?
- Should the binder reject equality on message and repeated values until this is settled? The
  current type rule permits equality for operands of the same type, while the semantic meaning for
  messages and repeated collections is not specified here.

### 13.4 Extensions

**Decided for 1.0: an extension is not a field of any message, and no name reaches one.**

A protobuf extension is declared in one scope and extends a message somewhere else, and it is a
field of neither. It is not a field of the message it extends, whose generated class has no accessor
for it: both runtimes read one through `GetExtension` and the extension's identifier. And it is not
a field of the message whose scope declares it, which gives the extension its full name and nothing
else. C# keeps that name on a nested static class, and C++ keeps it as a static identifier member,
so neither is something an instance of the declaring message can be read through.

Normative Requirements:

- The fields of a message are the fields it declares. An extension, whether it is declared at file
  level or inside a message, is never one of them.
- A name written where a field could be meant is resolved against those fields alone. An
  extension's name is reported as any name that is not a field is reported: `PC0037` for a bare
  name, `PC0041` for a member access or the operand of `has`, and `PC0059` for a test fixture field.
- An extension declared inside a message takes nothing from that message's name space. A method on
  the message may have the extension's name ([16.1](./§16-Methods.md#161-method-attachment)).

Implementation Note:

- Google.Protobuf's `MessageDescriptor.FindFieldByName` looks the name up in the descriptor pool
  under the message's full name. That full name is also the prefix of an extension declared inside
  the message, so the lookup finds that extension too. The compiler asks a single lookup that
  excludes extensions, and the fields an editor offers are listed from that same place, so what
  completion offers and what the binder accepts cannot disagree about one.

Open Questions:

- Reading an extension is deferred past 1.0. It needs its own syntax, because an extension is named
  by its full name on the message it *extends*. Protobuf's text format writes that as
  `[pkg.Host.scoped]` and its option syntax as `(pkg.Host.scoped)`. It also needs presence and
  repeated rules, and `GetExtension` in both backends.
