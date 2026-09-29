module BPlug.VSCode.Extension

open System
open Fable.Core
open Fable.Core.JsInterop

/// Fable emits JS for the VS Code extension host. Do not reference Workspace/Roslyn here.
let private vscode: obj = importAll "vscode"
let private nodePath: obj = importAll "path"
let private nodeFs: obj = importAll "fs"
let private childProcess: obj = importAll "child_process"

let private outputChannel: obj = vscode?window?createOutputChannel ("BPlug")
let private diagnostics: obj = vscode?languages?createDiagnosticCollection ("bplug")

let private log (message: string) =
    outputChannel?appendLine (message)

let private locateServer (extensionPath: string) =
    let exe = nodePath?join (extensionPath, "server", "BPlug.Server.exe") |> string
    let dll = nodePath?join (extensionPath, "server", "BPlug.Server.dll") |> string
    if nodeFs?existsSync (exe) then Some(exe, false)
    elif nodeFs?existsSync (dll) then Some(dll, true)
    else None

let private parseFindings (stdout: string) =
    stdout.Split('\n')
    |> Array.choose (fun raw ->
        let line = raw.TrimEnd('\r')
        if line.StartsWith("FINDING\t", StringComparison.Ordinal) then
            let parts = line.Substring(8).Split([| '\t' |], 3)
            if parts.Length = 3 then Some(parts[0], parts[1], parts[2]) else None
        else
            None
    )

let private range0: obj =
    emitJsExpr (vscode) "new $0.Range(0, 0, 0, 0)"

let private showFindings (findings: (string * string * string)[]) =
    diagnostics?clear ()
    findings
    |> Array.groupBy (fun (_, filePath, _) -> filePath)
    |> Array.iter (fun (filePath, items) ->
        let uri = vscode?Uri?file (filePath)
        let diags =
            items
            |> Array.map (fun (id, _, message) ->
                let diagnostic: obj =
                    emitJsExpr (vscode, range0, message) "new $0.Diagnostic($1, $2, $0.DiagnosticSeverity.Warning)"
                diagnostic?source <- "BPlug"
                diagnostic?code <- id
                diagnostic
            )
        diagnostics?set (uri, diags)
    )

let private runServer (fileName: string) (args: string[]) =
    let options = createObj [ "encoding" ==> "utf8"; "windowsHide" ==> true ]
    let result: obj = childProcess?spawnSync (fileName, args, options)
    let stdout = if isNull result?stdout then "" else string result?stdout
    let stderr = if isNull result?stderr then "" else string result?stderr
    let status =
        if isNull result?status then -1 else int result?status
    stdout, stderr, status

let private workspacePath () =
    let folders: obj = vscode?workspace?workspaceFolders
    if isNull folders || unbox<int> folders?length = 0 then
        None
    else
        let folder: obj = emitJsExpr (folders) "$0[0]"
        Some(string folder?uri?fsPath)

let private analyze (extensionPath: string) =
    match workspacePath () with
    | None ->
        vscode?window?showWarningMessage ("Open a folder that contains a .sln, .slnx, or .csproj first.")
        |> ignore
    | Some root ->
        outputChannel?show (true)
        log ("Analyzing " + root)
        match locateServer extensionPath with
        | None ->
            log "BPlug.Server was not found under server/. Build BPlug.Server, then rebuild BPlug.VSCode."
            vscode?window?showErrorMessage ("BPlug.Server was not found. Build BPlug.VSCode so it can copy the server.")
            |> ignore
        | Some(serverPath, useDotnet) ->
            let fileName, args =
                if useDotnet then
                    "dotnet", [| serverPath; "--analyze-solution"; root |]
                else
                    serverPath, [| "--analyze-solution"; root |]

            let stdout, stderr, status = runServer fileName args
            if stdout <> "" then log stdout
            if stderr <> "" then log stderr
            let findings = parseFindings stdout
            showFindings findings
            if findings.Length = 0 then
                log "No BPlug findings."
            else
                log (string findings.Length + " BPlug finding(s) sent to Problems.")
            if status > 1 then
                vscode?window?showErrorMessage ("BPlug.Server exited with code " + string status)
                |> ignore

let activate (context: obj) =
    let extensionPath = string context?extensionPath
    let runAnalyze () = analyze extensionPath
    let command: obj = vscode?commands?registerCommand ("bplug.analyze", runAnalyze)
    context?subscriptions?push (command)
    context?subscriptions?push (diagnostics)
    context?subscriptions?push (outputChannel)
    runAnalyze ()

let deactivate () = ()
