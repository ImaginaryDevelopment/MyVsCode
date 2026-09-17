module BPlug.Server.Program

open System
open BPlug

[<EntryPoint>]
let main argv =
    match argv |> Array.toList with
    | [ "--analyze-solution"; path ] ->
        let solution = Workspace.Projects.fromPath path
        let findings = Rules.analyzeSolution solution

        if findings.IsEmpty then
            stdout.WriteLine("No BPlug findings.")
            0
        else
            for finding in findings do
                stdout.WriteLine(sprintf "%s: %s" finding.Id finding.Message)

            1
    | _ ->
        stdout.WriteLine("BPlug.Server ready")
        stdout.WriteLine("Usage: BPlug.Server --analyze-solution <sln|slnx|csproj|directory>")
        0
