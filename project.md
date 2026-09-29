# Projects

This repo holds **three** independent products. They share a solution for convenience only.

| Product | What you ship / install | Needs a custom IDE plugin? |
|---|---|---|
| **BAnalyzer** | NuGet analyzer package (`analyzers/dotnet/cs` + `vb`) | No. Any host that loads Roslyn analyzers (Visual Studio, VS Code C# Dev Kit, `dotnet build`) picks it up from a project/package reference. |
| **BPlug for VS Code** | VS Code / Cursor extension under `src/BPlug.VSCode` | Yes — that extension. |
| **BPlug for Visual Studio** | VS 2022 VSIX under `src/BPlug.VisualStudio` | Yes — that VSIX. |

**BPlug projects must not reference BAnalyzer** (no project references, package references, or analyzer references). The two BPlug hosts share Core / Workspace / Server; they do not share code with BAnalyzer.

Local IDE steps for the two plugins: [local_testing.md](local_testing.md).

## BAnalyzer — compiler diagnostics

Roslyn `DiagnosticAnalyzer` / code-fix package loaded by `csc` / `vbc` and the IDE language service. Install it on a C# or VB project to get squiggles and light-bulb fixes at compile time. No BPlug extension is required.

| Project | Description |
|---|---|
| `src/BAnalyzer` | Analyzer and code-fix assembly (BA0001, BA0002). Packed under `analyzers/dotnet/cs` and `analyzers/dotnet/vb`. |
| `tests/BAnalyzer.Tests` | In-memory C# and VB compilation tests for those analyzers. |
| `samples/BAnalyzer.Sample` | C# console app that references BAnalyzer as an analyzer. |
| `samples/BAnalyzer.Sample.VB` | VB console app that does the same. |

## BPlug — editor plugins (not an analyzer)

On-demand analysis for VS Code and Visual Studio. Each editor is its own product/installable; both talk to the same server. The server uses Roslyn only through Workspace; rules live in Core (Fable-safe, no Roslyn types). First rules compare **loaded project settings**: mixed TFM versions in the same family (BP0001), Nullable (BP0002), and ImplicitUsings (BP0003).

```
VS Code / Visual Studio  →  BPlug.Server  →  BPlug.Workspace (project XML / Roslyn)  →  snapshot  →  BPlug.Core (rules)
```

| Project | Description |
|---|---|
| `src/BPlug.Core` | Shared models and rules. Fable-safe: no Roslyn, no IDE APIs. Referenced by Server, Workspace, and the VS Code Fable project. |
| `src/BPlug.Workspace` | Reads `.csproj` / `.fsproj` / `.vbproj` (and `Directory.Build.props`) plus Roslyn document snapshots into Core types. The only BPlug project that references `Microsoft.CodeAnalysis`. |
| `src/BPlug.Server` | Process the extensions start. `--analyze-solution <path>` or `--analyze-projects [<sln>] <csproj>...` runs the project-setting rules. |
| `src/BPlug.VSCode` | **Product:** Visual Studio Code / Cursor extension (Fable entry + `package.json`). Starts Server; does not load Roslyn in the extension host. |
| `src/BPlug.VisualStudio` | **Product:** Visual Studio 2022 VSIX. **Tools → Analyze Loaded Projects (BPlug)** enumerates open MSBuild projects, runs Server, and writes BP0001–BP0003 to the Error List. Also runs when a solution opens. |
| `tests/BPlug.Core.Tests` | Tests against Core types and rules with hand-built snapshots. |
| `tests/BPlug.Workspace.Tests` | Tests that Roslyn parse output becomes Core snapshots. |
