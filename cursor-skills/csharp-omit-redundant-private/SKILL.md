---
name: csharp-omit-redundant-private
description: >-
  Omits the redundant `private` keyword on C# members that are already private
  by default. Use when writing or editing C# (.cs) code, reviewing C# diffs, or
  applying C# style conventions.
---

# C# — omit redundant `private`

Anything that is implicitly private doesn't need to write the `private` keyword as it is redundant.

## Rule

Do **not** write `private` when the member would be private without an access modifier.

In C#, these are private by default (omit `private`):

- Class / struct instance and static **fields**, **methods**, **properties**, **events**, **indexers**, **constructors**
- **Nested** types (nested class, struct, enum, delegate, interface)

Still write an access modifier when it is **not** the default, e.g. `public`, `internal`, `protected`, `protected internal`, `private protected`.

Top-level types without a modifier are `internal` (not private) — do not treat them as implicitly private.

## Examples

```csharp
// Prefer
sealed class Example
{
    readonly BillingSqlExecutor _sql;

    Example(BillingSqlExecutor sql) => _sql = sql;

    static string VbStr(long value) => value.ToString();

    sealed class Nested { }
}

// Avoid — private is redundant
sealed class Example
{
    private readonly BillingSqlExecutor _sql;

    private Example(BillingSqlExecutor sql) => _sql = sql;

    private static string VbStr(long value) => value.ToString();

    private sealed class Nested { }
}
```

## When editing existing code

- Prefer this style for **new** members and files you author.
- Do not drive-by strip `private` from unrelated existing code unless the user asks for a style cleanup or you are already rewriting that member.
