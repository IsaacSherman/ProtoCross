## 25. Testing and Conformance Vectors

### 25.1 Test Categories

The conformance suite should include:

- Parser tests.
- Type-checker tests.
- IR golden tests.
- Backend source golden tests.
- Cross-language runtime behavior tests.
- Numeric edge-case tests.
- Presence/default-value tests.
- Repeated field and map tests.
- Error handling tests.
- Partial-binding and diagnostic-recovery tests.
- Symbol identity tests for editor-facing semantic data.

### 25.2 Conformance Vector Format

Decided. A conformance vector is not a separate file format at all: it is a ProtoCross `test`
declaration ([25.3](#253-author-written-protocross-unit-tests)) in a `.pcross` file, paired with the `.proto` it imports.

- **Format.** The `test` declaration, rather than YAML, JSON, or text format. It is already parsed,
  name-resolved, and type-checked against protobuf descriptors, so a fixture field that does not
  exist, or an expectation whose type does not match the method's return type, is a compile error
  rather than something discovered when generated test code fails to build. A second, untyped way
  to say the same thing would have to re-earn all of that.
- **Expected results.** ProtoCross literals bound to the method's return type. Being bound to the
  IR rather than to a serialization makes them language-independent without a wire format of their
  own. Every value of every numeric type has a literal ([6.6](./§6-Lexical%20Structure.md#66-numeric-literals)), `int64` MIN, `uint64` MAX,
  the infinities and NaN among them, so an expectation never has to be computed.
- **Compile and execute, not inspect.** Golden assertions over emitted source only state that a
  backend emits what it emitted last time, one language at a time. The suite compiles the generated
  code with a real compiler and runs it, and requires every backend to have run the same set of
  vectors, identified by a backend-independent test identity that each backend reports.

The reference corpus lives in `tests/conformance/`.

### 25.3 Author-Written ProtoCross Unit Tests

ProtoCross supports author-written unit tests for behavior defined in ProtoCross source. These tests
are distinct from the compiler's own conformance suite:

- Conformance vectors test whether a ProtoCross compiler/backend implements the language correctly.
- ProtoCross unit tests test whether a project's ProtoCross behavior is correct for that project.

Normative Requirements:

- Unit tests are written in a ProtoCross test declaration, in any `.pcross` source: beside the
  behavior they test, or in a source of their own.
- Unit tests are declarative fixtures and expectations, not arbitrary executable ProtoCross code.
- A test names a receiver method, supplies a protobuf receiver value and method arguments, and
  declares the expected return value or expected terminal failure.
- An `expect return` is met when the returned value equals the expected one under `==`, with one
  exception: a NaN expectation is met by any NaN. A NaN is unequal to itself, so without the
  exception no test could say that a result is not a number; and which NaN it is cannot matter,
  because nothing in the language can tell two apart ([6.6](./§6-Lexical%20Structure.md#66-numeric-literals)). `0.0` still meets `-0.0`.
- Test declarations are never emitted into production behavior output. The compiler generates
  target-language test source files into a user-selected test output directory, and only when test
  generation is explicitly requested.
- The compiler should not execute tests by default. Execution belongs to the target language's
  normal test runner or build system.

Syntax:

```protocross
import proto "invoice.proto";

extend Invoice {
    fn total_cents() -> int64 {
        var total: int64 = 0;

        for item in items {
            total = total + item.line_total_cents();
        }

        return total;
    }
}

test Invoice.total_cents "sums line totals" {
    receiver {
        items {
            quantity = 2;
            unit_price_cents = 300;
        }

        items {
            quantity = 4;
            unit_price_cents = 125;
        }
    }

    expect return 1100;
}
```

The `receiver` block is a descriptor-bound fixture initializer, not a general ProtoCross message
literal. Each entry names a protobuf field. Scalar fields use `field = expression;`; message fields
use nested blocks. Repeated fields may appear multiple times. The compiler binds field names and
fixture value types against protobuf descriptors, then a backend may lower the fixture to
target-language message construction code. A future test syntax may also accept protobuf text
format, but fixture semantics must still come from protobuf descriptors.

For methods with parameters, the test declaration should name each argument:

```protocross
test InvoiceItem.discounted_total "applies discount" {
    receiver {
        quantity = 2;
        unit_price_cents = 300;
    }

    arg discount_cents = 50;
    expect return 550;
}
```

For methods expected to terminate through `on_zero fail` or another future terminal failure
mechanism:

```protocross
test InvoiceItem.strict_ratio "zero divisor fails" {
    receiver {
        quantity = 2;
        unit_price_cents = 0;
    }

    expect fail;
}
```

Test generation follows the same shape as normal backend generation in the CLI:

```text
protocross behavior.pcross \
  --target csharp \
  --out generated/src \
  --test-out generated/tests
```

If ProtoCross is run as a `protoc` plugin, test generation should use protoc-style output flags
rather than a special test runner protocol. Since `protoc` passes `.proto` descriptors to
plugins, the ProtoCross behavior/test source file must be named explicitly in plugin options:

```text
protoc \
  --proto_path=protos \
  --protocross_out=generated/src \
  --protocross_opt=source=behavior.pcross,target=csharp \
  --protocross_test_out=generated/tests \
  --protocross_test_opt=source=behavior.pcross,target=csharp \
  invoice.proto
```

`--scaffold` writes a target-specific test project beside generated tests and requires
`--test-out`. Each test backend writes under `<test-out>/<target>/`.

Implemented backend behavior:

- C# generates ordinary test source and can scaffold a `.csproj`.
- C++ generates standalone test executables and can scaffold a CMake project.
- Python has no implementation.

Open Questions:

- ~~Should test declarations live in production `.pcross` files, separate `.pcrosstest` files,
  or both?~~ Decided: in any `.pcross` source, and a project's `<Tests>` group names the sources
  that are there only to test with ([25.3.1](#2531-test-sources-and-the-two-builds)).
- ~~Should a test build refuse a production source that names a type only a test source
  imports?~~ Decided: yes. Production behavior names only types in the production schema closure
  ([25.3.1](#2531-test-sources-and-the-two-builds)).
- Should expected protobuf message values use text format, JSON mapping, binary fixtures, or all
  three?
- Should the compiler embed fixtures in generated source, copy fixture files beside the generated
  tests, or support both?
- Should target test framework selection be a backend option such as
  `--test-opt framework=xunit|standalone|gtest`?
- Should generated tests be stable enough to check in, or treated as build artifacts only?
- What should the eventual `protoc` plugin flag names be?

Decided: `expect fail` runs out of process. A terminal failure cannot be observed from inside the
process it ends, so a backend generates such a test as a driver that relaunches itself for that one
test and inspects how the child ended.

The verdict is the child's exit code, and it is an equality check against the failure code 10.2.1
fixes at 70, not a test for "died somehow". That distinction matters: a child that crashed for an
unrelated reason, or that fell through to an ordinary test run and merely reported failures, must
not be mistaken for a method that terminated. Requiring one exact code across every backend is only
possible because 10.2.1 rules out crash primitives, whose exit codes the host chooses.

### 25.3.1 Test Sources and the Two Builds

**Decided: a test source is a source a project names only in `<Tests>`
([5.4](./§5-Source%20Organization.md#54-projects)). Its methods are helpers, generated with the
tests. Every other source is a production source, and that is every source when there is no
project.** There is no `.pcrosstest` extension: a project's `<Tests>` group already says which
sources are there only to test with, and a second way to say it would be a second thing to keep in
agreement with the first.

- **A production build** compiles the production sources, and leaves every test out. A test is
  still parsed, because it is part of the text, but it is neither bound nor generated, so a test
  that no longer binds (a renamed target, a fixture field the schema dropped) does not stop the
  program shipping. A test that does not parse is still reported: a declaration that does not parse
  cannot say where it ends, and passing over it could pass over the method written after it.
- **A test build** compiles the production sources and the test sources together, each source once
  however many groups name it, and binds every source's tests. It generates:
  - each production source's behavior into the behavior output, exactly as the production build
    would, byte for byte;
  - each test source's behavior into the test output, leaving out anything the behavior output
    already holds, such as a runtime file every source's behavior brings;
  - every source's tests into the test output.

  A test build is one build. It generates nothing unless all of it compiles, tests included: the
  behavior output alone, written beside a failed test build, would look like a finished one.
- **A test source may declare methods.** Any test may target one, and one may call any method,
  since it is generated beside everything it could call.
- **A production source's method may not call a test source's method.** It is `PC0088`, an error,
  wherever the two are compiled together, which includes an editor. Such a method would be generated
  into the behavior output calling something generated only into the test output, and the
  production build would report it as a method that does not exist. The rule is what lets the test
  build's behavior output be the production build's.
- **Adding test sources to a compilation must not make any production behavior valid that would
  be invalid without them.** `PC0088` above, and the three rules below, follow from it.
- **Production behavior may reference only protobuf types in the production schema closure**: the
  schemas production sources import, and every schema transitively reachable from those imports.
  Production behavior is a production source's `extend` blocks and methods, not its tests. Tests,
  and the methods test sources declare, use the full test schema closure: every schema any source
  brings. A type production behavior names that only the test closure declares is `PC0089`, an
  error, meaning that no production source brings its schema into the compilation. Which production
  source imports it does not matter, since sources share their imports
  ([5.2](./§5-Source%20Organization.md#52-relationship-to-proto)). Production behavior is bound
  against the production closure alone, so a test schema can neither make a production name
  resolve nor make one ambiguous.
- **Imports production sources write are resolved without the test sources' directories, and so
  is every schema in the production schema closure.** A schema only a test source's directory holds
  is one the production build cannot find, and a copy there that shadows another is not the one the
  production build loads. An import a production source writes that only such a directory holds is
  `PC0002`, as it is in the production build. A schema the production closure reaches through
  another schema's import, which the compilation loaded from a file the production build would not
  (missing without the test sources' directories, or found elsewhere), is `PC0090`, an error at the
  production import that brings it in.
- **Test sources come after production sources** in a compilation, each in the order given
  ([5.3](./§5-Source%20Organization.md#53-compilation-unit)). Order decides which source's directory
  answers an import first ([5.2](./§5-Source%20Organization.md#52-relationship-to-proto)), which of
  two declarations of one method is the duplicate, and the order of the generated files, so a test
  source can change none of those for a production source.

Implementation Note:

- The `Compilation` API does both builds: a source says which it is with `SourceDocument.Role`, and
  `CompilationOptions.SkipTests` makes a build a production build.
- The command line runs a production build without `--test-out`, and a test build with it, writing
  the behavior output to `-o` and the test output to `--test-out`. That holds without a project
  too, where every source is a production source: a test that no longer binds stops a build only
  when `--test-out` asks for the tests.
- An editor does not take a project yet, so every source it compiles is a production source, and it
  binds every source's tests.
