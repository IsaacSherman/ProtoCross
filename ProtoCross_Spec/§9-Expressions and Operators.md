## 9. Expressions and Operators

### 9.1 Expression Categories

The language currently includes:

- Integer, floating-point, string, and boolean literals.
- Local, loop-binding, parameter, and implicit receiver field references.
- Field access through `.`.
- Method calls on message receivers.
- Arithmetic, boolean, comparison, and bitwise expressions.
- Prefix field-presence checks with `has`.
- Explicit numeric conversions with `as`.
- Message literals, `new T { field: value, … }` ([13.2](./§13-Messages.md#132-message-construction)).
- A map's value at a key, `prices[sku] on_missing 0`, whether it holds one, `sku in prices`, and
  `count()` and `is_empty()` ([14.2](./§14-Repeated%20Fields%20and%20Collections.md#142-maps)).
- Parenthesized expressions.

Not implemented:

- Top-level function calls.
- Indexing anything but a map. A repeated field has none.
- Bytes literals.
- Math intrinsics such as `abs`, `min` and `max`. **Post-1.0.** Each can be written today with a
  comparison, and how an intrinsic is named and found belongs with the open question of top-level
  functions ([7.1](./§7-Grammar%20and%20Syntax.md#71-implemented-grammar)).

### 9.2 Operators

Implemented operator set:

```text
+  -  *  /  %
== != < <= > >=
and or not
&& || !
&  |  ^  ~  <<  >>
has
in
=
+=  -=  *=  /=  %=  &=  |=  ^=  <<=  >>=
```

`has` is a prefix operator on a field, producing `bool` ([8.4](./§8-Type%20System.md#84-nullability-and-presence)). It sits at the same precedence as
`not`, and unlike every other operator its operand is a field rather than a value -- reading the
value is exactly what it must not do.

`in` asks whether an enum value is one its enum names, `status in OrderStatus`, producing `bool`
([12.2](./§12-Enums.md#122-whether-a-value-has-a-name)), or whether a map holds a key, `sku in prices` ([14.2](./§14-Repeated%20Fields%20and%20Collections.md#142-maps)). Its right side is a type for
the first and a map for the second, which is told apart as `Level.HIGH` is, and it binds as a
relational operator does.

`m[k]` reads the value a map holds at a key, with an `on_missing` clause saying what a missing key
gives, and binds as a member access or a call does ([14.2](./§14-Repeated%20Fields%20and%20Collections.md#142-maps)). It is the one indexing the language has.

Normative Requirements:

- Both word and symbolic boolean operators are accepted: `and`/`&&`, `or`/`||`, and `not`/`!`.
- Assignment is a statement only, and so is a compound assignment.
- On integer operands, `%` follows the same `on_zero` rule as integer `/`. On floating-point
  operands it is the truncated remainder of [10.2](./§10-Numeric%20Semantics.md#102-division), which cannot fail and takes no clause.
- A comparison takes two operands of one type (`PC0048`), and is a `bool`. `<`, `<=`, `>` and `>=`
  compare numbers only (`PC0049`). `==` and `!=` compare any scalar or enum, but not two messages
  or two repeated values (`PC0098`), until [13.3](./§13-Messages.md#133-equality) says what makes
  two of those equal. They compare two maps by their keys and values, in any order, where the values
  have equality themselves ([14.2](./§14-Repeated%20Fields%20and%20Collections.md#142-maps)).

**Decided: the C-family precedence order.**

From the tightest binding to the loosest. Every binary operator is left-associative.

| Operators | Kind |
|---|---|
| `-` `not` `!` `~` `has` | Prefix |
| `as` | Conversion ([10.3](./§10-Numeric%20Semantics.md#103-numeric-conversions)) |
| `*` `/` `%` | Multiplicative |
| `+` `-` | Additive |
| `<<` `>>` | Shift |
| `<` `<=` `>` `>=` `in` | Relational |
| `==` `!=` | Equality |
| `&` | Bitwise and |
| `^` | Bitwise exclusive or |
| `\|` | Bitwise or |
| `and` `&&` | Logical and |
| `or` `\|\|` | Logical or |

It is the order C# and C++ both use, so an expression means what a reader of either target already
takes it to mean. The cost is the one C pays: a comparison binds tighter than `&`, `^` and `|`, so
`x & mask == 0` is `x & (mask == 0)`. That is a type error rather than a change of meaning, because a
comparison is a `bool` and a bitwise operand is an integer (`PC0085`), and its help says to write
`(x & mask) == 0`.

**Decided: bitwise and shift operators on integers only.**

Normative Requirements:

- `&`, `|`, `^` and `~` take integer operands, and nothing else: `PC0085` for a binary operator and
  `PC0086` for `~`. A `bool` is not a bit, and two of them combine through the logical operators,
  which a `bool` operand's help names.
- The two operands of `&`, `|` and `^` must have the same type, as for every other binary operator
  (`PC0048`), and the result has that type.
- `<<` and `>>` shift an integer value by an integer count. **The count may have any integer type,
  whatever the value's is**, because it is a count and not an operand of the arithmetic. The result
  has the value's type. What a count means, and what a shift does at the width, is
  [10.1](./§10-Numeric%20Semantics.md#101-integer-overflow)'s.
- Neither side of a shift is offered the other's type. A literal value adopts the type expected of
  the shift, and a literal count takes its natural type
  ([10.3](./§10-Numeric%20Semantics.md#103-numeric-conversions)). So in `1 << bit`, where `bit` is
  an `int32` and a `uint64` is expected, the `1` is a `uint64`; and where `x` is an `int32`,
  `x << 3000000001` shifts it by 1, where a count that took the value's type would be out of range.
- A literal operand of a bitwise or shift operator adopts the type expected of the result only where
  that is an integer type. Where a `double` is expected of `1 & 3`, the literals stay `int64`, and what
  is reported is that the result is not a `double`, rather than that `&` cannot take one.
- No overflow policy governs any of them ([10.1](./§10-Numeric%20Semantics.md#101-integer-overflow)).

**Decided: compound assignment is sugar for its long form.**

`x op= y` stores `x op y` in `x`, for each arithmetic, bitwise and shift operator: `+=`, `-=`, `*=`,
`/=`, `%=`, `&=`, `|=`, `^=`, `<<=` and `>>=`.

Normative Requirements:

- A compound assignment means exactly what its long form `x = x op (y)` means: the same operand
  rules, the same literal typing, the same overflow policy ([10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy)) and the same
  refusals. It is bound as that long form, so no backend sees anything else, and each emits for it
  what it emits for the long form.
- **The right side is one operand**, however loosely its own operators bind: `x *= a + b` is
  `x = x * (a + b)`, and `x &= a | b` is `x = x & (a | b)`.
- The target rule is `=`'s: a local variable, a field of a message the method may change, or an
  element of a map it may change ([18](./§18-Mutability.md#18-mutability)). An element is read as well, so it carries
  the read's clause: `counts[word] on_missing 0 += 1;` ([14.2](./§14-Repeated%20Fields%20and%20Collections.md#142-maps)). A parameter, the name a `for` binds and anything else that is neither is
  `PC0034`, and a field of a message the method may not change is `PC0094`.
- An integer `/=` or `%=` takes an `on_zero` clause after its divisor, as `/` and `%` do
  ([10.2.1](./§10-Numeric%20Semantics.md#1021-the-on_zero-clause)): `x /= d on_zero 0;`. The clause binds to the division it follows,
  so a divisor with an operator of its own is parenthesized. In `x /= a + b on_zero 0` the clause
  follows the `+`, which cannot take one (`PC0015`), and leaves the `/=` without one (`PC0054`);
  `x /= (a + b) on_zero 0` is what was meant. A divisor that is a non-zero literal needs no clause.
- A literal on the right takes the target's type, as it would beside the target in the long form,
  and a shift count keeps its own ([10.3](./§10-Numeric%20Semantics.md#103-numeric-conversions)).
- A diagnostic about the operation names the operator as it was written:
  `Cannot apply '+=' to 'int64' and 'double'`. No logical operator and no comparison has a
  compound form, so `&=`, `|=` and `^=` on two `bool`s are `PC0085`, and the help writes the long
  form out with `and`, `or` or `!=`.
- The target is one name written once, and is recorded once, as a write
  ([22.3](./§22-IR%20and%20Compiler%20Architecture.md#223-what-a-compilation-answers)), although the operation it stands for reads it too.

### 9.3 Evaluation Order

Normative Requirement:

- The evaluation order of expressions must be explicitly defined.
- Backends must preserve the specified evaluation order.

Current defined subset:

- Method call arguments evaluate left to right.
- A message literal's values evaluate in the order their fields are written, left to right, as a
  call's arguments do, and a list's elements in order. The order of the schema's field numbers plays
  no part ([13.2](./§13-Messages.md#132-message-construction)).
- A message literal is evaluated where it is written, as any other operand is. It is never moved
  ahead of the statement that holds it, and a literal in the right operand of `and` or `or` is
  built only if that operand is evaluated. Its order relative to the operands beside it is
  whatever the expression it stands in gives them.
- Boolean `and` and `or` short-circuit left to right.
- Assignment evaluates the right-hand side before storing the result.
- An assignment to a field reaches its target before it evaluates its value, setting any unset
  message on the way ([18](./§18-Mutability.md#18-mutability)), and then stores. A value that asks `has` of a link of
  its own target therefore sees the link set. The field itself is set last, so a value that asks
  `has` of the field, or reads another member of its `oneof`, sees it as it was before the
  assignment.
- An append ([14.1](./§14-Repeated%20Fields%20and%20Collections.md#141-supported-operations)) is ordered as an assignment to a field is: it reaches its
  target, setting any unset message on the way, then evaluates its value, and adds the element
  last. A value that counts the elements therefore counts the ones that were there.
- A compound assignment reads its target, evaluates its right side, and then stores. Reading a
  local or a field cannot fail, and nothing on the right can change either, so no program can
  observe that order.
- A store to a map's element, and a change one of a map's methods makes, are ordered as an
  assignment to a field is: the map is reached, setting any unset message on the way, then the key and
  the value are evaluated, and the change is made last ([14.2](./§14-Repeated%20Fields%20and%20Collections.md#142-maps)). The key and the value are evaluated in
  the order this section leaves open for an operator's operands, and so are a map entry's in a literal.
- A lookup evaluates its map and its key, and its `on_missing` fallback only when the key is missing.
- An integer division evaluates both its operands, and its `on_zero` fallback only when the divisor
  is zero ([10.2.1](./§10-Numeric%20Semantics.md#1021-the-on_zero-clause)). A conversion to an enum evaluates its number, and its `on_unknown` fallback only
  when the enum does not name it ([12.1](./§12-Enums.md#121-an-enums-number)).
- A compound assignment written through an element of a map evaluates its read, clause and all,
  before its target is reached, since reaching it puts a message at a missing key that the read must
  not find ([14.2](./§14-Repeated%20Fields%20and%20Collections.md#142-maps)).
- A call to a `mut fn` is never evaluated beside another operand: it stands on its own as a
  statement, a `var`'s initializer, a local's new value or a returned value
  ([18](./§18-Mutability.md#18-mutability)). So no operand can observe a change made by another, whichever order they are
  evaluated in.

Open Question:

- Should all non-short-circuit binary operators evaluate the left operand before the right operand?
  This only becomes observable when an operand can terminate through `on_zero fail`. A change cannot
  make it observable, because a mutating call is never an operand.
