## 5. Source Organization

### 5.1 Files

ProtoCross source files use the extension:

```text
.pcross
```

Decision:

- The extension is `.pcross`. The CLI and tests use this extension, and path-based compilation
  treats the file path as a source identity rather than deriving semantics from any alternate suffix.

### 5.2 Relationship to `.proto`

A ProtoCross file imports one or more protobuf schema files directly.

Example:

```protocross
import proto "inventory.proto";

extend InventoryItem {
    fn total_value() -> int64 {
        return quantity * unit_price;
    }
}
```

Normative Requirements:

- Imports use `import proto "path/to/schema.proto";`.
- The path is resolved against compiler include paths, then against the directory of each source in
  the compilation ([5.3](#53-compilation-unit)), a production source's before a test source's
  ([25.3.1](./§25-Testing%20and%20Conformance%20Vectors.md#2531-test-sources-and-the-two-builds)).
- **A compilation's sources share their imports.** A type any source imports can be named in every
  source, in the same way a type that an imported schema imports can already be named in the file
  that imports the schema. A schema imported by several sources is loaded once.
- **One directory is one search path, however it is spelled.** Include paths are searched in the
  order given, and a directory already in that order is not added again — whether the second
  spelling differs by case where the file system ignores case, by a trailing separator, by the
  alternate separator, or by being the source directory that would have been appended anyway. A
  directory searched twice is a redundant `--proto_path`, a diagnostic that names it twice, and, for
  a cached load ([21.1](./§21-Interoperability%20With%20Protobuf.md#211-descriptor-input)), a second key for one configuration.
- Well-known protobuf imports may be resolved by the descriptor loader's implicit include paths.
- **The directories an import is resolved against are one ordered list**, and one place answers for
  it: the include paths, then each source's own directory, then the loader's implicit ones. Everything
  that has to predict resolution — a diagnostic naming where the compiler looked, an editor offering
  the paths that would resolve — asks that list rather than assembling one of its own. Two assemblies
  are two answers to which root wins, and the disagreement surfaces as a path that is offered and then
  not found.
- **A schema beside a source that another directory shadows is `PC0087`, a warning.** The first
  match in that list is what every source gets, so a source whose own directory holds a *different*
  file under the path it imports is compiled against the other one, and whatever it names from its
  own copy may not exist. The warning names both files. Copies with the same bytes are not reported,
  since nothing differs whichever is loaded, and neither are copies that differ only by a leading
  UTF-8 byte order mark or by CRLF where the other has LF, which protoc reads alike. Nothing else is
  set aside: any other line separator can sit inside a string default, where it is part of the
  value. Resolution does not change: protoc knows a schema by its path under its root, so one
  compilation cannot load two schemas of one path, and the code protoc generates from two of them
  could not be built into one program either.
- **An import that resolves to nothing names the schema it came closest to naming**, where the
  directory it named holds one that differs from it by little enough to be a plausible slip. `PC0002`
  still says where the compiler looked; the near match comes first, because a list of directories only
  helps a reader who already knew what they were aiming at. Nothing is suggested from a directory the
  author did not name, and **nothing is suggested from a directory too broad to be read within a
  bounded amount of work** — a diagnostic that takes seconds to print is a worse failure than one that
  says less, and the nearest of a partial reading is not the nearest.
- A file with no `import proto` declaration is `PC0001`. A compilation stops before binding only when
  none of its files imports anything; otherwise the file is still bound, against the others' imports,
  so the rest of what is wrong with it is reported too.
- ProtoCross does not define an independent package declaration. Message, enum, and field names come
  from protobuf descriptors.
- One file may import schemas whose descriptors contain multiple protobuf packages; each `extend`
  resolves its target message through the descriptor pool.

Open Questions:

- Should descriptor-set input exist in addition to direct `.proto` imports?
- Should ProtoCross ever be embedded directly in `.proto` files?

### 5.3 Compilation Unit

**Decided: a compilation is one or more source files compiled as one program, each generated into
files of its own, under one policy.**

A compilation unit consists of:

- One or more ProtoCross source files.
- The protobuf descriptors referenced by those files.
- Compiler options.
- Backend target configuration.

Normative Requirements:

- The sources are one program. A method may call, and a `test` may target, a method declared in any
  source of the compilation, and the sources are not ordered
  ([16.1](./§16-Methods.md#161-method-attachment)). The one exception is a test source's method,
  which a production source's may not call
  ([25.3.1](./§25-Testing%20and%20Conformance%20Vectors.md#2531-test-sources-and-the-two-builds)).
- Every source is generated into files of its own, named after it, as a compilation of that source
  alone would be.
- Two sources whose names would collide in anything generated from them are `PC2006`, and the
  compilation stops. Every generated name drops something of the source's name -- case where a file
  system ignores it, punctuation, a `T` placed before a name that does not start with a letter -- so
  the names compared are the source names reduced to their letters and digits, upper-cased, with that
  `T` in front where it would go. One source given twice is `PC2006` as well.
- Two more kinds of generated name are compared the same way, and a source that takes one is
  `PC2006`. A backend generates some files under a fixed name beside every source's, so no source
  may be named `ProtoCrossArithmetic`, `ProtoCrossTestSupport` or `protocross_runtime`, in any role.
  And a test source's files are generated beside every source's tests
  ([25.3.1](./§25-Testing%20and%20Conformance%20Vectors.md#2531-test-sources-and-the-two-builds)),
  which are named after their source followed by `.tests`, so a test source may not be named
  `pricing.tests` in a compilation that has a `pricing`. A production source may: its files are
  generated into the other directory.
- Every source compiles under one policy ([10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy)).
- Every source carries an identity of its own, a path or the name its caller gave an unsaved buffer
  ([22.2](./§22-IR%20and%20Compiler%20Architecture.md#222-typed-ir-requirements)).

Implementation Note:

- The command line compiles every source it is given as one compilation
  (`protocross pricing.pcross discounts.pcross`), as the `Compilation` API does. A project can now
  write down which sources it is made of ([5.4](#54-projects)), but the command line does not yet
  take one, so whoever runs it still lists them.

### 5.4 Projects

**Decided: a project file, `<name>.pcproj`, says which sources make up one compilation and which of
them hold its tests. It names the configuration file its policy comes from, and never states policy
itself.**

```xml
<?xml version="1.0" encoding="utf-8"?>
<ProtoCrossProject>
  <Config>../protocross.config.xml</Config>
  <Sources Include="src/**/*.pcross" Exclude="src/scratch/**" />
  <Tests Include="tests/**/*.pcross" />
  <ProtoPath>protos</ProtoPath>
</ProtoCrossProject>
```

What is compiled and what it means are two questions, and each has a file of its own: a project
answers the first and `protocross.config.xml` the second
([10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy)). A project that could state
`<Arithmetic>` itself would give policy two homes.

Normative Requirements:

- The root element is `<ProtoCrossProject>`, and it holds only these elements, each of them optional:
  - `<Sources>`, which may be repeated: sources a production build compiles.
  - `<Tests>`, which may be repeated: sources a test build compiles as well.
  - `<ProtoPath>`, which may be repeated: a directory imported schemas are searched for in, as an
    include path is ([5.2](#52-relationship-to-proto)). They keep the order they are written in.
  - `<Config>`, at most once: the configuration file the project's compilation runs under.
- Anything else is `PC2008`, and so is an attribute an element does not take. An attribute in a
  namespace, such as `xsi:schemaLocation`, belongs to another vocabulary and is left alone. There is no element
  naming `protoc`, because a project comes with a repository and a repository does not choose what
  the machine runs ([10.4.1](./§10-Numeric%20Semantics.md#1041-host-configuration)), and none stating
  policy.
- Paths and patterns are relative to the project file's directory, and `../` reaches above it. In a
  pattern, `../` may only come first. A path may be absolute. A pattern may not, because it is matched
  below a directory, and a full path in a project that is committed names a directory on one machine
  only.
- `Include` and `Exclude` list patterns separated by `;`. `*` matches within one directory and `**`
  across any number of them, either separator divides directories, and case is ignored exactly where
  the file system ignores it. Only `.pcross` files are taken from what a pattern matches, so
  `src/**` means every source under `src`. An `Exclude` applies to the element it is written on.
- A file several patterns of one group match is one source. `<Sources>` and `<Tests>` may name the
  same files, and neither has to exclude the other's: building and testing are two procedures, and a
  file in both takes part in both. A file only `<Tests>` names is a test source, and what each build
  compiles and generates is [25.3.1](./§25-Testing%20and%20Conformance%20Vectors.md#2531-test-sources-and-the-two-builds)'s.
- Each group's sources are ordered by their path below the project's directory, so one tree is one
  compilation on every machine, whatever order a file system lists a directory in.
- A project that states anything it cannot mean is refused whole, because a project missing one of
  its lines would compile a program nobody wrote: `PC2007` for a file that cannot be read, is not
  XML, declares a document type (as a configuration file may not either,
  [10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy)), or has another root, and for a
  directory a pattern searches that cannot be listed; `PC2009`
  for an element without its patterns, patterns written as text, text standing directly inside
  `<ProtoCrossProject>`, a full path used as a pattern, a pattern that cannot be matched (a `../`
  after its start), an empty or impossible path, or `<Config>` stated twice.
- An element that matches no source is `PC2010`, a warning at that element. It is almost always a
  typo, and the rest of the project still stands.
- One project is one compilation and one file. Several may share a directory, and each is a
  compilation of its own.

Implementation Note:

- A project can be read and its sources found, but neither the command line nor an editor compiles
  one yet.
