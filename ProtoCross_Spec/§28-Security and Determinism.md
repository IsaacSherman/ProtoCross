## 28. Security and Determinism

Normative Requirements:

- ProtoCross behavior must be deterministic for a fixed input message and method arguments.
- No source-level access is provided to time, randomness, environment variables, filesystem, network, process state, or threads.
- Generated code must not depend on locale unless explicitly specified.
- Nesting is bounded, because every stage after parsing walks the tree by recursion, and a
  stack exhausted in the compiler cannot be recovered from. The bound is 128 levels, and it
  applies to the tree the compiler builds, not only to how the parser reached it:
  - No expression may be more than 128 levels tall. A name or a literal is one level, and each
    binary operator, member access, lookup (`m[k]`), call, conversion (`as`), prefix operator and
    `has` adds one to the tallest expression it holds, as do a message literal, a list and a map's
    entry. So `a.b.c` and `1 + 2 + 3` are each three levels tall, however they are written.
    Parentheses add no level of their own.
  - Parenthesized and argument expressions, blocks, message fixtures, a map's entries, and each
    `else if` of a chain nest to the same bound. An entry nests a level because its value may be a
    list of entries, `[{ value: [{ value: ... }] }]`, which nests through no expression at all.
  - A construct past the bound is `PC0081`. It is reported once per file, at the token where the
    construct went too deep. The compiler steps over the rest of that construct, diagnoses nothing
    inside it, and goes on to parse what follows. A check that needs what was skipped cannot see
    it: a method whose every `return` was in skipped branches is also told that not every path
    returns.
- **Recursion is allowed, and never refused**, even where it may never end, as no loop is
  ([15.2](./§15-Control%20Flow.md#152-loops)). A method that recurses without end runs out of stack, which ends the
  program the way its host ends one: it is not the terminal failure `fail` is, and does not exit with
  code 70 ([10.2.1](./§10-Numeric%20Semantics.md#1021-the-on_zero-clause)). Forbidding recursion after 1.0 would break programs, and recursion is
  the natural way to walk a tree of messages.
- Runtime loops and recursive method calls are not resource-limited.

Open Question:

- [Runtime limits](./§30-Unresolved%20Questions%20Index.md#30282-runtime-limits), post-1.0
