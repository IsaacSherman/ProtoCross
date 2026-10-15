## 19. Error Handling Without Exceptions

ProtoCross has no exceptions. The implemented failure model is:

- Compile-time rejection where portability cannot be guaranteed.
- `on_zero <fallback>` for recoverable integer division and modulo by zero.
- `on_zero fail` for deterministic terminal failure with exit code 70.
- `on_unknown <fallback>` for a number a conversion's enum does not name, and `on_unknown fail`,
  or a closed enum's default, for the same terminal failure ([12.1](./§12-Enums.md#121-an-enums-number)).
- A refusal: any method may declare a type it refuses with, `-> Invoice refuses InvoiceRefusal`, and end
  with `refuse value;` rather than its result. Every call to it says what a refusal makes of it: a
  fallback, `fail`, or `refuse`, which passes the same refusal on. A division, a conversion to an enum
  and a lookup may refuse where they have no value, `on_zero refuse value`
  ([16.3.1](./§16-Methods.md#1631-refusals)).
- A project's choice whether anything may end the program at all: `Failure/Termination = Forbidden`
  makes every construct that would end it a compile error, so a project whose code runs inside a
  process it does not own declines through refusals only ([10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy)).

No built-in `Result` type, generated status type, or catchable runtime error model exists. A refusal is
declared by the method and answered at the call, and no value holds one, so it needs neither a generic
type nor a type of the language's own.

**Decided: generated code may end the program it runs in, unless its project forbids it.** Ending the
program stays the meaning of `fail`, because it is the one failure that means the same thing in every
target (10.2.1), and a silently wrong substitute may be worse than a loud stop. A project whose code runs
inside a server, a daemon or a batch job it does not own forbids it once, in `protocross.config.xml`,
and every method that would have ended the program refuses instead. The setting refuses source rather
than reinterpreting it, so one source never means two things.

Open Questions:

- Does ProtoCross define a built-in `Result` type? `I think we should- it doesn't have to be used, but it should be there for ease of use. ~IS`
  Note: a refusal ([16.3.1](./§16-Methods.md#1631-refusals)) is that `Result` at the one place it is needed, a method's boundary, with
  the author's type for the error and no generics, and every method may declare one. A `Result` a value
  can hold remains open.
- Are arithmetic errors expressible in the type system? `Yes, it should be a fairly expansive enum. ~IS`
  Note: overflow is no longer one of them. Since 10.1 defines overflow as wrapping, it is a defined
  result rather than a failure, and nothing about it needs to reach the type system.
  Note: no built-in enum of them exists. A method that must not end the program for a zero divisor
  refuses with its own type, `on_zero refuse value` ([16.3.1](./§16-Methods.md#1631-refusals)). `on_zero fail` does not become a refusal
  by itself, since only the author can say what the refusal is.
- Can methods be declared as total, meaning they cannot fail at runtime? `I lean toward no, but this might be a nice optimization at some point. ~IS`
  Note: a project may declare all of its methods so, `Failure/Termination = Forbidden` ([10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy)). A
  `fails` marker on each method that may end the program was considered and lost: it makes the
  capability visible but lets no host opt out, and the project setting does both.
- How do target backends map error results idiomatically while preserving semantics?  `Error handling is heavily on the user to define; we'll provide the Result type with primitive error enums for primitive "exceptions" such as dividing by zero. More advanced stuff?  Users can (and should!) tailor that to their specific needs. ~IS`
  Note: a refusal is mapped to each target's try pattern, a method that returns whether it built its
  result and gives the result or the refusal through a parameter ([24.1](./§24-Generated%20API%20Strategy.md#241-c), [24.2](./§24-Generated%20API%20Strategy.md#242-c)). The refusal's
  type is the author's, as this asks.
