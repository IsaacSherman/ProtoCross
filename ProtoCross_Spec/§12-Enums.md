## 12. Enums

**Decided: enum types and enum values are both named through the protobuf type universe.**

An enum type can be named wherever a type is expected, and an enum value is named
`<enum type>.<VALUE_NAME>`:

```protocross
extend Order {
    fn is_shipped() -> bool {
        return status == OrderStatus.SHIPPED;
    }

    fn shipped() -> OrderStatus {
        return OrderStatus.SHIPPED;
    }
}
```

Normative Requirements:

- Both the type and the value are resolved by full name or by an unambiguous simple name, including
  enums nested in messages. A simple name matching more than one type is `PC0074`; a name that is
  not a value of the named enum is `PC0076`.
- The value name is the one the `.proto` file declares, exactly as written. ProtoCross does not
  re-spell it, even though both backends do.
- A name that is in scope as a value wins over an enum type spelled the same way, so
  `something.field` stays a field access. Adding an enum to a schema must not silently change what
  an existing expression means.
- Enum values are ordinary expressions, so they are equally available in a `test` fixture and in an
  expectation. A fixture sets an enum field from a named value, where a message field takes a
  literal ([13.2](./§13-Messages.md#132-message-construction)).
- Enums compare only for equality. Ordered comparison is rejected, because the numbers behind the
  values are a wire detail rather than a ranking the schema author asked for.

Backend obligations, because the two targets name a value in unrelated ways and neither spelling is
derivable from the other:

| ProtoCross | C# | C++ |
|---|---|---|
| `TopLevelStatus.TOP_LEVEL_STATUS_OK` | `TopLevelStatus.Ok` | `TOP_LEVEL_STATUS_OK` |
| `Outer.Nested.NESTED_SOME` | `Outer.Types.Nested.Some` | `Outer_Nested_NESTED_SOME` |
| `Outer.Inner.Deep.DEEP_NONE` | `Outer.Types.Inner.Types.Deep.None` | `Outer_Inner_Deep_DEEP_NONE` |

- **C#** strips the enum's own name from the front of the value, ignoring case and underscores, and
  PascalCases what is left. A value that does not carry the prefix keeps its whole name, and one
  where stripping would leave a leading digit gains an underscore. Backends must reproduce this
  exactly rather than approximate it: a near-miss names an identifier that does not exist, which
  fails in the consumer's build rather than in this compiler.
- **C++** keeps the declared spelling but places values at namespace scope, prefixing a nested
  enum's values with the flattened enum type name and leaving a top-level enum's values bare.
  protoc also emits a `static constexpr` member on the containing class, but the namespace-scope
  constant is the one every enum has.
- protobuf C++ enums additionally carry `_INT_MIN_SENTINEL_DO_NOT_USE_` values that are not part of
  the schema and must never be emitted. They will matter again for `switch`.

Open Questions:

- Should enum exhaustiveness be checked? proto3 enums are open -- a field may legally hold a number
  with no declared value -- so no switch over one is exhaustive at runtime regardless of the schema.
- ~~Should unknown enum values be representable?~~ Decided: yes. An open enum keeps a number it
  does not name, and a conversion or a fixture can make one ([12.1](#121-an-enums-number)).
- ~~Should an enum be convertible to or from an integer?~~ Decided: to and from `int32`, with `as`
  ([12.1](#121-an-enums-number)).

### 12.1 An Enum's Number

**Decided: an enum's number is an `int32`, converted with `as` in either direction, and a number the
enum does not name becomes what protobuf makes of it unless the conversion says otherwise.**

```protocross
extend Order {
    fn status_code() -> int32 {
        return status as int32;
    }

    fn status_from(code: int32) -> OrderStatus {
        return code as OrderStatus on_unknown OrderStatus.UNSPECIFIED;
    }
}
```

Normative Requirements:

- `status as int32` is the number behind an enum value, and `n as OrderStatus` is the value an
  `int32` numbers. An enum converts to no other type and from no other type, its own included:
  protobuf's enum number is an `int32`, and ProtoCross adds nothing protobuf does not have. A wider or
  unsigned number is converted to `int32` first, under 10.3's wrapping:
  `count as int32 as OrderStatus`. Anything else is `PC0075`, and its help names that step where it
  applies.
- An integer literal converted to an enum is an `int32`, so `7 as OrderStatus` needs no step in
  between, and one no `int32` holds is `PC0036`. This is the one place a converted literal does not
  keep its natural type ([10.3](./§10-Numeric%20Semantics.md#103-numeric-conversions)). A conversion to an enum has no wrapping to keep.
- Values still compare only for equality. `a as int32 < b as int32` compares two numbers, and says
  so.
- A number the enum does not name is a value of the enum too, equal to none of its names. What a
  conversion makes of one is said by an `on_unknown` clause after the type:
  - `on_unknown <value>` substitutes that value. It must already be a value of the enum converted to
    (`PC0103`), and it may be any expression of that type: a named value, a parameter, a field.
  - `on_unknown fail` terminates the program exactly as `on_zero fail` does
    ([10.2.1](./§10-Numeric%20Semantics.md#1021-the-on_zero-clause)), with a diagnostic on standard error naming the enum and the number, and exit
    code 70.
  - The clause binds to the one conversion it follows. Its fallback is a postfix expression, so an
    `as` after it converts the whole conversion: `n as Level on_unknown Level.LOW as int32` is a
    number again.
  - `on_unknown` on a conversion to anything but an enum is `PC0101`. Its fallback is still checked.
- With no clause, a conversion does what protobuf does with such a number:
  - An **open** enum keeps it, and the conversion carries `PC5000`, a note saying so. Converted back,
    the number is the one it was, in every backend.
  - A **closed** enum ends the program, as `on_unknown fail` would, and the conversion is `PC0102`, a
    warning that nothing says so. Protobuf will not hold such a number in a closed enum's field, and
    C++ asserts as much in a debug build, so a conversion that made one would invent a value the
    schema rules out.
- A proto2 enum is closed and a proto3 enum is open. An editions enum is closed where its
  `enum_type` feature says `CLOSED`, stated on the enum or else on its file. protoc accepts the
  feature nowhere between the two, and every edition so far defaults to open.
- `on_unknown` is not a keyword. It begins a clause only where `fail` or a name follows it, and is
  an identifier anywhere else ([6.4](./§6-Lexical%20Structure.md#64-keywords)). Every value of an enum is written starting with a name, so
  no fallback is lost to the rule.
- A fixture writes a number no value names through the same conversion, `status: 9 as OrderStatus`,
  and an expectation compares with one the same way, so a test reaches the case with no syntax of
  its own ([25.3](./§25-Testing%20and%20Conformance%20Vectors.md#253-author-written-protocross-unit-tests)).

Backend obligations:

| ProtoCross | C# | C++ |
|---|---|---|
| `status as int32` | `(int)status` | `static_cast<std::int32_t>(status)` |
| `n as Level`, kept | `(Level)n` | `static_cast<Level>(n)` |
| `on_unknown <value>` | `ProtoCrossEnums.NamedOr((Level)n, value)` | `protocross_runtime::named_or(static_cast<Level>(n), &Level_IsValid, value)` |
| `on_unknown fail` | `ProtoCrossEnums.NamedOrFail((Level)n, "pkg.Level")` | `protocross_runtime::named_or_fail(static_cast<Level>(n), &Level_IsValid, "pkg.Level")` |

- Whether a number is named is asked of what protoc generated: `Enum.IsDefined` over the C# enum, and
  the `_IsValid` protoc declares beside the C++ one. Both hold exactly the values the schema
  declares, aliases included, so the backends agree without either writing the list out.
- C# stores any `int` in an enum, and protoc declares every C++ enum over `int`, so a kept number
  survives the cast in both.
- What a conversion does with such a number is stamped on it by the binder. A backend never works
  out for itself whether an enum is closed.

Rationale: protobuf has already decided what each kind of enum does with a number it does not name,
and a conversion that decided differently would be a second answer to one question. A clause
required on every conversion, as `on_zero` is on every integer division, was considered and
rejected: most conversions are of numbers the author already trusts, and a clause written on all of
them says nothing about any. The note and the warning say what the default is, where a reader cannot
otherwise see it.
