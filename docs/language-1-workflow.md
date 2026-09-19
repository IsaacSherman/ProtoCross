# Implementing epic #108, one sprint at a time

[Epic #108](https://github.com/IsaacSherman/ProtoCross/issues/108) finishes the language and the IR
for 1.0. It follows [the #47 process](epic-47-workflow.md) almost step for step. This file records
only what is different, so the two cannot drift apart: where it says nothing, do what that one says.

Read it together with [ARCHITECTURE.md](../ARCHITECTURE.md) and [CLAUDE.md](../CLAUDE.md).

## What is different

### Sprints, not epic branches

Work lands on a **sprint branch** covering a handful of issues. The sprints and their issues are in
the table on #108. A sprint branch is cut from `main`, each issue branches off it and is merged back
into it by pull request, and the sprint then goes to `main` as one pull request, the way
`epics/language-server-N` did.

The current sprint branch is **`sprints/language-1-1`**. This line is the only record of that, so
moving to the next sprint means editing it here.

```bash
git checkout sprints/language-1-1 && git pull && git checkout -b issue-10-drop-virtual
```

Issue pull requests use the sprint branch as their base, never `main`:

```bash
gh pr create --draft --base sprints/language-1-1 --title "..." --body-file pr-body.md
```

They land one at a time. A ruleset on `sprints/**` requires a pull request, the CI checks, and a
branch that is up to date with the sprint tip, so everything reaches the sprint branch the same way,
including fixes to the sprint branch itself. When the tip moves, rebase onto it and let CI run again;
never merge the sprint branch in. The rules for side sessions, and the reasons for all of this, are
in [CLAUDE.md](../CLAUDE.md).

### The comments are part of the issue

The owner settles design questions **in comments on the issue being decided**, and many of those
comments are newer than the issue body. Step 1 is therefore:

```bash
gh issue view 13 --comments
```

Read every comment. A comment overrides the part of the body it answers. If a question the issue
leaves open has no comment settling it, ask the owner before building it. Do not choose silently: the
point of this epic is that each decision is made once, by the person who owns it.

Some older issues predate the rename and use the ProtoLang names (`.protolang`, `protolangc`,
`PL####`). Read them as `.pcross`, `protocross`, `PC####`.

### Every decision reaches the spec

The #47 loop's step 4 applies with one addition. In this epic almost every issue *is* a language
decision, so "if the change reached the language" is nearly always yes. For each decision the issue
or its comments settle:

- write the rule into the section that owns it, as normative text rather than a quotation of the
  comment;
- strike the spec 30 entry and name that section;
- add a dated spec 31 row with the rationale and the alternatives that lost;
- keep all of this in the same commit as the code.

A decision the owner made in a comment that the spec does not show has not been recorded. The next
session will reopen it.

### Semantics go in the conformance corpus

Unit tests cover the layer above. What a construct *means* is proven in
[tests/conformance/vectors](../tests/conformance/vectors), compiled and executed in both backends.
A backend that cannot emit a construct yet rejects it with a diagnostic (spec 23). It never emits
something that behaves differently.

### Check it in the editor

Each new construct should behave in the VS Code extension: diagnostics, hover, go-to-definition,
completion inside it, and semantic tokens. That is how the owner tests by hand. Before handing an
issue over, name what to try in the extension alongside the short list of what deserves a human eye.

## What is not different

The per-issue loop, the review-until-clean cycle, the draft-then-ready rule in CLAUDE.md, the
byte-for-byte proof against a base worktree, and ARCHITECTURE.md updates once the shape settles. All
of it is as written in [epic-47-workflow.md](epic-47-workflow.md), with `sprints/language-1-1` in
place of `epics/language-server-4` and `Part of #108.` in place of `Part of #47.`
