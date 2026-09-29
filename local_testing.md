# Local testing — BPlug plugins

How to run the Visual Studio 2022 VSIX and the VS Code / Cursor extension against a real IDE.

Both hosts start `BPlug.Server`, which reports **BP0001** (mixed TFM versions in the same family), **BP0002** (Nullable disagreement), and **BP0003** (ImplicitUsings disagreement) for loaded projects.

Use a solution that has more than one `.csproj` / `.fsproj` / `.vbproj` with mixed settings if you want findings. Same-family projects on different versions (for example `net8.0` + `net10.0`) should produce BP0001.

---

## Visual Studio 2022

Requires the **Visual Studio extension development** workload. The VSIX targets VS 2022 (`[17.0,18.0)`), not VS 2026.

### Debug (F5)

1. Open `BAnalyzer.slnx` in Visual Studio 2022.
2. Set **BPlug.VisualStudio** as the startup project.
3. In the debug dropdown, choose **Visual Studio Experimental Instance** (not a plain “Project” profile — SDK-style VSIX projects are class libraries and cannot start that way).
4. Press **F5**. That builds `BPlug.Server`, deploys into the **Experimental** hive (`/rootsuffix Exp`), and opens a second VS window titled with **(Experimental Instance)**.
5. In the Experimental Instance, open a second solution with multiple MSBuild projects.
6. Analysis runs when the solution opens, and again from **Tools → Analyze Loaded Projects (BPlug)**.
7. Check **Error List** for BP0001–BP0003 and **View → Output → BPlug**.

If you see *BPlug.Server.exe was not found next to the VSIX*, rebuild `BPlug.VisualStudio` so the Server binaries are copied under the extension output `Server\` folder.

Do **not** enable solution-level **Deploy** for this project on VS 2022. That path tried to install the project folder into the host IDE and failed with access denied. Deployment for F5 uses the Experimental hive via `DeployExtension` / `StartArguments`.

### Install without F5

Build `src/BPlug.VisualStudio/BPlug.VisualStudio.csproj`, then double-click the produced `.vsix` under `bin\Debug\` (or use **Extensions → Manage Extensions → Install from VSIX**). Same Tools command and Error List as above.

### Server-only smoke test (no IDE)

```powershell
dotnet run --project src/BPlug.Server -- --analyze-projects Your.sln path\to\a.csproj path\to\b.csproj
```

Or:

```powershell
dotnet run --project src/BPlug.Server -- --analyze-solution path\to\Your.sln
```

Findings print as `FINDING\t<id>\t<path>\t<message>`.

---

## VS Code / Cursor

The extension lives in `src/BPlug.VSCode`. Fable compiles `Extension.fs` to `out/Extension.js`. A build copies `BPlug.Server` into `src/BPlug.VSCode/server/`.

### One-time setup

From the repo root:

```powershell
dotnet tool restore
npm install --prefix src/BPlug.VSCode
```

### Debug (F5)

1. Open this repo in Cursor or VS Code.
2. Run and Debug → **BPlug VS Code Extension** → **F5**.
3. The preLaunch task builds the project and runs Fable into `src/BPlug.VSCode/out`, then opens an **Extension Development Host**.
4. In that window, open a folder that contains a `.sln`, `.slnx`, or project files (or keep this repo).
5. Analysis runs on activate. Run again from the Command Palette: **BPlug: Analyze Workspace Projects**.
6. Check **Problems** and **Output → BPlug**.

If Output says the server was not found under `server/`, rebuild `src/BPlug.VSCode/BPlug.VSCode.fsproj` so the copy target runs.

### Manual rebuild (without F5)

```powershell
dotnet build src/BPlug.VSCode/BPlug.VSCode.fsproj -nologo
dotnet tool run fable src/BPlug.VSCode -o src/BPlug.VSCode/out
```

Then use **Developer: Reload Window** in an Extension Development Host that already has `--extensionDevelopmentPath` pointed at `src/BPlug.VSCode`, or F5 again.

---

## Unit tests (rules / workspace, not the IDE UI)

```powershell
dotnet test BAnalyzer.slnx --nologo --filter FullyQualifiedName~BPlug
```

These cover Core and Workspace. They do not load the VSIX or the VS Code extension host.
