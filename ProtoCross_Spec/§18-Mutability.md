## 18. Mutability

**Decided: a method declared `mut fn` may change its receiver, any method may change the messages
it holds in locals, and nothing may change a parameter.**

```protocross
extend Invoice {
    mut fn apply_discount(cents: int64) {
        discount_cents += cents;
        status = InvoiceStatus.INVOICE_STATUS_ADJUSTED;
        audit.last_change_cents = cents;
    }

    fn total_cents() -> int64 {
        return subtotal_cents - discount_cents;
    }
}
```

A method is read-only unless it is declared `mut fn`. That is what nearly every method is, a
parameter can only ever be given to one, and a method that changes nothing keeps the signature it
always had ([24](./§24-Generated%20API%20Strategy.md#24-generated-api-strategy)). Side effects stay legible: a call
changes the message it is made on and nothing else its caller can see, and never a message passed to
it.

Normative Requirements:

- **What a method may change.** A method may change a message it owns:
  - its receiver, if it is a `mut fn`;
  - a message held in a local, which is the method's own copy ([13.2](./§13-Messages.md#132-message-construction));
  - a singular field of either, at any depth, and an element of a repeated field of either, through
    the name a `for` binds to it.

  It may change nothing else. A parameter, and anything reached through one, is read-only, and so is
  the receiver of a method that is not `mut`. A call's result and a literal are held by nothing once
  the statement is over, so a change to one would be lost. A change to any of these is `PC0094`.
- **Assignment.** `place = value;` and `place op= value;` ([9.2](./§9-Expressions%20and%20Operators.md#92-operators)) assign a local
  declared with `var`, or a singular field of a message the method may change: `total = 5;`,
  `customer.name = "x";`.
  - A parameter and the name a `for` binds cannot be assigned, nor can anything that is neither a
    local nor a field. Each is `PC0034`, and the help for the first two is to copy the value into a
    local.
  - A repeated field cannot be assigned (`PC0034`). A message literal gives one its elements
    ([13.2](./§13-Messages.md#132-message-construction)).
  - Writing through a message field that is unset sets it, as protobuf's mutable accessors do:
    `audit.flagged.cents = 1;` sets `audit` and `audit.flagged` when either is unset. So the links of
    a target are not reads, and need no guard ([13.1](./§13-Messages.md#131-field-access)). A compound assignment
    also reads its target, and that read is guarded like any other.
  - A message assigned is stored as a copy, unless it is a literal
    ([13.2](./§13-Messages.md#132-message-construction)).
  - Assigning one member of a `oneof` unsets the others, as setting one does in protobuf.
  - An assignment reaches its target first, setting any unset message on the way, and then
    evaluates its value ([9.3](./§9-Expressions%20and%20Operators.md#93-evaluation-order)).
- **Mutating calls.** A `mut fn` may be called only on a message the calling method may change
  (`PC0094`). Calling one on a message field of the receiver needs that field's guard, as any call on
  a message field does ([13.1](./§13-Messages.md#131-field-access)).
  - A call to a `mut fn` stands on its own: it is a statement, a `var`'s initializer, a local's new
    value, or the value a `return` returns. Anywhere else -- an operand, an argument, a condition, a
    field's new value -- it is `PC0095`. Nothing else is evaluated beside it, so the operand order that
    [9.3](./§9-Expressions%20and%20Operators.md#93-evaluation-order) leaves open cannot be seen through a change.
  - An argument cannot share a message with the receiver the call changes: be it, be part of it, or
    hold it (`PC0097`). Its parameter is read-only, and one that was part of the receiver would change
    under the method that was promised it could not. Messages, repeated values, strings and bytes are
    checked, because a target may pass each by reference; a number is passed as a value. The names
    two `for` loops bind over one field are taken to share, since they may be one element.
  - A message argument that is not a place -- a call's result, or something read from one -- is passed
    as a copy, because it may be part of the receiver.
- **Loops.** While a `for` traverses a repeated field, nothing inside it may change that field's
  membership, order or identity ([14.1](./§14-Repeated%20Fields%20and%20Collections.md#141-supported-operations)).
- **Locals.** A local holds a message of its own. Initializing or assigning one with a message, or
  with a repeated value, that is not a literal stores a copy
  ([13.2](./§13-Messages.md#132-message-construction)).
- No field can be cleared, and no element can be removed from a repeated field. So nothing a guard has
  shown to be set becomes unset, except by an assignment to another member of its `oneof`, or by the
  message holding it being replaced ([13.1](./§13-Messages.md#131-field-access)).

Current Status:

- Both backends generate every change above, and the `mutating_methods` conformance vector runs
  each of them in both ([25.2](./§25-Testing%20and%20Conformance%20Vectors.md#252-conformance-vector-format)).
- C# changes a message through protoc's properties, from the extension methods it always emitted
  ([24.1](./§24-Generated%20API%20Strategy.md#241-c)). C++ passes the receiver of a `mut fn` as `T&`, and changes a message
  through protoc's setters and mutable accessors ([24.2](./§24-Generated%20API%20Strategy.md#242-c)).
- Appending to a repeated field is decided, and is not implemented yet: until it is, a repeated field
  changes only through its elements ([14.1](./§14-Repeated%20Fields%20and%20Collections.md#141-supported-operations)).
