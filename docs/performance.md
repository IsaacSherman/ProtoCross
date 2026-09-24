# Performance budgets and how they are measured

Several issues in [#47](https://github.com/IsaacSherman/ProtoCross/issues/47) said "fast enough to
feel instant" or "fast enough to run on caret movement". That is not a specification and it cannot
fail a build. This file is the specification.

The numbers do not need to be exactly right. They need to **exist**, so that somebody choosing
between two designs has something to check against, and so that a regression is a failing test
rather than a feeling that things got worse.

## What the budgets are

| Operation | Budget | Why |
|---|---|---|
| diagnostics after edit | 400 ms | measured after the debounce, so this is the compile itself; slower and the squiggles stop feeling attached to the typing that caused them |
| completion | 50 ms | above this the list arrives after the author has typed past the word it was offering |
| hover | 50 ms | the same threshold, because a hover is asked on dwell and answers into a gesture the reader has already committed to |
| occurrence highlighting | 20 ms | fires on caret movement, so it is paid on every arrow key; anything slower makes cursor motion itself feel heavy, which is the one cost a reader blames on the editor |
| go-to-definition | 100 ms | a discrete action with a visible result, so a little latency reads as the editor working rather than as lag |

**A descriptor cold load is measured and reported, never budgeted.** It is `protoc` starting, reading
a schema closure and writing a descriptor set, and this project does not choose how long that takes.
The budget on a cold load is that it happens once.

Each budget is the **95th percentile over the stress corpus, warm**. A median hides the keystroke
that stutters, and the stutter is the whole experience being budgeted for — nobody notices the
nineteen fast hovers. Warm means the descriptors are loaded and the buffer has been compiled, which
is the state an editor is in for every keystroke after the first.

These figures live in exactly one place a machine reads:
[`PerformanceBudgets.cs`](../src/ProtoCross.LanguageServer/Hosting/PerformanceBudgets.cs). The table
above is the copy for people, and
`PerformanceBudgetTests.TheDocumentedBudgetsAreTheEnforcedOnes` fails if the two disagree — because a
rule written twice disagrees eventually, and the copy that would quietly stop being true is the one
people read.

They sit in the server rather than beside the benchmark, which is where #57 first put them. Two
readers want them now: the benchmark, which checks a measurement against them, and the status command
from #58, which reports what a running server's own requests have cost against them. A second copy in
the server would be a third place for this table to disagree with itself, and the copy a user reads
off a status report is the one nobody would think to check.

## What "a normal file" means

Stated rather than assumed, which is what #57 asks for.

| | File | Lines |
|---|---|---|
| normal | [`examples/simpleScript.pcross`](../examples/simpleScript.pcross) | 274 |
| stress | [`tests/perf/corpus/wide.pcross`](../tests/perf/corpus/wide.pcross) | 2,814 |

**The normal case is a real file on purpose.** `simpleScript.pcross` is maintained for its own
reasons and goes on being edited by people who are not thinking about measurement, which is exactly
what keeps it representative. A fixture written for a benchmark drifts towards whatever the benchmark
finds convenient.

**The stress case is generated and committed, which looks like wanting it both ways and is not.** A
file generated at measurement time can be raised later but is a different file every time the
generator is touched, so two runs a month apart would not be comparable — and comparability is the
only reason to write numbers down. Committing the output settles it. The generator is
[`StressCorpus`](../tests/ProtoCross.Tests/Performance/StressCorpus.cs), and
`PerformanceCorpusTests.TheCommittedStressFileIsWhatTheGeneratorProduces` runs on every build, so the
two cannot drift.

It is shaped rather than merely long, because the operations above are bounded by different things:

- **170 methods on one receiver** — the breadth of scope a completion has to gather.
- **One method called from every one of them** (516 references) — the length of the list occurrence
  highlighting walks.
- **One body of 60 chained locals** — a scope deep in declarations rather than wide in members.

To raise it, change `StressCorpus.Steps` and rewrite the committed file from the generator.

## How to measure

```bash
PROTOCROSS_BENCH=1 DOTNET_gcServer=0 dotnet test ProtoCross.slnx -c Release --filter "FullyQualifiedName~Performance"
```

**`-c Release` is not optional, and the benchmark refuses to run without it.** `dotnet test` builds
Debug unless told otherwise, and a Debug reading is not a Release reading with a constant factor
missing: measured here it was 1.0x on some rows and 1.8x on others, and the rows it moved most were
the ones on the larger corpus — which is to say, the rows the budgets are defined against. The first
figures published for this file came out of a Debug build, and the row nearest its budget was the one
the optimizer moved most. Every report now states the configuration it was taken on.

**`DOTNET_gcServer=0` is not optional either, and for the same reason.** The test project runs the
server garbage collector, which the resilience sweeps need to use every core, and the language server
ships with the workstation one. A collector is chosen once, when the process starts, so no test can
switch it for itself: the variable starts the whole run on the workstation collector, the benchmark
refuses to run on the other, and every report states which one it was taken under.

In PowerShell the variables are set separately — `$env:PROTOCROSS_BENCH = 1` and
`$env:DOTNET_gcServer = 0` — and stay set for the rest of the session, so clear both afterwards with
`= $null`. Left set, the second makes every later run of the suite in that session slower.

Every run writes `artifacts/perf/report.md`: every operation, both corpora, median, p95, min, max,
and whether it was within budget. The report is written whether the run passes or fails, because the
question after "too slow" is always "by how much, and was it always?".

The measurement drives the providers directly rather than going over the wire. Framing, the reader
loop and the worker handoff are real costs, but they are not what a design decision moves, and
reporting them as the cost of a hover would be misleading.

[#58](https://github.com/IsaacSherman/ProtoCross/issues/58) measures the same operations on a running
server, from inside the dispatch table, and reports them in the status command against these same
budgets — which is why the budgets and the percentile rule live in `ProtoCross.LanguageServer` rather
than beside the benchmark. That measurement starts one step further out than this one: it includes
deserializing the request's parameters, the lifecycle check and the wait for a slot on the answer
gate. It still stops short of the framing and of the time a request spends queued behind a
`didChange`, so neither file measures those. See *What this does not cover*.

## How regressions are caught

#57 left this open deliberately and asked for a decision rather than a default. The decision is
**both, split by what each kind of check is good at**:

- **CI checks counted work, on every pull request.** How many compilations a caret move costs, how
  many times `protoc` is invoked, how deep the queue gets, how many answers are in flight. These are
  deterministic: they cannot flake, and they fail the moment somebody adds a compile to a path that
  did not have one. That is
  [`PerformanceCostTests`](../tests/ProtoCross.Tests/Performance/PerformanceCostTests.cs).
- **A person checks milliseconds, on a machine they chose.** Wall-clock deadlines on a shared runner
  are a coin toss with a build attached: they flake until somebody loosens them, and a threshold
  loose enough never to flake no longer describes the budget. This repository has already spent two
  commits on deadlines that were fine locally and were not fine on a runner.

The honest weakness of that split is the one #57 names: a benchmark nobody runs is worth little. What
makes this one worth running is that it **asserts** as well as reports — a run that is over budget
fails and says by how much — so it is a check rather than a printout.

## What the measurements found

Measured 2026-09-13 on a 16-processor desktop, .NET 10.0.11, **Release**. Reproduce with the command
above; the numbers below are a snapshot and the report is the authority.

| Operation | Normal p95 | Stress p95 | Budget |
|---|---:|---:|---:|
| hover | 0.5–1.0 ms | 0.8–2.3 ms | 50 ms |
| occurrence highlighting | 0.7–1.0 ms | 1.4–1.5 ms | 20 ms |
| go-to-definition | 0.7–1.2 ms | 0.9–1.1 ms | 100 ms |
| diagnostics after edit | 1.4–2.0 ms | 18–23 ms | 400 ms |
| completion | 2.7–3.1 ms | 29–34 ms | 50 ms |

Ranges across four runs on one machine, minutes apart, where they disagreed by more than rounding.

**That spread is itself a result, and not a flattering one.** These are nearest-rank percentiles over
twenty samples with a fixed warm-up, which is the naive estimator: it has no outlier handling, no
confidence interval, and no way to tell a genuinely bimodal operation from a noisy one. Completion's
p95 moved by 5 ms between runs that changed nothing. The figures are good enough to answer the
question this issue asks — is anything near its budget — and they are **not** good enough to detect a
20% regression, which is one reason regressions are caught as counted work instead.

[#97](https://github.com/IsaacSherman/ProtoCross/issues/97) is where that would be improved, by
handing these five rows to BenchmarkDotNet for its statistics and its allocation counts. It is low
priority and blocks nothing. If it is closed without being done, this paragraph is the part that has
to survive: the limitation is real whether or not anybody is planning to fix it.

**Four of the five have one to two orders of magnitude of headroom, and that is the finding.** #57
exists partly to inform design — whether scope data is cached, whether occurrence highlighting can
consult the reference index directly, whether completion can resolve documentation eagerly. The
answers are: **no caching is warranted**, **yes it can**, and the reference index at 1.2 ms against a
20 ms budget on a file ten times normal size is not something to optimise. Anything built on top of
those paths to make them faster would be paying complexity for latency nobody can perceive.

**Completion on the stress corpus is still the closest row to its budget**, at 29–34 ms against 50 ms
— by a wide margin the closest, since the next nearest is diagnostics at 23 ms against 400 ms. It is
the row a new feature should be measured against before it is added, and `ScopeSearch` — the linear
scan it leans on hardest — is the first place to look if it ever goes over.

An earlier version of this file said it was at 48 ms and "will not stay within budget by accident".
That was a Debug measurement, and the correction is left visible rather than quietly applied: the
conclusion it supported — that completion was nearly out of headroom — was wrong, and it was wrong in
the direction that would have prompted somebody to optimise a path that did not need it. Which is
exactly the kind of decision #57 exists to prevent being made on a feeling, made on a bad number
instead.

The budget was **not** tightened to match the other four, and that is deliberate: a budget is a
threshold a person can feel, not a ratchet against the last measurement. Tightening hover to 2 ms
because it measures 0.9 ms would fail on a slower machine without anything having got worse, and
would say nothing about whether a hover felt slow. Regressions are the cost assertions' job.

### Descriptor loads, and the bounds that come off them

**A cold load of the examples' schema closure is 21–33 ms**, essentially all of it `protoc` starting.
Warm, the same load is 0.13 ms — a factor of about 200, which is the entire argument for the cache
existing.

| | Wall clock | Thread pool |
|---|---:|---|
| 1 cold load | 25 ms | 5 → 7 |
| 4 cold loads (the concurrency limit) | 44 ms | 7 → 15 |
| 8 cold loads (twice the limit) | 76 ms | 15 → 21 |

**The bounds #54 guessed at are now measured, and all three stand:**

- **The `protoc` timeout of 30 s is a backstop, confirmed.** A cold load is three orders of magnitude
  under it. It exists for a hung process, not for a slow one, and nothing normal approaches it.
- **The concurrency limit of 4 stays 4.** #54 changed `DescriptorCache` to hold each load as a `Task`
  rather than a `Lazy`, so a superseded compile can abandon its wait — at the cost of a second
  blocked thread per load. The measurement shows exactly that: roughly two pool threads per
  concurrent load, 7 → 15 at four and 15 → 21 at eight. Wall clock still scales sub-linearly, so the
  pool is absorbing it **here**. The honest caveat is that this machine has 16 processors, and the
  default minimum worker count is one per processor: this is the best case, not the typical one. A
  four-core machine running four concurrent cold loads is the case that would bite, and raising the
  limit doubles the thread demand with it. **So the limit is not raised, and the reason is a
  measurement rather than caution.**
- **The cache capacity of 16 stays 16, and its footprint is now a number**: about 28 KiB per retained
  bundle for the examples' closure, so about 0.4 MiB for a full cache of that closure. That is small
  enough that capacity is not what bounds memory here — but the examples' closure is two messages,
  and a real workspace's is not, so the figure to carry forward is the per-bundle one and not the
  total. #48 asked for this and could not answer it without measurement.

**What would change the answers**: a schema closure an order of magnitude larger than the examples',
or a machine with four cores or fewer. Both are worth re-measuring on before the concurrency limit or
the cache capacity moves.

### What is still a guess

Every bound above was measured. These were not, and saying so is the point — a reader should be able
to tell "measured and confirmed" from "nobody has looked", and the code comments at each site now
distinguish the two rather than all pointing here.

| Bound | Where | Why it was not measured | On a keystroke path? |
|---|---|---|---|
| the include-root walk budget, in entries examined | `SchemaCatalog` | it bounds a directory walk against a vendored tree or a network mount, and a measurement taken against this repository's own directories would say nothing about either | only for import completion, which is the one request that walks |
| the answer concurrency limit of 4 | `DeferredAnswers` | every measurement here asked one provider at a time, so nothing taken above ever reached the limit | **yes** |
| the scheduler's yield interval | `CompileScheduler` | it affects fairness under sustained editing, which the soak exercises and the budget table does not | yes, under sustained typing |
| whether configuration resolution needs revisiting | `WorkspaceConfiguration` | it is inside every warm figure above and was never separated out from them | **yes** |

**Two of those are on the per-keystroke path, and an earlier draft of this file said none of them
were.** The correction is worth keeping rather than quietly fixing, because both errors were the same
mistake — describing a bound by the first caller that came to mind:

- **`DeferredAnswers` bounds compilations, not only directory walks.** Its own remark reaches for the
  import-completion case, and that is one of seven providers; the other six compile inside the gate.
  So the limit of four is what stops ten open documents becoming ten simultaneous compiles, which is
  a much larger claim than the one the comment makes. Nothing here measured it, because every reading
  above asked one provider at a time.
- **Configuration resolution runs on every `DocumentSemantics.For` call, including cache hits.** It is
  resolved before the held entry is checked, deliberately — it is half of what decides whether the
  entry still answers. So it is paid on every hover, every highlight, every caret move, and it is
  already inside all ten warm figures in the table above rather than absent from them. What has not
  been done is separating its cost out from the answer's.

Neither is urgent — the totals they sit inside are one to two orders of magnitude under budget, which
bounds them from above. But "not urgent because it is small" is a different claim from "not on the
path", and only the first one is true.

## What this does not cover

**Nothing measures the framing or the queue.** #58 narrowed this rather than closing it. The status
command reports what a real server's own requests cost, from the dispatch table outwards, so a user
reporting slowness now produces numbers instead of adjectives — but the clock starts when the handler
is entered. What is still unmeasured is the header parse, the JSON envelope, and the time a request
waits behind an earlier message on the ordered worker.

The queue wait is the interesting one of the three, because it is the only one that can be large: it
is exactly what the reading worker was restructured to keep short in #50 and #51. It is deliberately
left out of the status figures rather than overlooked — folding it in would make a hover row depend
on unrelated traffic, and stop it meaning the same thing as the hover row in the table above, which
is the one property that lets the two be compared at all. `CompileScheduler.Pending` and the
in-flight counts on `DeferredAnswers` are what a stuck server shows instead, and the status report
prints both.

`DocumentSemantics` does not serialize two concurrent misses for one buffer, so a classification
request overlapping the debounced compile can compile the same text twice. It is a known cost, it is
counted by `DocumentSemantics.Compilations`, and at these latencies it is not worth the machinery to
prevent — which is a conclusion this measurement licenses rather than an omission.
