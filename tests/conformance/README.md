# ProtoCross Conformance Vectors

This directory is the answer to the question in spec 25.2: does every backend produce the *same*
answer for the same input?

Golden tests over emitted source only say that a backend emits what it emitted last time, and they
say it one language at a time. The vectors here are compiled by every backend, built with a real
compiler, and executed. Each one declares its expectation once, in ProtoCross, and every backend has
to agree with it.

```text
tests/conformance/
  protos/<vector>.proto        each vector's schema, named after it
  vectors/*.pcross             the vectors themselves
  vectors/<policy>/            vectors compiled under a non-default language policy
    protocross.config.xml       what makes them non-default
    *.pcross
  vectors/sweep/               generated vectors, under the default policy
  vectors/<policy>/sweep/      generated vectors, under that directory's policy
  vectors/[<policy>/]multi/<vector>/
    <vector>_*.pcross           one vector written across several sources, compiled as one program
```

The harness lives in [`tests/ProtoCross.Tests/Conformance/`](../ProtoCross.Tests/Conformance) and runs
as part of `dotnet test`.

## Vector format

A vector is a ProtoCross source file whose `test` declarations (spec 25.3) are the vectors:

```protocross
test DivisionCase.quotient "truncates a negative quotient toward zero" {
    receiver {
        numerator = -7;
        divisor = 2;
    }

    expect return -3;
}
```

Spec 25.2 sketched a separate YAML format. This repository uses the `test` declaration instead,
because it is already bound and type-checked against protobuf descriptors: a fixture field that does
not exist, or an expectation whose type does not match the method's return type, is a compile error
rather than something discovered when a generated test fails to build. Expectations are ProtoCross
literals bound to the method's return type, which makes them language-independent without needing a
serialization format of their own.

## Adding a vector

1. Add a schema for it to `protos/`, named after the vector (`foo.proto` for `foo.pcross`), in
   `package protocross.conformance` unless the package is the subject. Every schema there is
   generated and linked, so dropping the file in is enough. **Give it a message of its own.** Every
   vector is compiled into a single C# assembly, so two vectors declaring a method of one name on one
   message would declare it twice there (`ConformanceVectorTests.NoTwoVectorsDeclareOneMethod`
   enforces this), and a message of its own means never having to check. A schema per vector, rather
   than one they all share, is what lets two branches add vectors at once without meeting at the end
   of the same file.
2. Drop a `.pcross` file into `vectors/`. It is discovered automatically; nothing needs
   registering. The search is recursive, so a vector may live in a subdirectory.
3. Run `dotnet test ProtoCross.slnx`.

To pin behavior under a non-default language policy, put the vector in a subdirectory with its own
`protocross.config.xml`. Config discovery walks up from the source file and stops at the nearest
match, so the file governs that directory and nothing else -- which means the corpus exercises real
discovery rather than a hook that exists only for tests. `vectors/checked/` and
`vectors/saturating/` are the two that do this today. Vectors compiled under different policies
still build into the one C# assembly and the one C++ link, because both generated runtime files
carry every policy and are therefore identical whichever one was selected.

To pin what happens *between* sources -- a call from one into another, one receiver extended in two,
a file that holds only tests -- give the vector a directory of its own under `multi/`.
`vectors/multi/foo/` is the vector `foo`: every `.pcross` in it is compiled together as one program
(spec 5.3), against the schema `protos/foo.proto`. Name each source after the vector,
`foo_<part>.pcross`. Every vector is generated into one workspace, and each source's files are named
after the source, so a source named anything else could overwrite another vector's files
(`ConformanceVectorTests.NoTwoSourcesInTheCorpusGenerateFilesOfOneName` catches it). A `multi/`
directory goes under a policy directory the same way a single file does, as
`vectors/checked/multi/foo/`, and its sources find that policy by the same upward search.

One constraint is worth knowing before writing one:

- **Take divisors from fixture fields, not literals.** A non-zero literal divisor is proof that an
  `on_zero` clause is unreachable, and the compiler warns about it (PC0056). A field-supplied
  divisor keeps both the zero and non-zero paths live.
  That is a rule about integer division. A floating-point division takes no clause at all, so
  `constant_divisor.pcross` writes both operands out deliberately: what it pins is what the
  backends do with a quotient they can work out before the program runs.

## Generated vectors

The `sweep/` directories hold vectors nobody wrote by hand: every numeric and bitwise operator and
every conversion over the values where each can go wrong, several thousand rows in all. They and
their schemas are written by
[`tests/ProtoCross.Tests/Conformance/Sweep/`](../ProtoCross.Tests/Conformance/Sweep), and each file
says so on its first line. **Do not edit them.** Change the generator, then rewrite them:

```bash
PROTOCROSS_REGENERATE_SWEEP=1 dotnet test ProtoCross.slnx --filter "FullyQualifiedName~ArithmeticSweepTests"
```

`ArithmeticSweepTests` fails when a committed file is not what the generator writes, and when a
generated file is left behind that the generator no longer writes. They are committed rather than
generated as the suite runs because the harness compiles what is in the directory, a failure should
point at a line that exists, and a file that exists can be opened in the editor.

Each test walks one table of rows and returns the index of the first row the backend got wrong, or
`-1`, which is what it expects. So a failure reads `expected -1, actual 17`, and every row carries its
index in a trailing comment: search the vector for `// 17` inside the failing test.

The expected values do not come from either backend:

- **Integer results** are computed exactly, in arbitrary precision, and only then brought into range
  as the policy says: reduced modulo 2^N, clamped, or, under `Checked`, left out of the table. Each
  way an operator can overflow under `Checked` is a test of its own instead, expecting termination,
  using the narrowest overflow among the boundary values. A shift reduces its count modulo the width
  first, and keeps the low bits of its result whatever the policy, since none governs it.
- **Floating-point results** are C#'s own arithmetic, which is the reference (spec 10), done in the
  type's own precision. A row expecting NaN sets `nan` rather than a value, and every other row is
  compared by `1 / x` as well as `==`, which is what tells `0.0` from `-0.0`.
- **An integer converted to a floating-point type** is rounded from its exact value, to nearest with
  ties to even, rather than by a cast that might round twice.

The generated vectors are left out of `CompiledCorpus`, which the editor sweeps walk position by
position. They repeat a few constructs thousands of times, so they add nothing those sweeps would not
already meet, and they would multiply what the sweeps cost.

## What the harness checks

| Test | Checks |
|---|---|
| `ConformanceVectorTests` | Every vector compiles, declares at least one test, and declares no method another vector does, and no two sources generate files of one name. Needs only protoc, so it always runs |
| `ConformanceTests.CSharpRunsEveryConformanceVector` | The vectors build into one C# project and every test passes |
| `ConformanceTests.CppRunsEveryConformanceVector` | Each source that declares tests builds into a C++ executable, and every test passes |
| `ConformanceTests.BothBackendsRunTheSameVectors` | The set of tests C# ran, the set C++ ran, and the set declared in the corpus are the same set |

The last one is the one that matters. Each backend passing on its own is not enough: a driver that
ran zero tests also exits 0. Both backends report each test by the same backend-independent
identity — `IrTest.Identity`, which the C# backend uses as the xUnit display name and the C++ driver
prints — so the sets can be compared directly.

When a backend cannot run, its tests skip with a message naming the missing tool rather than
failing. A fully equipped machine should report no skips.

## Coverage

| Vector | Covers |
|---|---|
| `integer_overflow` | Two's complement wrapping for `int32`, `int64`, `uint32`, `uint64` addition, subtraction, multiplication, and negation (spec 10.1) |
| `integer_division` | Truncation toward zero, `on_zero` fallbacks, `on_zero fail`, and `MIN / -1` and `MIN % -1` (spec 10.2, 10.2.1) |
| `floating_point` | IEEE 754 division including by zero: infinities, NaN, and NaN comparison behavior |
| `floating_remainder` | `%` on `float` and `double`: the sign of the dividend, exactness, negative zero, and a zero, infinite, or NaN operand |
| `constant_divisor` | Floating-point division whose operands are both written into the source, which the C++ front end works out while compiling rather than leaving to run: a literal, negated, converted, and computed zero divisor, and the field-over-constant divisions that cannot be folded (spec 10.2, 23.1) |
| `ordinary_names` | `virtual` as a parameter, a local, a method, and a test target: an ordinary name in ProtoCross that both targets reserve, so each has to escape it (spec 17) |
| `control_flow` | `if` / `else if` / `else`, `while`, `while true` with `break`, `continue`, and `for`-`in` (spec 15) |
| `strings` | String equality, string returns, and literals containing characters both backends must escape (spec 11) |
| `enum_types` | Enum-typed locals, parameters, and returns, and named enum values in comparisons, returns, branches, and fixtures (spec 12) |
| `casts` | Explicit conversions: mixed-width arithmetic, integer narrowing and signedness, int-to-float rounding, and float-to-integer truncation, saturation, and NaN (spec 10.3) |
| `whimsy_math` | A larger end-to-end fixture with repeated protobuf objects, method calls, arguments, mixed numeric widths, explicit casts, unsigned wrapping, float/double math, strings, booleans, and compound control flow |
| `presence` | `has` over the three presence kinds a proto3 schema can carry, every guard shape, and the cases that separate explicit presence from a comparison against the default (spec 8.4, 13.1) |
| `keyword_fields` | Fields whose names C++ cannot use as they stand -- keywords, a capitalized keyword, a macro, and a generated member's name -- read, tested with `has`, iterated, and set in fixtures, as scalar, message, and repeated fields (spec 24.2) |
| `keyword_types` | Messages, enums, and enum values whose names C++ cannot use as they stand -- a keyword, a generated member's name, a nested type under an escaped parent, and keyword and macro values of a top-level and a nested enum -- as receivers, locals, parameters, returns, and fixture values (spec 24.2) |
| `keyword_package` | A package whose components are C++ keywords: the namespace the generated functions live in, a call between them, and a message and an enum value qualified with it (spec 24.2) |
| `property_names` | Fields whose C# property protoc renames -- after the message's own name, after a generated member, a letter after a digit, and an underscore before a leading digit -- read, tested with `has`, iterated, and set in fixtures, as scalar, message, and repeated fields (spec 24.1) |
| `literals` | Every numeric literal form -- hexadecimal, binary, separators, exponents, `__INF` and `__NAN` -- and the rules that type one: its natural type, a `-` written on it being part of it so that int32 and int64 MIN are literals, a literal on the left adopting the type on the right, rounding once to `float`, and `-0` as negative zero where a `double` is expected (spec 6.6, 10.3) |
| `bitwise` | `&` `\|` `^` `~` `<<` `>>`: where each binds among the other operators, the type a literal beside one takes -- the value shifted takes the type expected of the shift and never the count's, and a count literal keeps its own -- a count reduced modulo the width whatever its type and sign, a signed right shift copying the sign bit in and an unsigned one zero-filling, a left shift discarding what passes the width, and the idioms of testing, clearing and packing bits (spec 9.2, 10.1) |
| `compound_assignment` | `+=` `-=` `*=` `/=` `%=` `&=` `\|=` `^=` `<<=` `>>=`, each storing what its long form computes: the whole right side as one operand, an integer division's `on_zero` clause after its divisor, a literal on the right taking the target's type and a shift count keeping its own, the target as its own operand, and a local accumulating across a loop (spec 9.2) |
| `multi/cross_file` | One program written across three sources. Two extend one receiver and each calls a method the other declares, so C# has two parts of one static class and each C++ header includes the other. The third holds only tests, so its behavior output is empty and its driver includes the headers of the sources it tests (spec 5.3, 24.1, 24.2) |
| `sweep/integer_sweep` | Every integer `+ - * / %` and unary `-` over every pair of boundary values of each integer type, with the fallback for a zero divisor, and every comparison of the same pairs, under the default wrapping policy (spec 10.1, 10.2). Generated |
| `sweep/bitwise_sweep` | Every `&` `\|` `^` over every pair of boundary values of each integer type, `~` of each one, and `<<` and `>>` of each one by every count worth trying, in the value's own type; and one value shifted by every count in each other integer type (spec 9.2, 10.1). Generated |
| `sweep/floating_sweep` | Every floating-point `+ - * / %`, unary `-` and comparison, in `float` and `double`, over both zeros, an inexact fraction, the largest finite value, the smallest subnormal, both infinities and NaN (spec 10.2). Generated |
| `sweep/conversion_sweep` | Every `as` between the six numeric types, over each integer type's boundary values and rounding ties, and the floating-point values either side of each integer type's range and of `float`'s (spec 10.3). Generated |
| `checked/sweep/checked_integer_sweep` | The integer sweep under the checked policy: every result that fits, and the narrowest overflow in each direction each operator can overflow terminating (spec 10.1). Generated |
| `saturating/sweep/saturating_integer_sweep` | The integer sweep under the saturating policy, every result clamped (spec 10.1). Generated |
| `checked/checked_arithmetic` | The checked overflow policy: overflow at each width terminates with exit code 70, and neither `MIN % -1` nor a left shift past MAX does (spec 10.1, 10.4) |
| `saturating/saturating_arithmetic` | The saturating overflow policy: clamping at both bounds for every operation and width, and a left shift past MAX left unclamped (spec 10.1, 10.4) |
| `checked/checked_compound_assignment` | Compound assignment under the checked policy: an overflow in the operation it stands for terminates, and a left shift past MAX does not (spec 9.2, 10.1) |
| `saturating/saturating_compound_assignment` | Compound assignment under the saturating policy: an overflow in the operation it stands for stores the bound, and a left shift past MAX is not clamped (spec 9.2, 10.1) |

## Adding a backend

The harness is written so a third backend is a small addition, not a third copy:

1. Implement `ITestBackend`, as `CSharpBackend` and `CppBackend` do.
2. Report each test by `IrTest.Identity`, so the agreement check can see it. The C# backend uses it
   as the xUnit display name; the C++ driver prints `[ok] <identity>` and `[FAIL] <identity> ...`.
3. Add a workspace type under `tests/ProtoCross.Tests/Harness/` that writes the generated files,
   runs protoc for that language, builds, and executes. `ProcessRunner` and `Toolchain` already
   handle process plumbing and tool discovery.
4. Add a `ConformanceRun` for it in `ConformanceFixture` and a fact in `ConformanceTests`.

## Not covered yet

- **Enum values with no declared name.** proto3 enums are open, so a field can hold a number the
  schema does not name, but a fixture can only set a value that exists. What happens to an unknown
  value is undecided (spec 12), so there is nothing to pin.
- **Maps, oneof, and mutation** are not implemented in the language, so there is
  nothing to write a vector against.
- **The compiler's refusals** are not here and cannot be: a vector has to compile. `PC0078`,
  `PC0079`, and `PC0080` are covered by `PresenceTests`, and the configuration diagnostics by
  `ProjectConfigTests`.
- **The negative case for `expect fail` is not in the suite.** That a passing `expect fail` really
  does detect a method returning normally was verified by hand, by pointing such a test at a
  non-zero divisor and confirming it fails with "the method returned instead of terminating the
  process". Covering it automatically means building a deliberately wrong corpus alongside the real
  one, which is a second full build of everything; what the suite asserts today is that both
  backends emit the rejection paths. The half of the check that is machine-verified every run is
  strict: the verdict is an equality test against the failure exit code 70, so a child that fell
  over for an unrelated reason fails rather than passing.
