## 14. Repeated Fields and Collections

### 14.1 Supported Operations

Implemented operation set:

- Iterate in order with `for <name> in <repeated expression> { ... }`.
- Read the repeated field as a collection value for iteration.
- Change the element a `for` is given, where the method may change the field
  ([18](./§18-Mutability.md#18-mutability)).
- Append an element to the end with `place.append(value);`, where the method may change the place
  ([18](./§18-Mutability.md#18-mutability)).

Not implemented:

- Length.
- Indexing.
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

```protocross
extend Invoice {
    mut fn add_item(item: InvoiceItem) {
        items.append(item);
    }
}
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
- **`place.append(value);` adds `value` to the end of a repeated value the method may change**: a
  repeated field of a message it may change, at any depth, or a local holding a repeated value, which
  is the method's own copy ([18](./§18-Mutability.md#18-mutability), [13.2](./§13-Messages.md#132-message-construction)).
  - It is the one method a repeated value has. Any other is `PC0042`.
  - It takes one value, of the element type. Another number of them is `PC0045`, and a value of
    another type is `PC0046`: no implicit numeric conversion is applied.
  - It has no value, so it is a statement of its own. Anywhere else -- an operand, an argument, an
    initializer -- it is `PC0095`, as a call to a `mut fn` is, and a call to a `mut fn` cannot be the
    value it appends.
  - A message appended is stored as a copy, unless it is a literal ([13.2](./§13-Messages.md#132-message-construction)).
  - Its target is written through as an assignment's is ([18](./§18-Mutability.md#18-mutability)): an unset message on the
    way is set, and needs no guard, and setting a member of a `oneof` that way unsets the others.
  - It reaches its target first, then evaluates its value, and adds the element last
    ([9.3](./§9-Expressions%20and%20Operators.md#93-evaluation-order)). A value that counts the elements counts the ones that were there.
  - Appending to a parameter's field, to the receiver of a method that is not `mut`, or to a value
    nothing holds -- a call's result, a literal -- is `PC0094`.
  - `append` is not a keyword. A method a source declares on a message under that name is called as
    any method is.
- **While a `for` traverses a repeated field, nothing inside it may change that field's
  membership, order or identity** (`PC0096`). That refuses anything that could, whether or not it
  would:
  - assigning the field, the message holding it, or anything further out, which replaces it;
  - appending to the field, which adds to it;
  - assigning, or writing through, another member of a `oneof` holding it, which unsets it;
  - calling a `mut fn` on the message holding it, or on anything further out, which may change it.

  What the called method does is not asked, because the answer would change whenever its body did,
  and break a loop in some other method. The names two loops bind over one field are taken to be one
  element, since they may be. A loop over a parameter's field has nothing to protect: nothing may
  change a parameter, and nothing the method may change is part of one.

Open Questions:

- Should filtering, mapping, sorting, or aggregation helpers exist? `No. Basics only. ~IS`
- ~~Should repeated field mutation ever be allowed?~~ Decided: an element may change through the name
  a `for` binds, and a field, or a local holding a repeated value, may be appended to with
  `place.append(value);`. Nothing may be cleared or removed ([18](./§18-Mutability.md#18-mutability)).
- Should collection indexing be bounds-checked with explicit error results if indexing is added?
  `Ugh... probably. I really want to say no, but... I have a feeling that not doing this could lead to security concerns in some language or another. ~IS`

### 14.2 Maps

**Decided: a map is read and changed by key, every read says what a missing key gives, and nothing
iterates one.**

```protocross
extend Basket {
    fn price_of(sku: string) -> int64 {
        return prices[sku] on_missing 0;
    }

    mut fn count_words() {
        for word in words {
            counts[word] on_missing 0 += 1;
        }
    }
}
```

The three initial targets keep a map's keys in three different orders: C#'s `MapField` and Python's
`dict` in the order they were added, and C++'s `Map` in the order of their hashes. None of them agrees
with another about a missing key either. C#'s indexer throws, C++'s `operator[]` puts a default at the
key and gives it back, and Python raises. So the language gives a map no order at all, and has no read
that leaves a missing key to the target.

Normative Requirements:

- A protobuf map field has the type `map<K, V>`, spelled as protobuf spells it, with each side named
  as the language names its type ([8.2](./§8-Type%20System.md#82-protobuf-scalar-mapping)): a key declared `sint64` is an `int64`. A local may hold a
  map, which is a copy of its own ([13.2](./§13-Messages.md#132-message-construction)). No type reference names a map, so no parameter or return
  value is one.
- **Reading.** `m[k] on_missing v` is the value `m` holds at `k`, or `v` where it holds none.
  `m[k] on_missing fail` ends the program there instead, with a diagnostic naming the map on standard
  error and exit code 70, as `on_zero fail` does ([10.2.1](./§10-Numeric%20Semantics.md#1021-the-on_zero-clause)).
  - A read without a clause is `PC0115`. It is the choice `on_zero` makes for a zero divisor, made
    where the read is written.
  - **The fallback is evaluated only when the key is missing**, so a fallback that would end the
    program does not end one whose key was there. It must already have the map's value type
    (`PC0116`); no implicit conversion is applied.
  - The clause belongs to the one lookup it follows, and its fallback parses at unary precedence, as
    `on_zero`'s does: `prices[sku] on_missing 0 + 1` adds one to whichever value the lookup gives. A
    clause ends the lookup, so a member of what it gives is read around parentheses:
    `(items[id] on_missing fail).quantity`.
  - What a lookup gives is a value: the message the map holds, or the fallback. Storing it stores a
    copy, as storing a field's message does ([13.2](./§13-Messages.md#132-message-construction)). It has no name, so a message field read
    through it cannot be guarded (`PC0078`), and nothing may change it (`PC0094`). Bind it to a local
    first.
- A key has the map's key type (`PC0114`). A literal key takes that type, as a literal assigned to a
  local does, so `counts[5]` is a key of a map of `int32` keys. Only a map is indexed (`PC0113`): a
  repeated field has no indexing ([14.1](./§14-Repeated%20Fields%20and%20Collections.md#141-supported-operations)).
- `k in m` is a `bool`: whether `m` holds `k`. It is the operator [12.2](./§12-Enums.md#122-whether-a-value-has-a-name) asks an enum with, and
  which question it asks is settled as `Level.HIGH` is read. A right side that is a dotted name whose
  first part names a value is that value, and has to be a map (`PC0118`); any other dotted name names a
  type, and asks about an enum. So a field named like an enum shadows the enum here, as it does
  everywhere else. There is no separate `contains`.
- `m.count()` is how many keys `m` holds, an `int32`, as protobuf's own size is in both targets.
  `m.is_empty()` is whether it holds none.
- **Changing.** A map the method may change ([18](./§18-Mutability.md#18-mutability)) is changed by key:
  - `m[k] = v;` stores `v` at `k`, replacing what was there.
  - `m[k] on_missing f op= v;` reads the element with its clause, as any compound reads its target,
    and stores the result: `counts[word] on_missing 0 += 1;`. A compound whose target has no clause is
    `PC0115`, as any read without one is.
  - `m.remove(k);` removes `k`, whether or not `m` held it. `m.clear();` removes every key.
  - `m.add_if_absent(k, v);` stores `v` at `k` unless `k` already holds a value, and
    `m.replace_if_present(k, v);` stores it only if `k` does. Each evaluates both of its arguments,
    whichever it does. Neither implies the operation is atomic: a map is not a concurrent structure.
  - `m.merge(other);` stores every value of `other` at its key, `other`'s value winning where both hold
    a key, which is what protobuf's parser and its merge do with a key written twice. Merging a map
    into itself changes nothing.
  - These five have no value, so each stands as a statement of its own, as `append` does
    ([14.1](./§14-Repeated%20Fields%20and%20Collections.md#141-supported-operations)). One inside an expression is `PC0095`, and one on a map nothing holds is `PC0094`. Each
    argument is checked as a call's is: a wrong number of them is `PC0045`, and one of the wrong type
    is `PC0046`.
  - The map is reached as a field written through is: no link needs a guard, and an unset message on
    the way is set, which unsets the other members of its `oneof` before the key is evaluated. Then
    the key and the value are evaluated, and the change is made last. So the value of `m[k] = v;` sees
    the map as it was before the store ([9.3](./§9-Expressions%20and%20Operators.md#93-evaluation-order)).
- **Writing through an element.** `items[id].label = "x";`, `items[id].marks.append(7);` and
  `items[id].tags[t] = 3;` write through the element at `id`, and a missing key is given a new
  message first, as an unset message field written through is set ([13.1](./§13-Messages.md#131-field-access)). Nothing is read there, so
  an element written to or through takes no clause (`PC0117`). A compound written through a lookup,
  `(items[id] on_missing new Item { quantity: 10 }).quantity += 5;`, reads with its clause and stores
  into the element at that key. **The read comes first**, as a compound's always does, so at a
  missing key it starts from the fallback, 15 here, and under `on_missing fail` it ends the program,
  rather than reading the message the store would have put there.
- A call to a `mut fn` on an element is not a write through it. Its receiver is read, so it needs a
  clause, and what the clause gives is held by nothing (`PC0094`). Copy the message into a local, call
  the method on the local, and store it back.
- A map field is never assigned whole (`PC0034`), and a message literal gives one a list of entries
  rather than a whole map ([13.2](./§13-Messages.md#132-message-construction)).
- **Nothing iterates a map.** `for` over one is `PC0033`, and no operation gives a map's keys, its
  values, its entries, a first or last element, a position, a sort, a minimum or a maximum. An author
  who needs an order keeps the keys in a repeated field, in that order, and iterates that.
- `has` on a map field is `PC0079`: a map has no presence, and an unset one is an empty one.
- **Two maps are equal** when they hold the same keys, each with a value equal to the other's under
  `==` ([9.2](./§9-Expressions%20and%20Operators.md#92-operators)), in whatever order either holds them. So a map holding a NaN equals no map, itself
  included. A map of messages has no equality, as a message has none (`PC0098`, [13.3](./§13-Messages.md#133-equality)).
- A `for` over a field of what a lookup gives traverses a message of its own, as one over a field of a
  call's result does ([24.1](./§24-Generated%20API%20Strategy.md#241-c)), so a change the loop makes through the map does not reach what the loop
  traverses.

`PC0038` refused every use of a map field before #11, and `PC0060` a map field in a message literal.
Both are retired, and neither code will mean anything else.

Rationale: The owner settled which operations a map has on #11, and that none of them iterates one
(2026-10-04): the targets order a map in too many different ways to account for. The spellings, the
missing-key rule, the shape of a map literal and the compound target were settled when #11 began
(2026-10-07). A clause at the read follows `on_zero`, which made the same choice about a zero divisor,
and it needs no analysis of its own. A lookup guarded by an established `k in m`, as a message field
is by `has` (13.1), was the alternative; it would have needed a second set of facts to track, ended by
every `remove`, `clear` and store, and it can still be added later without breaking a clause anyone
wrote. Methods were preferred to keywords for the changes because `append` is already spelled that
way, and the names say what happens when the key is there and when it is not. The fallback is lazy
because an eager one ends a program whose key was present; `on_zero`'s fallback is eager in both
backends today, and #173 brings it into line.

Open Questions:

- ~~Is map iteration order specified or explicitly unspecified?~~ Decided: no map is iterated (above).
- ~~What map operations are portable?~~ Decided: the ones above.
- A repeated field of entries made into a map, the last of a key winning, may be added later.
- A map of messages has equality when a message does ([13.3](./§13-Messages.md#133-equality)).
