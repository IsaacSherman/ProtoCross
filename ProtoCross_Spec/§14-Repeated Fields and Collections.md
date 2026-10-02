## 14. Repeated Fields and Collections

### 14.1 Supported Operations

Implemented operation set:

- Iterate in order with `for <name> in <repeated expression> { ... }`.
- Read the repeated field as a collection value for iteration.
- Change the element a `for` is given, where the method may change the field
  ([18](./§18-Mutability.md#18-mutability)).

Not implemented:

- Length.
- Indexing.
- Append. Decided ([18](./§18-Mutability.md#18-mutability)), and not implemented yet.
- Clear, and removing an element. Decided against: a field that cannot be emptied keeps what a guard
  shows set ([13.1](./§13-Messages.md#131-field-access)).
- Element assignment.

Example syntax:

```protocross
var total: int64 = 0;

for item in invoice.items {
    total = total + item.amount;
}

return total;
```

Normative Requirement:

- `for` may iterate only a protobuf repeated field value. Iterating anything else is `PC0033`.
- Iteration order is the protobuf repeated-field order.
- The name a `for` binds is read-only: assigning it is `PC0034`, `n = n + 2;` included. To work
  with a changed value, copy it into a local.
- The element itself may change, through that name, when the method may change the field it is
  an element of ([18](./§18-Mutability.md#18-mutability)): a field of it may be assigned, and a `mut fn` called on
  it. `for item in items { item.add_trait(trait); }` is allowed, because the field still holds the
  same elements in the same order.
- **While a `for` traverses a repeated field, nothing inside it may change that field's
  membership, order or identity** (`PC0096`). That refuses anything that could, whether or not it
  would:
  - assigning the field, the message holding it, or anything further out, which replaces it;
  - assigning another member of a `oneof` holding it, which unsets it;
  - calling a `mut fn` on the message holding it, or on anything further out, which may change it.

  What the called method does is not asked, because the answer would change whenever its body did,
  and break a loop in some other method. The names two loops bind over one field are taken to be one
  element, since they may be. A loop over a parameter's field has nothing to protect: nothing may
  change a parameter, and nothing the method may change is part of one.

Open Questions:

- Should filtering, mapping, sorting, or aggregation helpers exist? `No. Basics only. ~IS`
- ~~Should repeated field mutation ever be allowed?~~ Decided: an element may change through the name
  a `for` binds, and a field may be appended to (not implemented yet). Nothing may be cleared or
  removed ([18](./§18-Mutability.md#18-mutability)).
- Should collection indexing be bounds-checked with explicit error results if indexing is added?
  `Ugh... probably. I really want to say no, but... I have a feeling that not doing this could lead to security concerns in some language or another. ~IS`

### 14.2 Maps

Protobuf maps are repeated key-value structures with language-specific APIs.

Current Status:

- Maps are not supported. A map field is rejected rather than treated as an ordinary repeated
  key-value message.

Open Questions:

- Is map iteration order specified or explicitly unspecified?
- What map operations are portable?
