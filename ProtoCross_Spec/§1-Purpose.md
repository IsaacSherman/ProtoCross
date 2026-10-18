## 1. Purpose

ProtoCross is a deliberately small language for defining portable behavior over Protocol Buffer message types.

ProtoCross source files define methods and related behavior once, then compile that behavior into target languages. The current implementation emits C# and C++.

The language is intended to describe core semantic behavior, not target-language idioms.

### 1.1 Normative Language Boundary

This specification distinguishes between:

- Normative language semantics: behavior every conforming compiler and backend must preserve.
- Implementation details: compiler, IR, runtime, and backend strategies that may vary.
- Open design questions: unresolved issues requiring explicit decisions before stabilization, each
  asked once, in [30](./§30-Unresolved%20Questions%20Index.md#30-unresolved-questions-index).

Unless a section is marked "Implementation Note" or "Open Question", its contents are intended to become normative.
