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
- A fact is about the message a guard tested. ProtoCross cannot assign to a field ([18](./§18-Mutability.md#18-mutability)), so nothing
  shown to be set can become unset, but a local can be assigned another message, and what was shown
  about the one it held says nothing about the next:
  - A fact about the receiver or a parameter holds for the remainder of the method. Neither can be
    assigned, so a guard before a loop holds inside it.
  - A fact reached through a local holds until the local is assigned. Assigning it ends every fact
    reached through it, however deep: `c = b;` ends what was shown about `c.inner` and about
    `c.inner.stamp` alike.
  - A statement that assigns a local anywhere inside it, in any branch or loop body, ends those
    facts for everything after the statement. That includes a branch that cannot complete normally,
    which costs a guard written again, never a read let through.
  - A loop's body, and a `while` loop's condition, run again after the body may have assigned a
    local, so they see no fact about a local the body assigns. What a `while` condition itself
    proves still holds inside the body, because it is proved afresh on every pass.
- A message field reached through a value that has no name -- a method result -- cannot be guarded,
  and is `PC0078`. Binding the intermediate to a local first gives it the name a guard needs.
- The receiver, parameters, locals, and `for` bindings are present by construction and are never
  guarded. Every message value in the language comes from one of those or from a guarded read.
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

**Decided: a message is built by a literal, `new T { field: value, … }`.**

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
- `new` is not reserved ([6.4](./§6-Lexical%20Structure.md#64-keywords)). It begins a literal only when a type name follows it, so a field or a
  local named `new` keeps its meaning: `new.this` and `has new` read a field.
- The type is resolved as a type name is anywhere else ([8.1](./§8-Type%20System.md#81-type-sources)): by its full name, or by a simple name no other
  message or enum shares.
- A message field's value is a literal of the field's own type. The type is always written, and the
  field never supplies it: `customer: new Customer { … }`, never `customer: { … }`. A literal of
  another message is `PC0063`, and any other value is `PC0062`.
- A repeated field's value is a list, `[first, second]`, holding every element in order. A comma after
  the last element is allowed. A list is only ever a repeated field's value, lists do not nest, and a
  repeated field given anything but a list is `PC0063`, as is a list given to a singular field.
- Each field is written at most once, a repeated one included (`PC0061`): a repeated field's list is
  the one place its contents are read.
- A map field is refused (`PC0060`), as maps are everywhere else ([14.2](./§14-Repeated%20Fields%20and%20Collections.md#142-maps)).
- A field that is left out is unset. There is no required field and no check that a message is
  complete: protobuf has neither, and this compiler does not invent one.

Current Status:

- A literal appears only as the value of a fixture's field, or of a field of a literal inside one.
  Writing one in a method body is the next step of #80, and until it lands [18](./§18-Mutability.md#18-mutability)'s "methods
  cannot allocate new protobuf messages" still holds.

Open Questions:

- Whether a repeated field may also be given a whole repeated value, `items: items`, as well as a
  list. It matters only where names are in scope, so it is settled when a literal can appear in a
  method body.

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
