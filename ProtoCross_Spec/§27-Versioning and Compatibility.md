## 27. Versioning and Compatibility

### 27.1 Language Versioning

No language-version declaration is implemented today.

Possible future syntax:

```protocross
language "1.0";
```

Open Question:

- [Language version](./§30-Unresolved%20Questions%20Index.md#30271-language-version)

### 27.2 Compatibility Policy

The project should define:

- Source compatibility rules.
- IR compatibility rules.
- Backend compatibility rules.
- Generated API compatibility rules.
- Runtime behavior compatibility rules.

Candidate policy:

- Patch versions may fix bugs and add diagnostics.
- Minor versions may add backward-compatible syntax or features.
- Major versions may change semantics.

Open Questions:

- [The compatibility policy](./§30-Unresolved%20Questions%20Index.md#30272-the-compatibility-policy)
- [Generated API compatibility](./§30-Unresolved%20Questions%20Index.md#30273-generated-api-compatibility)
