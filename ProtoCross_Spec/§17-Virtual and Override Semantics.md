## 17. Virtual and Override Semantics

**Decided: ProtoCross has no virtual methods and no overriding.** A method means one thing, stated
once, in every target.

Normative Requirements:

- There is no `virtual` modifier and no mechanism for replacing a method's behavior from outside
  it: not by subclassing, not by a registered hook, and not by a generated delegate.
- `virtual` is not a keyword ([6.4](./§6-Lexical%20Structure.md#64-keywords)). It is an ordinary
  identifier, and a backend escapes it wherever a target reserves it, as it does any other name.
- ProtoCross source cannot declare subclasses.
- Subclassing generated protobuf messages is forbidden as a portability strategy.

Rationale:

The project exists so that behavior has one source of truth. An overridable method is a second one
by construction: what `rate()` computes depends on code that is not in the ProtoCross source and may
differ per host and per target. That is the divergence the language is here to prevent.

It could not have been classical dispatch in any case. protoc generates sealed or
inheritance-hostile classes in several targets, so every workable design needs something else. The
most concrete one sketched was a static, settable delegate on the generated class. That is global
mutable state, it is not thread-safe, and spec 20 bans concurrency primitives but cannot stop a host
from being concurrent.

The keyword had been parsed and carried through the IR, and then rejected by both backends (the
former `PC1001` and `PC1101`). So it type-checked and failed only at the last step, and no program
ever generated code with it. Removing it breaks nothing that worked. Keeping it reserved would keep a
word for a feature the language has decided against.

The owner's note from the draft that preceded this decision, kept because it is the argument:

`That said, I'm really not convinced they're a good idea at all. Again, we're writing this because *we don't want to have more than 1 source of truth for behavior*.  Virtual functions are antithetical to that.  But they might be a necessary workaround for some people in some scenarios- I just don't know what they might be. ~IS`

A host that genuinely needs different behavior should call a different method, or do the work in its
own language around the generated code.
