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

- [A `Result` a value can hold](./§30-Unresolved%20Questions%20Index.md#30192-a-result-a-value-can-hold)
- [Arithmetic errors in the type system](./§30-Unresolved%20Questions%20Index.md#30193-arithmetic-errors-in-the-type-system)
- [Total methods](./§30-Unresolved%20Questions%20Index.md#30194-total-methods)
- [Error results in each target](./§30-Unresolved%20Questions%20Index.md#30195-error-results-in-each-target)
