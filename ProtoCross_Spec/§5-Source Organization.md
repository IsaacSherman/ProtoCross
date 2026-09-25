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
  the compilation ([5.3](#53-compilation-unit)).
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
  own copy may not exist. The warning names both files. Copies with the same contents are not
  reported, since nothing differs whichever is loaded. Resolution does not change: protoc knows a
  schema by its path under its root, so one compilation cannot load two schemas of one path, and the
  code protoc generates from two of them could not be built into one program either.
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
  ([16.1](./§16-Methods.md#161-method-attachment)).
- Every source is generated into files of its own, named after it, as a compilation of that source
  alone would be.
- Two sources whose names would collide in anything generated from them are `PC2006`, and the
  compilation stops. Every generated name drops something of the source's name -- case where a file
  system ignores it, punctuation, a `T` placed before a name that does not start with a letter -- so
  the names compared are the source names reduced to their letters and digits, upper-cased, with that
  `T` in front where it would go. One source given twice is `PC2006` as well.
- Every source compiles under one policy ([10.4](./§10-Numeric%20Semantics.md#104-compile-time-policy)).
- Every source carries an identity of its own, a path or the name its caller gave an unsaved buffer
  ([22.2](./§22-IR%20and%20Compiler%20Architecture.md#222-typed-ir-requirements)).

Implementation Note:

- The command line still compiles one source; the `Compilation` API compiles several. Which sources
  make up a project is not yet something a project can write down.
