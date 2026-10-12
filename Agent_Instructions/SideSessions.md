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
- **When stacking side issues, be careful of merges** If a side issue will overlap with another session,
  highlight this to the user and merge very deliberately. Whenever possible, branch in a serial pattern so
  merging the final branch incorporates all prior branches on that line, reducing merge conflicts.
  "Star" patterns are to be discouraged, but allowed where minimal merge conflicts will occur.