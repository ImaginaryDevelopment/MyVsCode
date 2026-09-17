# Projects

This repo holds two independent products. They share a solution for convenience only. **BPlug projects must not reference BAnalyzer** (no project references, package references, or analyzer references).

## BAnalyzer — compiler diagnostics

Roslyn `DiagnosticAnalyzer` / code-fix package loaded by `csc` / `vbc` and the IDE. Install it on a C# or VB project to get squiggles and light-bulb fixes at compile time.

| Project | Description |
|---|---|
| `src/BAnalyzer` | Analyzer and code-fix assembly (BA0001, BA0002). Packed under `analyzers/dotnet/cs` and `analyzers/dotnet/vb`. |
| `tests/BAnalyzer.Tests` | In-memory C# and VB compilation tests for those analyzers. |
| `samples/BAnalyzer.Sample` | C# console app that references BAnalyzer as an analyzer. |
| `samples/BAnalyzer.Sample.VB` | VB console app that does the same. |

## BPlug — editor plugins (not an analyzer)

On-demand analysis for VS Code and Visual Studio. Editors talk to a server; the server uses Roslyn only through Workspace; rules live in Core (Fable-safe, no Roslyn types).

```
VS Code / Visual Studio  →  BPlug.Server  →  BPlug.Workspace (Roslyn)  →  snapshot  →  BPlug.Core (rules)
```

| Project | Description |
|---|---|
| `src/BPlug.Core` | Shared models and (later) rules. Fable-safe: no Roslyn, no IDE APIs. Referenced by Server, Workspace, and the VS Code Fable project. |
| `src/BPlug.Workspace` | Translates Roslyn syntax/semantics into Core snapshots. The only BPlug project that references `Microsoft.CodeAnalysis`. |
| `src/BPlug.Server` | Process the extensions start. Calls Workspace, then Core, replies over stdio. |
| `src/BPlug.VSCode` | Visual Studio Code extension (Fable entry + `package.json`). Starts Server; does not load Roslyn in the extension host. |
| `src/BPlug.VisualStudio` | Visual Studio 2022 VSIX. Starts Server; shows findings in VS. Does not embed Roslyn in the VSIX. |
| `tests/BPlug.Core.Tests` | Tests against Core types and rules with hand-built snapshots. |
| `tests/BPlug.Workspace.Tests` | Tests that Roslyn parse output becomes Core snapshots. |
