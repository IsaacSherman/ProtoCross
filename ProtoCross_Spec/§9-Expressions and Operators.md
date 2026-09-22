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
- Parenthesized expressions.

Not implemented:

- Top-level function calls.
- Indexing.
- Message literals in ordinary method bodies.
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
=
```

`has` is a prefix operator on a field, producing `bool` ([8.4](./§8-Type%20System.md#84-nullability-and-presence)). It sits at the same precedence as
`not`, and unlike every other operator its operand is a field rather than a value -- reading the
value is exactly what it must not do.

Normative Requirements:

- Both word and symbolic boolean operators are accepted: `and`/`&&`, `or`/`||`, and `not`/`!`.
- Assignment is a statement only.
- On integer operands, `%` follows the same `on_zero` rule as integer `/`. On floating-point
  operands it is the truncated remainder of [10.2](./§10-Numeric%20Semantics.md#102-division), which cannot fail and takes no clause.

**Decided: the C-family precedence order.**

From the tightest binding to the loosest. Every binary operator is left-associative.

| Operators | Kind |
|---|---|
| `-` `not` `!` `~` `has` | Prefix |
| `as` | Conversion ([10.3](./§10-Numeric%20Semantics.md#103-numeric-conversions)) |
| `*` `/` `%` | Multiplicative |
| `+` `-` | Additive |
| `<<` `>>` | Shift |
| `<` `<=` `>` `>=` | Relational |
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

### 9.3 Evaluation Order

Normative Requirement:

- The evaluation order of expressions must be explicitly defined.
- Backends must preserve the specified evaluation order.

Current defined subset:

- Method call arguments evaluate left to right.
- Boolean `and` and `or` short-circuit left to right.
- Assignment evaluates the right-hand side before storing the result.

Open Question:

- Should all non-short-circuit binary operators evaluate the left operand before the right operand?
  This only becomes observable when an operand can terminate through `on_zero fail`.
