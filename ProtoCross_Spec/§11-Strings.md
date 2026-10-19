## 11. Strings

### 11.1 String Model

Strings are Unicode text corresponding to protobuf `string`. The current language supports string
literals, assignment to string-typed locals, return values, parameters, field reads, and equality
or inequality against another string value of the same type.

Normative Requirements:

- No locale-sensitive operations are available.
- Ordered string comparison is not supported.
- String indexing, length, case conversion, and normalization operations are not implemented.
- **`==` and `!=` compare two strings by their Unicode scalar values**, one by one. For a well-formed
  string that is what C#'s ordinal comparison and C++'s comparison of UTF-8 bytes give already, so
  each backend writes its own `==`. A string protobuf read from bytes that are not UTF-8 compares as
  its runtime decoded it.
- **Strings are compared as they are stored, and never normalized.** `é` written as one code point,
  and as `e` followed by a combining accent, are two different strings.

Open Questions:

- [String indexing](./§30-Unresolved%20Questions%20Index.md#30111-string-indexing), post-1.0
- [Ordered string comparison](./§30-Unresolved%20Questions%20Index.md#30114-ordered-string-comparison), post-1.0
