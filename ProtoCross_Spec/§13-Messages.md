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
- A fact, once established, holds for the remainder of the method. ProtoCross cannot assign to a
  field ([18](./§18-Mutability.md#18-mutability)), so nothing shown to be set can become unset. A guard before a loop therefore holds
  inside it.
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

Open Questions:

- Should ProtoCross be able to create new message instances?
- Should object initializer syntax exist?
- Should construction be limited to backend helper APIs?

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
