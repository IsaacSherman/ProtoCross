## 14. Repeated Fields and Collections

### 14.1 Supported Operations

Implemented operation set:

- Write a list as a value, `[first, second]`, of the type `repeated T`, and hold one in a local, pass
  one as an argument, and return one ([8.1](./§8-Type%20System.md#81-type-sources)).
- Iterate any list in order with `for <name> in <list> { ... }`.
- Change the element a `for` is given, where the method may change the list
  ([18](./§18-Mutability.md#18-mutability)).
- Append an element to the end with `place.append(value);`, where the method may change the place
  ([18](./§18-Mutability.md#18-mutability)).
- Count a list's elements, `items.count()`, and ask whether it holds none, `items.is_empty()`.
- Read the element at an index the compiler shows is there, `items[i]` from the start or `items[^j]`
  from the end, and replace it, write through it, or call a `mut fn` on it, where the method may change
  the list ([18](./§18-Mutability.md#18-mutability)).
- Read the element at any index, from the start with `items.try_get_index(i)` or from the end with
  `items.try_get_backward_index(j)`, saying with `on_refused` what an index out of range gives.
- Give a repeated field a whole list, in a message literal or by assignment, which it stores a copy of
  ([13.2](./§13-Messages.md#132-message-construction), [18](./§18-Mutability.md#18-mutability)).

Not implemented:

- Clear, and removing an element. Decided against. A field is emptied only by assigning it a list,
  `items = [];`, which ends every fact shown about what it held, as assigning any field does
  ([13.1](./§13-Messages.md#131-field-access), [18](./§18-Mutability.md#18-mutability)).

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

```protocross
extend Reading {
    fn spread() -> double {
        if samples.count() < 2 {
            return 0.0;
        }
        return samples[^1] - samples[0];
    }

    fn sample_or_zero(i: int32) -> double {
        if i >= 0 and i < samples.count() {
            return samples[i];
        }
        return samples.try_get_backward_index(1) on_refused 0.0;
    }
}
```

```protocross
extend Interval {
    fn bounds(scale: double) -> repeated double {
        var products: repeated double = [low * scale, high * scale];

        var minimum = low * scale;
        var maximum = minimum;
        for product in products {
            if product < minimum {
                minimum = product;
            }
            if product > maximum {
                maximum = product;
            }
        }

        return [minimum, maximum];
    }
}
```

Normative Requirement:

- **A list is a value**, `[first, second]`, written wherever an expression may be: a local's
  initializer, an argument, a result, a fallback, a repeated field's value. A comma after the last
  element is allowed. Its type is `repeated T` ([8.1](./§8-Type%20System.md#81-type-sources)).
  - Where a list's type is expected -- a local declared `repeated T`, a parameter, a result, a field --
    each element is bound as a value of `T`, so a literal takes `T`, as one does wherever a type is
    expected ([10.3](./§10-Numeric%20Semantics.md#103-numeric-conversions)). An element of another type is `PC0148`.
  - Where nothing says what a list holds, its elements do. Every element has one type, which is that
    of the first element that is not a numeric literal; a literal takes it, as a literal operand takes
    the other operand's ([10.3](./§10-Numeric%20Semantics.md#103-numeric-conversions)), and its range is checked in that type alone, so
    `[-9223372036854775809, ratio]` is a list of doubles though no integer type holds its first element.
    A list of literals alone is `double` where any of them is a floating-point literal, so `[1, 2.5]` is
    a `repeated double`. One of integer literals alone is `uint64` where one of them only `uint64` can
    hold, and `int64` otherwise, as each literal's natural type is. An element of another type is
    `PC0148`.
  - An empty list where nothing says what it holds is `PC0149`: write its type where it is declared,
    `var items: repeated double = [];`.
  - Lists do not nest: a list holding a list, or a repeated value, is `PC0147`, and so is
    `repeated repeated T`. A list holding a map is `PC0148`.
  - Its elements are evaluated in the order they are written
    ([9.3](./§9-Expressions%20and%20Operators.md#93-evaluation-order)), and each message among them is stored as a copy, unless it is a literal,
    as a field's message is ([13.2](./§13-Messages.md#132-message-construction)).
- **A stored list is a copy.** A local, or a field, given a list holds a copy of it, messages and all,
  so nothing done through either shows through the other
  ([13.2](./§13-Messages.md#132-message-construction)). A list written in place is exempt, since nothing else holds it.
- **A list is passed and returned as any value is.** A parameter of type `repeated T` is read-only, as
  every parameter is: it is not appended to (`PC0094`) or assigned (`PC0034`), and neither are its
  messages changed through it ([16.2](./§16-Methods.md#162-parameters-and-returns), [18](./§18-Mutability.md#18-mutability)). It is not copied as it is passed, which
  nothing could tell. A list returned belongs to the caller, which stores a copy of it, under the rule
  above, wherever it stores it.
- `for` may iterate any list: a repeated field, a local, a parameter, a call's result, or a list
  written in place. Iterating anything else is `PC0033`.
- Iteration order is the list's order, which for a field is the protobuf repeated-field order.
- The name a `for` binds is read-only: assigning it is `PC0034`, `n = n + 2;` included. To work
  with a changed value, copy it into a local.
- The element itself may change, through that name, when the method may change the list it is
  an element of ([18](./§18-Mutability.md#18-mutability)): a field of it may be assigned, and a `mut fn` called on
  it. `for item in items { item.add_trait(trait); }` is allowed, because the list still holds the
  same elements in the same order. A local's list is the method's own, so its elements may always
  change, and a parameter's never may.
- **A list has five methods: `count`, `is_empty`, `append`, `try_get_index` and
  `try_get_backward_index`.** Any other is `PC0042`. None is a keyword: a method a source declares on
  a message under one of these names is called as any method is.
- **`items.count()` is how many elements `items` holds, an `int32`**, as protobuf's own size is in both
  targets, and as a map's count is ([14.2](#142-maps)). `items.is_empty()` is whether it holds none. Each
  takes no argument (`PC0045`), and reads any list: a field, a local, a parameter, a call's result, or a
  list written in place.
- **`items.try_get_index(i)` is the element at `i`, counting from 0, and
  `items.try_get_backward_index(j)` the element `j` from the end, 1 being the last and the count the
  first.** Each refuses where there is no element there, so a call to one says what that gives, with an
  `on_refused` clause after it, as a call to a method that may refuse does ([16.3.1](./§16-Methods.md#1631-refusals)):
  - `on_refused <fallback>` gives the fallback, which must already have the element type (`PC0140`);
    no implicit conversion is applied. It is evaluated only where there is no element.
  - `on_refused fail` ends the program, with a line on standard error naming the list, the index and
    the count, and exit code 70, as `on_missing fail` does ([14.2](#142-maps)).
  - `on_refused refuse value` ends the method with the refusal `value`, as `on_missing refuse value`
    does: only in a method that declares a refusal (`PC0136`), with a value of the type it declares
    (`PC0137`), and from a read that stands on its own (`PC0142`). A try method refuses with no value
    of its own, so a bare `on_refused refuse` has nothing to pass on (`PC0150`).
  - A call with no clause is `PC0138`, whose help lists the forms that fit where it is written.
  - The index may be of any integer type, and anything else is `PC0046`. It is compared with the count
    as the number it is, so a negative index, a from-end `0`, and one past the count have no element,
    in every integer type, and none wraps around to an element that is there.
  - The list is evaluated before the index, and the fallback or the refusal only after both
    ([9.3](./§9-Expressions%20and%20Operators.md#93-evaluation-order)).
  - What a try method gives is a value, as what a map's lookup gives is ([14.2](#142-maps)): storing a
    message it gives stores a copy ([13.2](./§13-Messages.md#132-message-construction)).
- **`items[i]` is the element at `i`, counting from 0, and `items[^j]` the element `j` from the end, 1
  being the last and the count the first, where the facts at the read show the index in range.** Nothing
  is checked as the program runs. A read the facts do not show is `PC0152`, whose help writes the guard
  that would show what is missing, `if i >= 0 and i < items.count() { … }`, and names the try method
  that answers an index out of range instead.
  - Facts are shown and combined as a field's presence is ([13.1](./§13-Messages.md#131-field-access)): a condition shows what it
    says inside the branch it guards, and its opposite inside the `else`, and after an `if` whose branch
    cannot fall through, what its opposite says holds for the rest of the block. `and`, `or` and `not`
    combine them, and the right operand of `and` is read with what the left shows when true, and of
    `or` with what it shows when false. Exactly
    these show a fact:
    - `items.count()` compared with an integer literal, by `<`, `<=`, `>`, `>=`, `==` or `!=`, either
      side of the other, shows the least the count can be: `items.count() > 2` shows it is at least 3,
      and `items.count() != 0` that it is at least 1. `items.is_empty()` shows it is at least 1 where it
      is false.
    - A list written out and stored, by a local's declaration or by an assignment, shows as many
      elements as it was written with: after `var xs: repeated int32 = [4, 5, 6];`, `xs[2]` is shown.
      An append, standing as a statement of its own, shows one more than was shown before it.
    - An index compared with an integer literal shows that it is at least 0, `i >= 0`, or at least 1,
      `i > 0`.
    - A literal is one written, or one negated whose value its type holds. `-(-2147483648)` is an
      `int32` its type cannot hold, whose value depends on the overflow policy
      ([10.1](./§10-Numeric%20Semantics.md#101-integer-overflow)), so it shows nothing, and as an index it is never shown.
    - An index compared with a count shows how the two are related: `i < items.count()` that it is
      below the count, and `i <= items.count()` or `i == items.count()` that it is at most the count.
    - A comparison of two integer types needs a conversion ([10.3](./§10-Numeric%20Semantics.md#103-numeric-conversions)), and one that keeps every value of
      what it converts is read through, so `i < items.count() as int64` shows `i`, an `int64`, below the
      count. A count is never negative, and keeps its value in every integer type. A conversion of an
      index that could change it, an `int64` to an `int32`, compares a different number, and shows
      nothing.
  - An index from the start needs to be at least 0, which an unsigned one always is, and below the
    count. An index from the end needs to be at least 1 and at most the count. A literal needs the count
    alone: `items[2]` needs it to be at least 3, `items.count() > 2`, and `items[^2]` at least 2.
  - A fact is about a list a local, a parameter or a field holds, and about an index a literal is or an
    integer local, parameter or `for` binding holds. Any other list or index is `PC0152`, whose help
    says to store it in a local first, so a list read from an element is: `boxes[0].parts[0]` is read as
    `var parts = boxes[0].parts;`, and the facts shown about `parts`.
  - A fact ends where what it is about may change, as a presence fact does ([13.1](./§13-Messages.md#131-field-access), [18](./§18-Mutability.md#18-mutability)):
    assigning the index, assigning the list or anything it is reached through, calling a `mut fn` that
    may change it, or setting another member of a `oneof` on the way to it. In a loop, anything the body
    may change ends what was shown about it for every pass, since a pass reads what the last one left.
    An append ends nothing, since nothing removes an element. A fact about a parameter never ends.
    Where a change ended a fact the read needed, `PC0152` says which change.
  - A literal index no list has an element at, a negative one or a from-end `0`, is `PC0153`, which no
    guard could show. A literal index is an `int32`, the count's type, so one past what an `int32`
    holds is `PC0036`.
  - The index may be of any integer type, and anything else is `PC0114`. Both targets read the element
    with their own indexer, which the index always fits.
  - `on_missing` after a list's index is `PC0154`: nothing is missing where an index is shown, and the
    help names the try method that answers one out of range.
  - What a read gives is a value, as what a try method gives is: storing a message stores a copy
    ([13.2](./§13-Messages.md#132-message-construction)), a `for` over a field of one traverses that value whatever the body changes,
    and a `mut fn` given one as an argument is given it as it was when the call began
    ([18](./§18-Mutability.md#18-mutability)).
- **An element at an index shown in range is a place, where the method may change the list**
  ([18](./§18-Mutability.md#18-mutability)): a list a local holds, or a repeated field of a message the method may change, at any
  depth, or one a `for`'s binding holds.
  - `items[i] = value;` replaces the element. The value has the element type (`PC0035`), with no
    implicit conversion, and a message is stored as a copy, unless it is a literal
    ([13.2](./§13-Messages.md#132-message-construction)). A compound assignment, `counts[i] += 1;`, reads the element and replaces it.
  - `boxes[i].grams = 3;`, `boxes[i].parts.append(7);` and `boxes[i].marks[k] = 1;` write through the
    element, and `boxes[i].weigh();`, a `mut fn`, changes it, as each does a field
    ([18](./§18-Mutability.md#18-mutability)).
  - The index is shown in range by the same facts as a read's, and an element written takes no
    clause: `on_missing` there is `PC0154`. `PC0152`'s help for one writes the guard alone, since a try
    method only reads. For a list nothing names, `boxes[0].parts[1] = 7;`, it says to copy the message
    it is in to a local, write the element there, and store the local back, unless a loop around the
    write traverses what that store would change: inside `for box in boxes`, storing `boxes[0]` back
    replaces an element of `boxes`, which is `PC0096`. There it says to write the element in a `mut fn`
    on the message, `Box`, where the list is its own field and a guard can show the index, and to call
    that on `boxes[0]`, which changes the element without replacing it. Where a loop inside that one
    traverses a list in that message, `for part in box.parts`, the call is `PC0096` as well, and it
    names that loop, `box.parts`, and says to make the change after it, where the call is allowed. Neither replacing an element nor writing inside one ends
    anything shown about the list's count, or about an index, which no element holds.
  - A store reaches the list first, setting every unset message on the way, then evaluates its value,
    and replaces the element last ([9.3](./§9-Expressions%20and%20Operators.md#93-evaluation-order)). The element is always there, so nothing is put at its index,
    as a map's store puts a key.
  - Writing to an element of a list the method may not change is `PC0094`: a list parameter's, one a
    parameter holds, the receiver's in a method that is not `mut`, and a call's result's.
  - A message field read through an element is a read, which no guard can show is set, since no fact
    is about an element (`PC0078`), so it is read through a local: `var box = boxes[i];`. One written
    through needs no guard.
- **`place.append(value);` adds `value` to the end of a repeated value the method may change**: a
  repeated field of a message it may change, at any depth, or a local holding a repeated value, which
  is the method's own copy ([18](./§18-Mutability.md#18-mutability), [13.2](./§13-Messages.md#132-message-construction)).
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
  - Appending to a list parameter, to a parameter's field, to the receiver of a method that is not
    `mut`, or to a value nothing holds -- a call's result, a literal -- is `PC0094`.
- **While a `for` traverses a repeated field, or a local holding a list, nothing inside it may change
  that list's membership, order or identity** (`PC0096`). That refuses anything that could, whether or
  not it would:
  - assigning the field or the local, the message holding it, or anything further out, which replaces
    it;
  - appending to it, which adds to it, or replacing one of its elements, `items[0] = item;`;
  - assigning, or writing through, another member of a `oneof` holding it, which unsets it;
  - calling a `mut fn` on the message holding it, or on anything further out, which may change it.

  What the called method does is not asked, because the answer would change whenever its body did,
  and break a loop in some other method. The names two loops bind over one field are taken to be one
  element, since they may be, and so is an element at an index with any binding over its list. A loop
  over a parameter's field has nothing to protect: nothing may change a parameter, and nothing the
  method may change is part of one.

  Writing inside an element of the list changes none of these, so `boxes[0].grams += 1;` is allowed
  inside `for box in boxes`, as a change through `box` is. A `mut fn` called on that element is not
  inside a loop over a list the element holds, since the element may be the binding's.

**Decided: no collection helpers, and no index left to the target.** There is no filtering, mapping,
sorting or aggregation: basics only. Nor is there `first()` or `last()`, which `items[0]` and
`items[^1]` already are, and which would need their own answer for an empty list. An index is checked
by the compiler, where the facts at the read show it in range, or as the program runs, by a try method
whose clause says what an index out of range gives. Neither leaves one to the target: C#'s indexer
throws, and C++'s reads past the end.

Open Questions:

- ~~Should repeated field mutation ever be allowed?~~ Decided: an element may change through the name
  a `for` binds, or at an index shown in range, and a field, or a local holding a repeated value, may be
  appended to with `place.append(value);`. A field may be assigned a whole list. Nothing may be cleared
  or removed ([18](./§18-Mutability.md#18-mutability)).
- [List equality](./§30-Unresolved%20Questions%20Index.md#30146-list-equality), post-1.0
- ~~[First and last](./§30-Unresolved%20Questions%20Index.md#30147-first-and-last)~~ Decided: neither.
- [Induction over an index](./§30-Unresolved%20Questions%20Index.md#30148-induction-over-an-index), post-1.0

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
  error and exit code 70, as `on_zero fail` does ([10.2.1](./§10-Numeric%20Semantics.md#1021-the-on_zero-clause)). `m[k] on_missing refuse r` ends the method with the
  refusal `r` instead, as `on_zero refuse` does: only in a method that declares a refusal, and from a
  lookup that stands on its own, so not from a compound that reads an element ([16.3.1](./§16-Methods.md#1631-refusals)).
  - A read without a clause is `PC0115`. It is the choice `on_zero` makes for a zero divisor, made
    where the read is written.
  - **The fallback is evaluated only when the key is missing**, so a fallback that would end the
    program does not end one whose key was there. So is a refusal. It must already have the map's value type
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
  local does, so `counts[5]` is a key of a map of `int32` keys. Only a map and a list are indexed
  (`PC0113`), a list by an index ([14.1](./§14-Repeated%20Fields%20and%20Collections.md#141-supported-operations)). A map's keys have no end to count from, so
  `m[^k]` is `PC0155`.
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
because an eager one ends a program whose key was present, and `on_zero`'s and `on_unknown`'s are
lazy for the same reason ([10.2.1](./§10-Numeric%20Semantics.md#1021-the-on_zero-clause), [12.1](./§12-Enums.md#121-an-enums-number)).

Open Questions:

- ~~Is map iteration order specified or explicitly unspecified?~~ Decided: no map is iterated (above).
- ~~What map operations are portable?~~ Decided: the ones above.
- [A map built from repeated entries](./§30-Unresolved%20Questions%20Index.md#30145-a-map-built-from-repeated-entries), post-1.0
- [Message equality](./§30-Unresolved%20Questions%20Index.md#30133-message-equality), which a map of messages has when a message does
