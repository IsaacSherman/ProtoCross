## Keep the spec current

[ProtoCross_Spec/](ProtoCross_Spec/README.md) is the language, not a description of it. Anything that
changes what an author can write, what it means, or what the compiler tells them about it changes
the spec too, and that edit belongs in the **same commit as the code**. A spec that lags is still
consulted, and being trusted is exactly what makes a stale one expensive.

What counts:

- **Syntax, semantics, or the type system.** A new construct, a rule that moved, a case that used to
  be an error and is not.
- **A diagnostic.** §26 governs codes and rendering, and both are published output.
- **What the IR preserves.** §22.2 states the contract, and it is the worked example for this whole
  section. It spent four issues behind the code — where each name was *used* (#40), what was in
  scope at a position (#49), how each operation behaves (#105), how a literal's value is represented
  (#28) — and each of those shipped without saying so, so the contract had to be read from the
  binder until #107 caught it up. It also now states invariants a consumer may rely on, and those
  are swept over the corpus in `IrContractTests`, so an addition that breaks one fails a test
  instead of passing in silence. What the IR *preserves* is still prose, and still the part that
  goes stale.
- **An open question, once it is settled.** §30 is the authoritative list: strike the entry through,
  say what was decided, and name the section that decides it. §31 gets a dated row with the
  rationale. A decision argued out in a PR body and recorded nowhere else is one the next reader
  re-litigates from scratch.

What does not count is an internal with no observable surface. `BlockStatement.IsClosed` is a fact
about the parser rather than about the language, and the spec is not where it goes.

Say what moved under the PR's `## What`. If a change *should* move the spec and deliberately does
not — the wording is contested, or another branch owns that section — say that as well, so the
omission reads as a decision rather than an oversight.
