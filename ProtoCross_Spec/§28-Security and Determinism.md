## 28. Security and Determinism

Normative Requirements:

- ProtoCross behavior must be deterministic for a fixed input message and method arguments.
- No source-level access is provided to time, randomness, environment variables, filesystem, network, process state, or threads.
- Generated code must not depend on locale unless explicitly specified.
- Nesting is bounded, because every stage after parsing walks the tree by recursion, and a
  stack exhausted in the compiler cannot be recovered from. The bound is 128 levels, and it
  applies to the tree the compiler builds, not only to how the parser reached it:
  - No expression may be more than 128 levels tall. A name or a literal is one level, and each
    binary operator, member access, call, conversion (`as`), prefix operator and `has` adds one to
    the tallest expression it holds. So `a.b.c` and `1 + 2 + 3` are each three levels tall, however
    they are written. Parentheses add no level of their own.
  - Parenthesized and argument expressions, blocks, message fixtures, and each `else if` of a chain
    nest to the same bound.
  - A construct past the bound is `PC0081`. It is reported once per file, at the token where the
    construct went too deep. The compiler steps over the rest of that construct and goes on to
    parse what follows, so that one diagnostic is the only one it causes.
- Runtime loops and recursive method calls are not currently resource-limited.

Open Questions:

- Should resource limits be specified for generated methods?
- Should recursion be allowed?
- Should the compiler reject potentially unbounded recursion?
