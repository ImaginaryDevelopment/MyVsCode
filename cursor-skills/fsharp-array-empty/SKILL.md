---
name: fsharp-array-empty
description: >-
  Uses Array.empty for a literal empty array in F#. Use when writing or editing
  F# (.fs, .fsx, .linq) code, reviewing F# diffs, or applying F# style
  conventions.
---

# F# — use `Array.empty` for a literal empty array

use Array.empty for a literal empty array.

## Rule

Prefer `Array.empty` (or `Array.empty<T>` when the element type is not inferred) over `[||]` for an empty array literal.

```fsharp
// Prefer
let xs = Array.empty
let ys : string[] = Array.empty

// Avoid
let xs = [||]
let ys : string[] = [||]
```

## When editing existing code

- Prefer this style for **new** code you author.
- Do not drive-by replace `[||]` in unrelated existing code unless the user asks for a style cleanup or you are already rewriting that expression.
