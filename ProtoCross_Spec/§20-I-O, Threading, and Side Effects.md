## 20. I/O, Threading, and Side Effects

Normative Requirements:

- ProtoCross has no I/O primitives.
- ProtoCross has no threading or concurrency primitives.
- ProtoCross has no clock, randomness, environment, filesystem, network, console, or process APIs.
- Backends must not silently introduce observable I/O or concurrency behavior into generated method bodies.
- Generated terminal-failure paths are the exception: `on_zero fail` writes a diagnostic to standard
  error and terminates the process as specified in 10.2.1, and a conversion to an enum that fails on
  a number the enum does not name does the same ([12.1](./§12-Enums.md#121-an-enums-number)). A project that forbids ending the program
  has none ([10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy)), so nothing it generates writes to standard error or ends the process.
- ProtoCross methods may call only ProtoCross methods resolved by the compiler.

Open Questions:

- [Marking pure functions](./§30-Unresolved%20Questions%20Index.md#30202-marking-pure-functions)
