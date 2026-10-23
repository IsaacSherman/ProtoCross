# Taking a sprint to `main`

Read this when a sprint's last issue has landed and the sprint goes to `main` as one pull request.
The sprint rules themselves (the branches, the version and the README rule) are in
[language-1-workflow.md](language-1-workflow.md). This file is the order of the closing steps, and
what each one has to show. `N` is the sprint finishing, so sprint 6 reaches `main` at `0.7.0`.

## 1. Check the sprint is finished

- `gh pr list --base sprints/language-1-N` lists nothing open.
- Every issue in the sprint's row of the table on #108 has landed, or the owner has deferred it with a
  comment on the issue saying so. A deferred issue stays open, and this pull request does not close
  it.
- `git fetch`, and work from the tip of `origin/sprints/language-1-N`.

## 2. Cut `sprintN-to-main`

```bash
git fetch origin && git checkout -b sprintN-to-main origin/sprints/language-1-N
```

Work in the main checkout, as for an issue. The closing commits go on this branch, not on the sprint
branch: that takes commits only through a pull request, and one into it would number itself as the
sprint's next version.

## 3. Commit what the sprint still owes `main`

Each is a commit of its own, named in the pull request.

- **The version:** `npm version 0.<N+1>.0 --no-git-tag-version` in `editors/vscode`, committed as
  "Take sprint N to main at 0.<N+1>.0", with the range the sprint ran over in the message.
- **The coverage table:** every vector the sprint added has a row in
  [tests/conformance/README.md](../tests/conformance/README.md). The vectors added since `main`:

  ```bash
  git diff --name-status --diff-filter=A origin/main -- tests/conformance/vectors
  ```

- **The README and ARCHITECTURE.md:** each sprint pull request was to keep them current. Read the
  README's "Implemented" and "Not implemented" lists against what the sprint built *and decided*. A
  decision written into spec 30 can make a README line wrong without any code changing.

Nothing else goes here. A fix bigger than a stale line goes through the sprint branch, or into the next
sprint.

## 4. Prove what moved

Generated output and what the command line prints are published
([ARCHITECTURE.md](../Architecture/ARCHITECTURE.md#invariants-that-constrain-a-change)), so the pull
request says what moved against `main`, and shows it.

- Export both trees into the scratchpad with `git archive <commit> | tar -x -C <dir>`, one for
  `origin/main` and one for the head. Publish each tree's CLI from its own tree.
- Compile each tree's whole corpus with that tree's CLI, as production and test builds with
  `--scaffold`:
  - every vector;
  - each policy directory;
  - every `multi/` program and project;
  - `examples/simpleScript.pcross`.
- Diff the two outputs:
  - Name every file that moved, with the pull request that moved it.
  - State that everything else is byte-for-byte identical, with the file and compilation counts.
  - Compare what the CLI printed once each run's output directory is stripped, and its exit codes.
- Run the same command lines through both CLIs, and state what differs: none, `--help`, the usage
  errors, and each verb.

## 5. Run the suite

```bash
PROTOCROSS_SWEEP=1 dotnet test ProtoCross.slnx
```

Run it in the background; it takes about seven minutes. Record what passed, failed and was skipped,
for the pull request. Run it again after any later commit that touches code. After a commit that
touches only documents, rerun the tests that read them: `DiagnosticCodeTests`, and
`VsCodeExtensionTests` for `package.json`.

## 6. Open the draft pull request into `main`

This is the one pull request whose base is `main`.

```bash
gh pr create --draft --base main --head sprintN-to-main --title "..." --body-file pr-body.md
```

- **The body** has the headings [Committing.md](../Agent_Instructions/Committing.md) gives, and #188
  is the model:
  - **Why:** what the sprint set out to fix, and what was deferred or found along the way.
  - **What:** a table of every pull request into the sprint, with its title and issue, then what each
    feature does, the commits after the sprint tip, and the version.
  - **Compatibility:** step 4's results, any source break, the rendered diagnostics that are new or
    changed, and the public API.
  - **Tests:** step 5's counts, what to try in the extension and on the command line, and what
    deserves a human eye.
- **It closes every issue the sprint finished.** A pull request into a sprint branch closes nothing,
  and GitHub doesn't list its `Closes` lines as links, so read each body:

  ```bash
  gh pr view <n> --json body --jq .body | grep -oiE 'closes #[0-9]+'
  ```

  Include an issue finished across several pull requests, one found along the way, and an umbrella
  whose sub-issues are now all done (sprint 5 closed #79). End the body with one `Closes #N.` for
  each, then `Part of` the sprint's epic, which was #108 through sprint 6. Leave deferred issues and
  the epic open. Sprint 7 belongs to no epic, so it ends with its `Closes` lines.
- **Turn Auto-fix on as soon as it opens:** `get_status`, then `bind_pr` if the pull request isn't
  reported, then `set_monitor` with `auto_fix` and `address_comments`.

## 7. Fix the owner's findings

- Fix each on `sprintN-to-main`, and say in the commit which finding it answers.
- Update the pull request body: the commits after the sprint tip, and any count that moved.
- Run the suite again after a code change. Repeat step 4 after anything that could move generated
  output.

## 8. Mark it ready

Once the findings are fixed and the suite passes on the head:

- Run `gh pr ready`. Ready is what starts CI's full suites, and CI is paid, so nothing is readied
  before this point.
- Don't push and then ready straight away. A push followed at once by `gh pr ready` cancels the only
  real run. If that happens, rerun it.
- Check with `get_status` that Auto-fix is still on.

## 9. Wait until it merges

- The owner merges. Never merge it, or turn on auto-merge.
- Auto-fix wakes the session on a CI failure, a conflict or a review comment:
  - A failure is fixed on the branch.
  - A conflict is resolved by merging `main` in, and the merge commit says how each file was resolved
    (Committing.md).
  - A comment is answered or fixed.

## 10. After it merges

- Check that every issue the body closes is closed: `gh issue view <n> --json state`.
- Publish the extension when the owner says to, by releasing the merge commit under its version:

  ```bash
  gh release create v0.<N+1>.0 --target "$(gh pr view <n> --json mergeCommit --jq .mergeCommit.oid)" --generate-notes
  ```

  The release starts [publish.yml](../.github/workflows/publish.yml). It checks the tag against the
  version, waits for CI on `main`, packs the tool, and attaches the VSIX and the tool's `.nupkg` to
  the release. Then it waits for the owner to approve the `marketplace` deployment, once, before
  publishing the extension to the stores and the tool to nuget.org. To finish a publish that failed
  part of the way, run the workflow again with the same tag.
- Check the published tool from outside the repository, once nuget.org lists the version, which takes
  a few minutes. It should print the version just released:

  ```bash
  dnx ProtoCross@0.<N+1>.0 --version
  ```
- Open the next sprint only when the owner says to start it. That is its own pull request, as #189
  was:
  - cut `sprints/language-1-<N+1>` from `main`;
  - point the sprint line in [language-1-workflow.md](language-1-workflow.md) at it, recording what
    sprint N carried and which pull request took it to `main`;
  - bump to the number after `main`'s version: `0.<N+1>.1`, unless a release from `main` has taken
    it since. Sprint 7 opens at `0.7.2`, after `0.7.1`
    ([language-1-workflow.md](language-1-workflow.md#the-version-follows-the-sprint)).
