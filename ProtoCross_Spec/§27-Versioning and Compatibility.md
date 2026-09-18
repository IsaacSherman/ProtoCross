## 27. Versioning and Compatibility

### 27.1 Language Versioning

No language-version declaration is implemented today.

Possible future syntax:

```protocross
language "1.0";
```

Open Questions:

- Is the version declaration required?
- Can a compilation unit mix language versions?

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

Open Question:

- Is ProtoCross intended to preserve generated API compatibility across compiler versions?
