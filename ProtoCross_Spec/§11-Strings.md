## 11. Strings

### 11.1 String Model

Strings are Unicode text corresponding to protobuf `string`. The current language supports string
literals, assignment to string-typed locals, return values, parameters, field reads, and equality
or inequality against another string value of the same type.

Normative Requirements:

- No locale-sensitive operations are available.
- Ordered string comparison is not supported.
- String indexing, length, case conversion, and normalization operations are not implemented.

Open Questions:

- [String indexing](./§30-Unresolved%20Questions%20Index.md#30111-string-indexing)
- [String normalization](./§30-Unresolved%20Questions%20Index.md#30112-string-normalization)
- [String equality](./§30-Unresolved%20Questions%20Index.md#30113-string-equality)
- [Ordered string comparison](./§30-Unresolved%20Questions%20Index.md#30114-ordered-string-comparison)
