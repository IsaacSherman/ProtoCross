# Working in this repository

ProtoCross compiles methods written against protobuf messages into equivalent C# and C++.

Read [ARCHITECTURE.md](ARCHITECTURE.md) for the lay of the land, [ProtoCross_Spec/](ProtoCross_Spec/README.md)
for the language, and [docs/language-1-workflow.md](docs/language-1-workflow.md) for the per-issue
process of the 1.0 language epic (#108), which builds on the editor-support epic's
[docs/epic-47-workflow.md](docs/epic-47-workflow.md).

## Commands

```bash
dotnet build ProtoCross.slnx
```

```bash
dotnet test ProtoCross.slnx
```

The full suite takes about two minutes because it builds and runs real generated projects. Filter
while iterating (`--filter "FullyQualifiedName~LexerTests"`), but the unfiltered run is the gate.
`protoc`, the .NET SDK, and a C++ toolchain must be on the machine.

Three checks are switched off by default, because none is what a person mid-iteration wants to wait
for: `PROTOCROSS_SWEEP=1` runs the whole-corpus completion sweep, `PROTOCROSS_SOAK=1` runs the long
editing soak, and `PROTOCROSS_BENCH=1` measures the latency budgets (which also needs `-c Release`
and `DOTNET_gcServer=0`, and refuses to run without either: the suite runs the server garbage
collector, and the language server ships with the workstation one).
`.github/workflows/ci.yml` turns the first two on for every pull request to `main`, so what a local
run skips is still checked before anything merges — and `report.ps1` fails the job when one of those
is skipped there, since a gate that quietly stays shut looks exactly like a green build. CI builds
and tests in Release, so a failure seen only there reproduces with `-c Release` on both commands.

`PROTOCROSS_BENCH` is deliberately not one of them. A wall-clock deadline on a shared runner flakes
until somebody loosens it past the point of describing anything, so CI checks counted work instead —
compilations per caret move, protoc invocations, answers in flight — which is deterministic and runs
unconditionally. The budgets, the corpus and the measured results are in
[docs/performance.md](docs/performance.md); **read it before optimising anything**, because four of
the five budgeted operations have one to two orders of magnitude of headroom and the measurement is
what says so.

## How to write code here

**DRY, religiously.** If a rule is expressed twice, the two copies will disagree eventually. When a
value is derived in more than one place, give it one home and have both callers ask. The
`SourceIdentity` type exists because a source path was being decomposed four separate ways.

**A function reads like a paragraph.** Names carry the meaning; the body says what happens, in order,
at one level of abstraction. A function that resolves a path, parses XML, and formats a message is
three functions. If you need a comment to explain *what* a line does, the line or its names are
wrong.

**Comment the why, never the what.** The house pattern is XML docs, not inline comments:

- `<summary>` — what this is, in a sentence or two.
- `<remarks>` — **why it is this way**: the alternative considered and rejected, the failure being
  guarded against, the constraint that forced the shape. This is where the real documentation lives
  in this codebase, and it is often several paragraphs. It is what a reader six months out needs.
- Inline `//` comments are for the non-obvious *decision* at a specific line — why a check is
  ordered before another, why a value is clamped. Not for narrating the code.

**Keep abstraction levels consistent inside a function.** Do not mix "settle the policy" with
"index into a char array" in one body.

**Errors are diagnostics, not exceptions.** The compiler must survive anything a user or an editor
buffer can hand it. Collect into a `DiagnosticBag` and keep going; the binder deliberately continues
past an unresolved name with `ErrorType`. Throwing is for programmer error (`ArgumentNullException`
on a null argument), never for bad input.

**Prefer additive change.** New types over reshaped ones, especially anywhere the epic touches.

## Current patterns

Observed across the codebase; match them rather than introducing a second style.

- **Records for data, `sealed` by default.** `sealed record` for reference data, `readonly record
  struct` for small values on hot paths (`SourceSpan`, `SourcePosition`). Positional syntax when the
  members cannot be inconsistent with each other; an explicit constructor when construction has to
  validate.
- **File-scoped namespaces**, `var` throughout, collection expressions (`[]`, `[.. items]`), target
  typed `new()`, pattern matching over branching.
- **Init-only properties instead of growing parameter lists** where new knobs are expected
  (`CompilationOptions` says so out loud).
- **`Try*` with `out`** for parse-or-report, returning `bool`.
- **Guard clauses and early return**; the happy path stays at the left margin.
- **Nullable is on and meaningful.** `null` means "there is none" (a buffer with no directory), and
  every consumer asks one question rather than `is null` in one place and `IsNullOrEmpty` in another.
- **Diagnostics** get a `PC####` code, a lowercase title, a full-sentence message, a span, and —
  wherever a reader could act on it — a `Help` string that says what to do. The first three come
  from a `DiagnosticDescriptor` in `DiagnosticCodes`, or `HostDiagnosticCodes` for the server's own
  range: a raise site names one and never spells a code, so two sites cannot disagree about what a
  code means. The message and the help are written at the site, because both say what went wrong
  *here*. Help text is part of the product; #61 turns it into quick fixes.
- **Public API stability is deliberate.** Existing constructor signatures and rendering are kept
  working when a type changes shape underneath them.

## Tests must have teeth

A test that cannot fail is worse than no test: it costs a run and buys confidence it has not earned.

- **Name the property, not the method.** `RangesAreHalfOpen`,
  `AnEmptyRangeIsDistinguishableFromAOneCharacterRange`,
  `TheSameTextDiagnosesIdenticallyWhicheverDoorItCameIn`. A full sentence in PascalCase saying what
  is true. Never `Method_Condition_Result`.
- **Assert the real answer, not the implementation's answer.** Compute the expectation from the
  input where you can — `text.IndexOf("extend")` beats a hardcoded `31`, because it still means
  something after the fixture is edited.
- **One property per test**, with a private static helper at the top of the class doing the setup
  (`Tokenize`, `Parse`, `Load` are the existing ones). `out var diagnostics` and
  `Assert.Empty(diagnostics)` is the standing idiom.
- **Prefer a sweep to a sample** when a property should hold everywhere: asserting it for *every*
  token in a fixture costs one loop and covers every path at once.
- **Say why in the assertion.** `Assert.True(cond, "the span must not end past the end of the text")`
  — a failure should diagnose itself.
- **Group long classes** with `// ------- section name` separators, as the existing suites do.
- Semantic behavior belongs in the **conformance corpus**
  ([tests/conformance/vectors](tests/conformance/vectors)), where it is compiled and executed in both
  backends. Unit tests cover the layer above that.
- **A new topic gets a new file, not the end of an old one.** Two branches that both append to the
  same long class or schema collide at its last lines, however unrelated they are. Each conformance
  vector owns a schema named after it (`floating_remainder.proto` for `floating_remainder.pcross`),
  and a test class that has grown sections is split into `partial` files, one per section
  (`NameMappingTests.CppTypes.cs`), so a new section is a new file.

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

## Never

- Move generated output or rendered diagnostics without saying so explicitly and diffing to prove
  the scope of the move.
- Let a backend see the AST, or branch on policy. Emission comes from IR behavior annotations.
- Add a CLI, editor, or file-system dependency to `ProtoCross.Core`.
- Bake one-file-per-compilation into a new API (#27 is coming).
- Add docs, changelogs, formatting passes, or coverage the task did not ask for. The spec is the
  exception, and only where the change actually reached the language; see above.

## Commits and pull requests

Subject line in the imperative, describing the change in the language of the problem, not the patch:
*"Give a span both of its ends"*, *"Hand the compiler text instead of a path"*, *"Survive malformed
input instead of taking the process down"*.

The body is prose wrapped near 80 columns — a few paragraphs on the defect, the shape of the fix, the
alternative rejected, and what did **not** move. Bullets only for a genuine list. Close with
`Closes #N. Part of #M.` and the `Co-Authored-By` trailer.

PR bodies follow the same voice with `## Why`, `## What`, `## Compatibility`, `## Tests` headings.
Compatibility is not optional: say what stayed byte-for-byte identical and how that was checked.

**Open every pull request as a draft, and mark it ready only once it merges cleanly into its base.**
The base is `main` for a sprint, and the sprint branch an issue branch was cut from for everything
else. Not a formality — it is what the CI triggers are built around. A draft is not tested, so a
branch that still has conflicts costs nothing while it is being rebased; marking it ready is the
event that asks for the full suite, both gated switches thrown. Reversing that order spends a run on
a branch that cannot merge, and then spends another on the version that can.

So the sequence is: open as draft, rebase onto the base until
`git merge-base --is-ancestor <base> HEAD` succeeds — the branch contains everything on its base, so
there is nothing left to conflict — run the suite locally, then mark ready. If a conflict appears
after that, because someone else merged first, put it back into draft, resolve, and mark it ready
again. The green check has to describe the code that is going to land, and a conflict resolved after
the check means it no longer does.

**Rebase onto the base; never merge the base into a branch.** A rebase replays each commit, so a
conflict is resolved inside an ordinary commit that the pull request's diff shows. A merge buries the
resolution in a merge commit nobody reads. That is how a sprint once shipped a test helper with
another test's body in it, and a shared schema missing two closing braces: each was a conflict
resolved inside a merge of the sprint branch into an issue branch, and each merged without anyone
seeing it. Then run the suite, because a conflict resolved correctly line by line can still combine
into something that does not build.

**The sprint branch accepts only tested, up-to-date pull requests.** A ruleset on `sprints/**`
requires a pull request, the CI checks, and a branch that is current with the sprint tip, and it
refuses force pushes. So pull requests land one at a time: merging one makes the others stale, and
each of those rebases and is tested again before it can follow. That applies to fixes made on the
sprint branch itself as well — they go through a pull request like anything else.

## Side sessions

A side session is a task handed to a separate session, in its own worktree, so the current issue
does not grow to include it. They are worth having, and they are also how parallel branches come to
edit the same lines. These rules keep the first without the second.

- **Only the session working the current issue starts one.** A side session that finds something
  files an issue for it and says so in its pull request. It never starts a session of its own, so
  there is always one place that knows everything in flight.
- **Check for overlap first.** List the files the side session will touch, and compare them with the
  current issue branch and with every open pull request into the sprint branch
  (`gh pr list --base <sprint branch> --json number,files`). If they overlap, file an issue instead:
  the work waits for its turn rather than racing another branch through the same lines.
- **It branches from the sprint branch's tip, never from the branch that started it.** A branch cut
  from another unmerged branch carries that branch's commits, so its pull request lands both — one
  merge once closed three pull requests at once. Work that genuinely depends on something unmerged is
  an issue, not a side session.
- **It closes one issue, opens a draft pull request, and stops.** It never marks its own pull request
  ready, never merges it, and rebases rather than merging when the base moves.
- **The prompt that starts it says all of this**, together with the sprint branch to cut from and the
  issue it closes. A side session reads CLAUDE.md, but it should not have to infer its own limits.

## Tooling notes

- Write and edit `.cs` files with the Write/Edit tools. Bash heredocs in this environment break on
  apostrophes in the body, which C# prose comments are full of.
- `perl -0777 -pi -e` is reliable for surgical multi-line replacements in existing files.
- `TreatWarningsAsErrors` is on, so an unused field or parameter fails the build. That is the check
  that a refactor left nothing dangling.
