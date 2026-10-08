# Working in this repository

ProtoCross compiles methods written against protobuf messages into equivalent C# and C++.

Start with symbol searches and focused excerpts. Expand reads as needed to understand callers,
state, and invariants. Summarize successful checks; inspect failure details. Avoid repeating
unchanged file contents and diffs.

## Find the relevant guidance

Read these as needed, rather than on every prompt:

- **Architecture:** Start with the [feature lookup](Architecture/Feature-Index.md) for source files,
  entry symbols, and focused topic guides. Do not read [ARCHITECTURE.md](Architecture/ARCHITECTURE.md)
  in its entirety. Keep the lookup and topic guides synchronized when updating the architecture.
- **Issue work:** Read the [language 1.0 workflow](docs/language-1-workflow.md) for the per-issue
  process. The current language epic (#108) builds on the [editor-support workflow](docs/epic-47-workflow.md).
- **Testing:** Follow [Testing.md](Agent_Instructions/Testing.md).
- **Spec maintenance:** Follow [SpecMaintenance.md](Agent_Instructions/SpecMaintenance.md).
- **Commits and pull requests:** Follow [Committing.md](Agent_Instructions/Committing.md).
- **Side sessions:** See [SideSessions.md](Agent_Instructions/SideSessions.md).

## How to write code here

**DRY, religiously.** Duplicated rules eventually disagree. Give derived values one home and have
callers ask for them. `SourceIdentity` exists because a source path was being decomposed four ways.

**A function reads like a paragraph.** Names carry the meaning; the body says what happens, in order,
at one level of abstraction. Resolving a path, parsing XML, and formatting a message are three
functions. Do not mix policy decisions with indexing into a char array. If a comment must explain
*what* a line does, improve the line or its names.

**Comment the why, never the what.** The house pattern is XML docs, not inline comments:

- `<summary>` — what this is, in one or two sentences.
- `<remarks>` — **why it is this way**: rejected alternatives, guarded failures, and constraints
  that forced the shape. Several paragraphs are appropriate when a future reader needs them.
- Inline `//` comments explain a non-obvious *decision* at a specific line: why checks are ordered
  or a value is clamped. They do not narrate the code.

**Errors are diagnostics, not exceptions.** The compiler must survive anything a user or an editor
buffer can hand it. Collect into a `DiagnosticBag` and keep going; the binder deliberately continues
past an unresolved name with `ErrorType`. Throwing is for programmer error (`ArgumentNullException`
on a null argument), never for bad input.

**Prefer additive change.** New types over reshaped ones, especially anywhere the epic touches.

## Current patterns

Match the established style:

- **Records for data, `sealed` by default.** `sealed record` for reference data, `readonly record
  struct` for small values on hot paths (`SourceSpan`, `SourcePosition`). Positional syntax when
  members cannot be inconsistent; an explicit constructor when validation is needed.
- **File-scoped namespaces**, `var` throughout, collection expressions (`[]`, `[.. items]`), target
  typed `new()`, pattern matching over branching.
- **Init-only properties instead of growing parameter lists** where new options are expected
  (`CompilationOptions`).
- **`Try*` with `out`** for parse-or-report, returning `bool`.
- **Guard clauses and early return**; the happy path stays at the left margin.
- **Nullable is on and meaningful.** `null` means "there is none" (a buffer with no directory).
  Consumers check consistently rather than mixing `is null` and `IsNullOrEmpty`.
- **Diagnostics** get a `PC####` code, a lowercase title, a full-sentence message, a span, and —
  wherever a reader could act on it — a `Help` string that says what to do. The first three come
  from a `DiagnosticDescriptor` in `DiagnosticCodes`, or `HostDiagnosticCodes` for the server's
  range. Raise sites name a descriptor and never spell a code. Write the message and help at the
  site to explain what went wrong *here*. Help text is part of the product and supports quick fixes.
- **Public API stability is deliberate.** Existing constructor signatures and rendering are kept
  working when a type changes shape underneath them.

## Never

- Move generated output or rendered diagnostics without saying so explicitly and diffing to prove
  the scope of the move.
- Let a backend see the AST, or branch on policy. Emission comes from IR behavior annotations.
- Add a CLI, editor, or file-system dependency to `ProtoCross.Core`.
- Bake one-file-per-compilation into a new API.
- Add docs, changelogs, formatting passes, or coverage the task did not ask for. The spec is the
  exception, and only where the change actually reached the language; see above.

## Tooling notes

- Use file-editing tools for `.cs` changes.
- `TreatWarningsAsErrors` is on: unused fields and parameters fail the build and expose incomplete refactors.
