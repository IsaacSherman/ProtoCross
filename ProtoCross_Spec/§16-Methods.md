## 16. Methods

### 16.1 Method Attachment

Methods are attached to protobuf message types using `extend`, and a message's factory declares the
methods that build one ([16.3](#163-factories)).

```protocross
extend DetectorReading {
    fn calculate_rate() -> double {
        return counts / live_time on_zero fail;
    }
}
```

Normative Requirements:

- All ProtoCross-defined methods are public, except a factory's helpers, which only that factory may
  call ([16.3](#163-factories)).
- Method behavior must not depend on target-language inheritance.
- Method names share a namespace with protobuf fields on the receiver. A method whose name collides
  with a field is `PC0023`. A factory's methods and helpers are in that namespace too, so no name
  means one thing called on a message and another called through its factory. An extension declared inside the receiver is not one of its fields
  ([13.4](./§13-Messages.md#134-extensions)), so a method may take its name.
- Overloading is not supported. Two methods with the same name on the same receiver are `PC0022`,
  even if their parameter lists differ, and even if they are declared in different sources of one
  compilation. The diagnostic stands on the later declaration, and when the earlier one is in
  another source, the message says where it is.
- A method may call any method the compilation declares, whichever source it is in and wherever in
  that source it is written, and a `test` may target any of them. Sources are not ordered: every
  method in every source is declared before any body is bound.
- A method whose declaration is syntactically incomplete may still be bound into a partial semantic
  model for editor use, but it is not callable when it lacks a usable declaration name.
- A method declared `mut fn` may change its receiver, and one declared `fn` may not
  ([18](./§18-Mutability.md#18-mutability)). `mut` is not a keyword anywhere else ([6.4](./§6-Lexical%20Structure.md#64-keywords)).

Open Questions:

- ~~Should methods ever be allowed to mutate the receiver?~~ Decided: a `mut fn` may
  ([18](./§18-Mutability.md#18-mutability)).

### 16.2 Parameters and Returns

Normative Requirements:

- Parameters are named and typed: `name: type`.
- `void` is allowed only as an omitted or explicit method return type. It is not allowed for
  parameters or variables (`PC0024`).
- Methods have either one return value or no return value. Multiple return values are not supported.
- A non-void method must return a value on every path that can reach the end of the body.
- A void method may use `return;`.
- **A call to a void method has no value.** It may stand as a statement of its own, and anywhere a
  value is expected it is `PC0100`: an operand, either side of `==`, a `var` initializer, an
  argument, or a receiver. That is the call's one diagnostic, so whatever holds it reports nothing
  further about it, and a call to a `mut fn` that returns nothing is not also told it is inside an
  expression ([18](./§18-Mutability.md#18-mutability)). `return nothing();` in a void method is `PC0031`
  alone, because that method returns no value at all.
- Parameters are not assignable, and a message passed as one cannot be changed through it
  ([18](./§18-Mutability.md#18-mutability)).

Open Questions:

- ~~Should recoverable errors be represented through user-defined protobuf messages, a future standard
  library `Result`, or some other convention?~~ Decided: a refusal of a type the author chooses, declared
  after the return type and answered at every call, which every method may declare ([16.3.1](#1631-refusals)).

### 16.3 Factories

**Decided: a message's factory is a `factory` block, whose methods build that message and are called
on nothing.**

```protocross
factory Invoice {
    fn create(options: InvoiceOptions) -> Invoice {
        return new Invoice {
            number: options.number,
            total_cents: line_total(options),
        };
    }

    helper fn line_total(options: InvoiceOptions) -> int64 {
        return options.line_count * options.unit_cents;
    }
}

extend Order {
    fn invoice() -> Invoice {
        return Invoice.factory.create(new InvoiceOptions { number: number });
    }
}
```

In the language, a factory is part of its message. `factory Invoice` declares no type: its methods
are members of `Invoice`, and code calls them as `Invoice.factory.create(options)`. What is generated
differs from that model on purpose. Each message's factory becomes a class of its own
([24](./§24-Generated%20API%20Strategy.md#24-generated-api-strategy)), and the compiler knows where to find it.

Normative Requirements:

- **A factory block is a top-level declaration**, `factory` and the message it builds, then its
  methods in braces ([7.1](./§7-Grammar%20and%20Syntax.md#71-implemented-grammar)). It resolves the message as an `extend` block resolves its receiver, and one whose
  message does not resolve is skipped as an `extend` block is. A message needs no `extend` block to
  have a factory.
- **Every method of a factory returns the message it builds.** One that returns anything else,
  nothing included, is `PC0123`, and its help says to mark it a helper if what it computes is part of
  building one.
- **A message has one factory in a compilation.** A second block for it is `PC0124`, and its methods
  are bound for the names in them and called by nothing. Spreading one factory across sources would
  only be convenience, and C++ could not declare it ([24.2](./§24-Generated%20API%20Strategy.md#242-c)).
- **A factory's methods and helpers share the message's one set of member names** ([16.1](#161-method-attachment)).
  One that shares a name with a field is `PC0023`, and with a method or another member of the factory,
  `PC0022`, whichever block declared the first.
- **A factory body follows every rule a method body does, except that it has no receiver.** A bare
  name reaches no field, and a bare name that is not a local or a parameter is `PC0037`, whose message
  says why. A method of the message is called on a message the factory holds, and called bare, it is
  `PC0093`. A `mut fn` may be called on a message the factory built and holds in a local, and not on
  a parameter ([18](./§18-Mutability.md#18-mutability)). A factory's method or helper marked `mut` is `PC0128`, and is bound as
  though it were not.
- **A factory's own methods and helpers call each other by bare name**, as a method reaches its
  receiver's members, and anywhere else as `Message.factory.method(…)`. A factory method called on a
  message, `invoice.create()`, or by bare name in an `extend` block, is `PC0130`, whose help spells the
  call through the factory. A call through the factory of a message that has none, or to a name it
  lacks, is `PC0044`. Anywhere else the word is read as the factory only where the message has one:
  `Message.factory` written anywhere but before a call's name is then `PC0131`, and
  `Message.factory.create` not called is `PC0040`, as a method is. For a message with no factory either
  is the unknown name `PC0037` it was before factories existed.
- **`Message.factory` is read as the factory only where `Message` is not a value.** A local, a
  parameter or a field named like the message wins, as a value wins over an enum's name
  ([12](./§12-Enums.md#12-enums)), and the dot reaches into it.
- **A nested type named `factory` takes the word.** A factory for a message that declares a nested
  message or enum named `factory` is `PC0125`. A message with such a type and no factory keeps reaching
  it as `Message.factory`, as it always has.
- **A helper is `helper fn`, and only its own factory may call it.** It need not return the message.
  A call to one from anywhere else, and a `test` that targets one, is `PC0129`. A helper written in an
  `extend` block is `PC0127` and is bound as an ordinary method: there are no helpers outside a factory
  and no access modifiers. The rule is the binder's, so every backend behaves the same, including one
  with no private members. It holds for now. Making helpers public later would break no caller.
- **A test targets a factory's method as `Invoice.factory.create`, with no receiver**
  ([25.3](./§25-Testing%20and%20Conformance%20Vectors.md#253-author-written-protocross-unit-tests)).
- **A factory for one of protobuf's well-known types is `PC0133`**, a warning, for the reason an
  `extend` block of one is `PC0077`: consumers have the type without the factory.
- **A factory whose generated class would share a name in its namespace is refused** with
  `PC0126`, whether with another factory's class or with a schema's type or namespace, in either
  backend ([24](./§24-Generated%20API%20Strategy.md#24-generated-api-strategy)).
- **A factory's method may refuse**, by declaring what it refuses with, as any method may ([16.3.1](#1631-refusals)).

Factories offer no support for injection or mocking in 1.0. A factory method is pure: it does no I/O
and calls nothing outside ProtoCross ([20](./§20-I-O,%20Threading,%20and%20Side%20Effects.md#20-io-threading-and-side-effects)), so nothing in it needs replacing in a test. A
consumer who wants an interface writes a wrapper that forwards to the generated methods, which is
how consumers already adapt protoc's sealed message classes. Generating one was considered and left
out, because nobody needs it yet and adding it later breaks no caller.

### 16.3.1 Refusals

**Decided: a method may refuse, by declaring what it refuses with after its return type, and every
call to one says what a refusal makes of it.**

```protocross
factory Invoice {
    fn create(options: InvoiceOptions) -> Invoice refuses InvoiceRefusal {
        require_lines(options) on_refused refuse;
        return new Invoice { number: options.number };
    }

    helper fn require_lines(options: InvoiceOptions) refuses InvoiceRefusal {
        if options.line_count <= 0 {
            refuse new InvoiceRefusal { reason: RefusalReason.NO_LINES };
        }
    }
}

extend Order {
    fn draft() -> Invoice {
        return Invoice.factory.create(options()) on_refused new Invoice { number: "draft" };
    }

    fn unit_cents() -> int64 refuses InvoiceRefusal {
        return total_cents / line_count on_zero refuse new InvoiceRefusal { reason: RefusalReason.NO_LINES };
    }
}
```

A refusal is how a method says no without ending the program. Its type is the author's: a message
that says why, an enum, a string, or any type a method may return. A team shapes it as it needs, and a
consumer reads it as the ordinary value it is ([24](./§24-Generated%20API%20Strategy.md#24-generated-api-strategy)). A method that only returns declares nothing, and is
called as it always was.

Normative Requirements:

- **A method declares its refusal after its return type, `-> T refuses R`**, or after its parameters
  where it returns nothing. Any method may: a factory's method or helper, and a method of an extend
  block, `mut fn` included. A refusal of `void` is `PC0024`.
- **`refuse value;` ends the method with its refusal**, as `return` ends it with its result, so
  nothing after it on its path runs and a path that refuses needs no `return`. The value must already
  have the type the method refuses with (`PC0137`); no implicit conversion is applied. A `refuse` in a
  method that declares no refusal, or in a test, is `PC0136`. The value is evaluated as the method ends,
  so it may not itself refuse (`PC0142`) or call a `mut fn` (`PC0095`).
- **A division, a conversion to an enum and a map lookup may refuse where they would otherwise give no
  value**, with `refuse` and a value as their clause: `a / b on_zero refuse value`,
  `n as Grade on_unknown refuse value`, `m[k] on_missing refuse value`. Each ends the method with that
  refusal, as `refuse` does, and is bound as `refuse` is: only in a method that declares a refusal
  (`PC0136`), and with a value of the type it declares (`PC0137`). The value is evaluated only where the
  operation gives none, after its operands, as a fallback is
  ([10.2.1](./§10-Numeric%20Semantics.md#1021-the-on_zero-clause), [12.1](./§12-Enums.md#121-an-enums-number), [14.2](./§14-Repeated%20Fields%20and%20Collections.md#142-maps)).
- **A call to a method that may refuse says what a refusal makes of it**, with an `on_refused` clause
  after the call. One without it is `PC0138`, whose help lists the forms that fit where it is written:
  - `on_refused <fallback>` gives the fallback instead. It must have the type the call gives
    (`PC0140`), and it is evaluated only where the method refused, after the call, as an `on_zero`
    fallback is only for a zero divisor ([9.3](./§9-Expressions%20and%20Operators.md#93-evaluation-order)). A method that returns nothing gives no value to fall
    back from, so a fallback on a call to one is `PC0140` too.
  - `on_refused fail` ends the program as `on_zero fail` does, with exit code 70 and a line on standard
    error naming the method as the language does, `acme.Invoice.factory.create refused`
    ([10.2.1](./§10-Numeric%20Semantics.md#1021-the-on_zero-clause)).
  - `on_refused refuse` ends the calling method with the same refusal. A refusal is passed on as it is,
    so the caller must refuse with the same type (`PC0141`).
- **A refusal ends the method only from an operation that stands on its own**: a call as a statement,
  and a call, a division, a conversion or a lookup as a `var` initializer, a local's new value or a
  `return` value. These are the places a mutating call may stand ([18](./§18-Mutability.md#18-mutability)), because the method ends there
  and nothing beside the operation may have been evaluated. Anywhere else, a field's compound
  assignment included, it is `PC0142`. A `mut fn` that may refuse stands in the same places as one that
  cannot, its clause answered after it returns.
- **A change a method made before it refused stands.** A `mut fn` that changes its receiver and then
  refuses leaves the receiver changed, and a fallback that reads it reads it changed. Nothing is
  undone, as nothing is undone before `return`.
- **An `on_refused` clause on a call that cannot refuse is `PC0139`**, a warning, as an `on_zero` on a
  division by a non-zero literal is: it cannot run, and the call means what it would without it.
- **A refusal is not a value a method can hold.** No type is "a message or a refusal", and nothing in
  the language reads one but the test that expects it ([25.3](./§25-Testing%20and%20Conformance%20Vectors.md#253-author-written-protocross-unit-tests)). This is what lets a refusal need no
  generic type: it exists only between the method that refuses and the call that answers it.

A refusal is the way a method declines without ending the program, so a project that forbids ending
it ([10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy)) writes one wherever it would otherwise have written `fail`. `fail` does not turn into a
refusal by itself: a refusal's type is the author's, and only the author can say what value means
"no time" or "no price". Whether a value may hold a refusal remains open
([19](./§19-Error%20Handling%20Without%20Exceptions.md#19-error-handling-without-exceptions)).
