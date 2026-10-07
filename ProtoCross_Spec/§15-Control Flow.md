## 15. Control Flow

### 15.1 Conditional Statements

```protocross
if condition {
    ...
} else if other_condition {
    ...
} else {
    ...
}
```

Normative Requirements:

- The condition is an expression of type `bool`. There is no truthiness: a numeric, string, or
  message value in condition position is a diagnostic (PC0071), not a shorthand for a comparison.
- The condition is unparenthesized and every branch is braced. There is no single-statement form,
  so no statement can dangle off an `if`.
- An `else` binds to the nearest unmatched `if`. `else if` is a chain rather than a block
  containing a nested `if`, and generated code preserves that shape.
- A method that declares a return type must not be able to reach the end of its body. An `if`
  guarantees that only when it has an `else` and every branch guarantees it.

### 15.2 Loops

The current implementation has two loop forms:

```protocross
while condition {
    ...
}

for item in collection {
    ...
}
```

Normative Requirements:

- A `while` condition is an expression of type `bool`, under the same rule as 15.1.
- `for` iterates a protobuf repeated field in field order ([14](./§14-Repeated%20Fields%20and%20Collections.md#14-repeated-fields-and-collections)).
- `break` leaves the innermost enclosing loop or `switch` ([15.3](#153-switch)), whichever is nearer,
  and `continue` advances the innermost enclosing loop to its next iteration. A switch is not a loop,
  so a `continue` in one of its arms continues the loop around it. A `break` with no loop or switch
  around it is PC0072, and a `continue` with no loop around it is PC0073.
- The compiler performs no termination analysis. `while true` is legal, and a method whose only
  exit is a `return` inside `while true` satisfies the missing-return check, because control
  cannot reach the end of the body. A `break` that can leave that loop makes the end reachable
  again, and the method then needs a return after it. A `break` in an arm of a switch inside the
  loop leaves the switch and not the loop, so it is not one of those.

Open Questions:

- ~~Should numeric `for` loops exist?~~ Decided: yes. `Yes. ~IS` Not yet implemented; `for`-`in`
  remains the only `for` form.
- ~~Should `break` and `continue` be supported?~~ Decided: yes, and implemented. `Yes. ~IS`
- ~~Should loops require static termination checks?~~ Decided: no. `No. ~IS`

### 15.3 Switch

**Decided: `switch` chooses one braced arm by an integer or an enum, nothing falls from one arm into
the next, and the default arm is optional.**

```protocross
extend Order {
    fn priority() -> int32 {
        switch status {
            case OrderStatus.SHIPPED, OrderStatus.DELIVERED {
                return 0;
            }
            case OrderStatus.PENDING {
                return 2;
            }
            default {
                return 1;
            }
        }
    }
}
```

Normative Requirements:

- `switch` is a statement. It is never an expression and never an operator.
- The subject is an expression of an integer type or an enum type, unparenthesized as an `if`
  condition is. Anything else is PC0106. It is evaluated once, before an arm is chosen.
- The subject is not a constant. A value built from literals and enum values alone, arithmetic on
  them included, runs the same arm every time, and is PC0112.
- A switch lists at least one `case`. One with no arms, or with only a default arm, decides nothing
  by its value, and is PC0111. A switch the parser found unfinished, missing a brace or holding
  something that is not an arm, is one still being typed: the parser has reported it, and it is not
  told as well that it lists no case.
- Every arm is braced. A `case` lists one or more values separated by commas, and runs for any of
  them. The `default` arm lists none, and runs for every value no `case` lists. Nothing falls from
  one arm into the next, so no `break` is needed to end an arm, and each arm is a block with a scope
  of its own.
- A case lists constants: an integer literal, a `-` written on it included, or a value of the
  subject's enum, written `Enum.VALUE` ([12](./§12-Enums.md#12-enums)). A literal takes the subject's type and has to fit
  it, as a literal assigned to a local of that type does ([10.3](./§10-Numeric%20Semantics.md#103-numeric-conversions)). Anything that is not a constant is
  PC0107, a conversion included, and a constant of another type is PC0108. A number its enum does
  not name ([12.1](./§12-Enums.md#121-an-enums-number)) cannot be listed, and runs the `default` arm.
- No number is listed twice in one switch, in one arm or across two. That holds whether it is
  written the same way twice, in two spellings of one literal, or as two names an enum gives one
  number (`allow_alias`). The second listing is PC0109.
- The default arm is optional. A switch without one runs nothing for a value no case lists, as an
  `if` with no `else` runs nothing when its condition is false. Where there is one it is the last arm
  and the only default: a `default` arm with any arm after it is PC0110, which a second default
  always makes the first.
- No switch is checked for exhaustiveness. A proto3 enum is open, so a value can hold a number no
  case could list, and listing every name does not make a switch over one exhaustive. A switch that
  lists fewer than every name is not wrong.
- A `break` in an arm leaves the switch, and a `continue` there continues the innermost loop
  ([15.2](#152-loops)). A switch guarantees a return ([15.1](#151-conditional-statements)) only when it has a `default` arm and every
  arm guarantees one, since an arm that a `break` can leave goes on past the switch.
- `switch`, `case` and `default` are reserved words ([6.4](./§6-Lexical%20Structure.md#64-keywords)). Reserving `default` takes the name from
  any schema field or package component called `default`, which no source can write, and the
  owner accepted that. The rest of a message holding such a field can be used as before.

Backend obligations, because each target has a switch of its own and neither lets an arm end as
ProtoCross does:

| ProtoCross | C# and C++ |
|---|---|
| `switch x { ... }` | `switch (x) { ... }` |
| `case A, B { body }` | `case A: case B: { body break; }`, the `break;` only where the body can reach its end |
| `default { body }` | `default: { body break; }`, likewise |
| no `default` arm | in C++ only, `default: { break; }` |

- Each arm is a braced section of the target's own switch, and its values are that section's labels.
  A section ends in `break;` exactly where its arm can reach its end. C++ would fall into the next
  section there. C# refuses a section whose end can be reached, and a build with warnings as errors
  refuses a `break` that cannot be reached. Whether an arm can reach its end is asked of the IR
  ([22.2](./§22-IR%20and%20Compiler%20Architecture.md#222-typed-ir-requirements)), the question the missing-return check asks.
- Where an arm holds a branch or loop whose condition is built from literals and enum values alone,
  C# may find the arm's end unreachable where ProtoCross does not, so C# writes that arm's `break;`
  with its unreachable-code warning (CS0162) suspended around it. [24.1](./§24-Generated%20API%20Strategy.md#241-c) says why, and why
  the `break;` is not left out instead.
- A `break` or `continue` the author wrote is written as it stands. In both targets a `break` in a
  switch leaves the switch, and a `continue` there continues the loop around it, so neither needs
  rewriting.
- protoc gives every C++ enum two sentinel values the schema does not declare, so `-Wswitch` warns
  about a switch over an enum with no `default` however many values it lists. C++ adds a default arm
  that does nothing, which is what matching nothing does anyway, and a consumer building with
  warnings as errors can build the output.

Rationale: The owner decided the shape on #6. A switch is a statement only. It is on integers and
enums only, because a case compares a constant by number. Its arms are braced and never fall
through, matching 15.1's rule that every branch is braced, and `default` is reserved to spell the
default arm. That was preferred to a contextual `default`, to `else` and to `case _`. The owner
first decided that an arm's `break` acted on the loop around the switch. Building it showed what that
costs, and the owner reversed it: a switch in a loop would leave the loop from an arm, which a reader
of either target takes to leave the switch, and both backends would have had to write a `goto` to a
label after the loop. Emitting the switch as an `if` / `else if` chain would have made either rule
easy to write. It was rejected because the output would not read like the source.
