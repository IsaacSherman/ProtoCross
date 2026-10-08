## Commits and pull requests

Subject line in the imperative, describing the change in the language of the problem, not the patch:
*"Give a span both of its ends"*, *"Hand the compiler text instead of a path"*, *"Survive malformed
input instead of taking the process down"*.

The body is prose wrapped near 80 columns — a few paragraphs on the defect, the shape of the fix, the
alternative rejected, and what did **not** move. Bullets only for a genuine list. Close with
`Closes #N. Part of #M.` and the `Co-Authored-By` trailer.

PR bodies follow the same voice with `## Why`, `## What`, `## Compatibility`, `## Tests` headings.
Compatibility is not optional: say what stayed byte-for-byte identical and how that was checked.

**Open every pull request as a draft, and keep it a draft until the owner says to merge it.** The
base is `main` for a sprint, and the sprint branch an issue branch was cut from for everything else.
Not a formality — it is what the CI triggers are built around, and CI is paid for by the minute. A
draft is not tested, so a branch still being rebased, reviewed and fixed costs nothing; marking it
ready is the event that asks for the full suite, both gated switches thrown. Until the owner asks
for that, the suite is run locally. Marking a branch ready early spends a run on code that is still
going to change, and then spends another on the version that lands.

So the sequence is: open as draft, rebase onto the base until
`git merge-base --is-ancestor <base> HEAD` succeeds — the branch contains everything on its base, so
there is nothing left to conflict — and run the suite locally. Then stop, and say it is ready for the
owner. Mark it ready only when the owner says to merge it. If a conflict appears after that, because
someone else merged first, put it back into draft, resolve, and mark it ready again. The green check
has to describe the code that is going to land, and a conflict resolved after the check means it no
longer does.

**Markdown-only changes skip the build and test suites.** Documentation and workflow notes are checked
locally, together with any test that reads them, and are never the reason to mark a pull request
ready or to rerun a check. When the owner asks to merge, mark it ready as usual: the required checks
then pass after checking the changed paths. A documentation update to a pull request that includes
code reuses successful validation of the same non-Markdown tree, including the merged base, per
suite and platform. Changed inputs or a missing successful record run the suites; an explicit
workflow rerun runs them again. Documentation still has to pass any test that reads it locally.

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
