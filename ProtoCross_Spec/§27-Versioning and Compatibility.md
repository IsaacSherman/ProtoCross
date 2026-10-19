## 27. Versioning and Compatibility

### 27.1 Language Versioning

**Decided: a source with no language-version declaration is language 1.x, forever.** There is no
declaration in 1.0, and none is needed: every 1.x compiler reads a source as 1.x, and so will a 2.0
compiler. A minor or a patch release only adds to the language ([27.2](#272-compatibility-policy)), so a source
written for 1.0 keeps its meaning across 1.x.

- Fixing what no declaration means is the one part of this that cannot come later, since every source
  written for 1.0 has none.
- Where a declaration would go, in each source or in the project, and whether one compilation could mix
  versions, are questions for the version that first needs one.
- No declaration meaning exactly 1.0 was the alternative. Every feature added after 1.0 would then need a
  declaration in each source that used it.

Possible future syntax:

```protocross
language "2.0";
```

### 27.2 Compatibility Policy

**Decided: a release keeps to the meaning of its version number.**

- A patch release fixes bugs, and may add diagnostics.
- A minor release may add syntax and features that leave every program meaning what it meant.
- A major release may change semantics.

ProtoCross makes every effort to stay compatible by these rules, but reserves the right to change a
behavior, as long as every available backend behaves the same way afterwards. A fix that brings a backend
into line with this specification is such a change, and may come in any release, because a backend that
behaved differently from another was the bug.

The parts of the policy:

- **Source.** A source that compiles under a release compiles under every later release of its major
  version, and means the same, within that reservation ([27.1](#271-language-versioning)).
- **Generated API.** Within a major version, a source generates the same public names and signatures in
  every backend. Formatting, comments and private helpers may change in any release, so generated code is
  not the same text from one release to the next. A name is changed only where what it named never
  compiled.
- **Diagnostics.** A code and its severity are stable within a major version, and a code is never reused
  ([26](./§26-Diagnostics.md#26-diagnostics)).
- **The IR and backends.** No compatibility is promised in 1.0, and no third-party backend is supported
  ([22.3](./§22-IR%20and%20Compiler%20Architecture.md#223-what-a-compilation-answers)).
