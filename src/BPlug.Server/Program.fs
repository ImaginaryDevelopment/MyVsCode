module BPlug.Server.Program

open System
open BPlug

let private writeFindings (findings: Model.Finding list) =
    if findings.IsEmpty then
        stdout.WriteLine("No BPlug findings.")
        0
    else
        for finding in findings do
            stdout.WriteLine(sprintf "FINDING\t%s\t%s\t%s" finding.Id finding.FilePath finding.Message)

        1

let private analyzeSolution path =
    Workspace.Projects.fromPath path
    |> Rules.analyzeSolution
    |> writeFindings

let private analyzeProjects (argvRest: string list) =
    match argvRest with
    | [] ->
        stderr.WriteLine("BPlug.Server: --analyze-projects requires at least one path.")
        2
    | first :: rest when
        first.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
        || first.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ->
        Workspace.Projects.fromProjectFiles first rest
        |> Rules.analyzeSolution
        |> writeFindings
    | paths ->
        Workspace.Projects.fromProjectFiles "loaded-projects" paths
        |> Rules.analyzeSolution
        |> writeFindings

[<EntryPoint>]
let main argv =
    match argv |> Array.toList with
    | "--analyze-solution" :: [ path ] -> analyzeSolution path
    | "--analyze-projects" :: paths -> analyzeProjects paths
    | _ ->
        stdout.WriteLine("BPlug.Server ready")
        stdout.WriteLine("Usage:")
        stdout.WriteLine("  BPlug.Server --analyze-solution <sln|slnx|csproj|directory>")
        stdout.WriteLine("  BPlug.Server --analyze-projects [<sln>] <csproj> [<csproj> ...]")
        0
