## Commands

```bash
dotnet build ProtoCross.slnx
```

```bash
dotnet test ProtoCross.slnx
```

The full suite takes several minutes because it builds and runs real generated projects, and longer
with the soak; the README's Building section states the measured times. Filter while iterating
(`--filter "FullyQualifiedName~LexerTests"`), but the unfiltered run is the gate. `protoc`, the .NET
SDK, and a C++ toolchain must be on the machine.

Three checks are switched off by default, because none is what a person mid-iteration wants to wait
for: `PROTOCROSS_SWEEP=1` runs the whole-corpus completion sweep, `PROTOCROSS_SOAK=1` runs the long
editing soak, and `PROTOCROSS_BENCH=1` measures the latency budgets (which also needs `-c Release`
and `DOTNET_gcServer=0`, and refuses to run without either: the suite runs the server garbage
collector, and the language server ships with the workstation one).
`.github/workflows/ci.yml` turns the first two on for every pull request to `main` or `sprints/**`
that changes more than Markdown, and for such pushes to `main`, so what a local
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

### Every full run keeps a record of its failures

A full run's failures are kept whole, for the last three runs. A failure filtered down to its name
cannot be told from a flake afterwards, and a flaky test is diagnosed by comparing the runs it failed
in. Each run writes its own folder under `TestResults/full-suite/`, which git ignores, holding every
test project's TRX, with each failure's message, stack trace and output, and the console output whole:

```bash
run="TestResults/full-suite/$(date +%Y%m%d-%H%M%S)-$(git rev-parse --short HEAD)"
mkdir -p "$run"
dotnet test ProtoCross.slnx --logger trx --results-directory "$run" 2>&1 | tee "$run/console.txt"
```

Then delete all but the three newest runs:

```bash
ls -d TestResults/full-suite/*/ | sort | head -n -3 | xargs -r rm -rf
```

- **Filter what you read, never what is kept.** Search `console.txt` for `[FAIL]` to find the
  failures, and read each one's message in the TRX.
- **Before calling a failure flaky, look for it in the other two runs.** A test that failed before is
  not a one-off, and the earlier message says whether it failed the same way.
- **Name the run's folder** when reporting its failures, so they can be read again.

The same holds with `PROTOCROSS_SWEEP`, `PROTOCROSS_SOAK` or `-c Release` set: they are full runs too.

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