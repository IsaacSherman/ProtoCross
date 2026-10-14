## 19. Error Handling Without Exceptions

ProtoCross has no exceptions. The implemented failure model is:

- Compile-time rejection where portability cannot be guaranteed.
- `on_zero <fallback>` for recoverable integer division and modulo by zero.
- `on_zero fail` for deterministic terminal failure with exit code 70.
- `on_unknown <fallback>` for a number a conversion's enum does not name, and `on_unknown fail`,
  or a closed enum's default, for the same terminal failure ([12.1](./§12-Enums.md#121-an-enums-number)).
- A factory's refusal: a factory's method may declare a type it refuses with,
  `-> Invoice refuses InvoiceRefusal`, and end with `refuse value;` rather than its result. Every call
  to it says what a refusal makes of it: a fallback, `fail`, or `refuse`, which passes the same refusal
  on ([16.3.1](./§16-Methods.md#1631-refusals)).

No built-in `Result` type, generated status type, or catchable runtime error model exists. A refusal is
declared by the method and answered at the call, and no value holds one, so it needs neither a generic
type nor a type of the language's own.

Open Questions:

- Does ProtoCross define a built-in `Result` type? `I think we should- it doesn't have to be used, but it should be there for ease of use. ~IS`
  Note: for a factory, a refusal ([16.3.1](./§16-Methods.md#1631-refusals)) is that `Result` at the one place it is needed, a method's
  boundary, with the author's type for the error and no generics. A `Result` a value can hold, and a
  refusal from a method outside a factory, remain open.
- Are arithmetic errors expressible in the type system? `Yes, it should be a fairly expansive enum. ~IS`
  Note: overflow is no longer one of them. Since 10.1 defines overflow as wrapping, it is a defined
  result rather than a failure, and nothing about it needs to reach the type system.
  Note: no built-in enum of them exists. A factory that must not end the program for a zero divisor
  refuses with its own type before it divides ([16.3.1](./§16-Methods.md#1631-refusals)). Whether `on_zero fail` may become a refusal
  is #16's to decide.
- Can methods be declared as total, meaning they cannot fail at runtime? `I lean toward no, but this might be a nice optimization at some point. ~IS`
- How do target backends map error results idiomatically while preserving semantics?  `Error handling is heavily on the user to define; we'll provide the Result type with primitive error enums for primitive "exceptions" such as dividing by zero. More advanced stuff?  Users can (and should!) tailor that to their specific needs. ~IS`
  Note: a refusal is mapped to each target's try pattern, a method that returns whether it built its
  result and gives the result or the refusal through a parameter ([24.1](./§24-Generated%20API%20Strategy.md#241-c), [24.2](./§24-Generated%20API%20Strategy.md#242-c)). The refusal's
  type is the author's, as this asks.
