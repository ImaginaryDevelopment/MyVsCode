module BPlug.Rules

open System
open BReusable
open BPlug.Model

module Ids =
    let TargetFrameworkSkew = "BP0001"
    let NullableDisagreement = "BP0002"
    let ImplicitUsingsDisagreement = "BP0003"

let private emptySpan = { Start = 0; Length = 0 }

let private projectLabel (project: ProjectSnapshot) =
    if String.IsNullOrEmpty project.Name then
        project.FilePath
    else
        project.Name


let private finding id message filePath =
    {
        Id = id
        Message = message
        FilePath = filePath
        Span = emptySpan
    }

let private targetFrameworkFindings (solution: SolutionSnapshot) =
    solution.Projects
    |> List.collect (fun project ->
        project.TargetFrameworks |> List.map (fun tf -> project, tf)
    )
    |> List.groupBy (fun (_, tf) -> tf.Family)
    |> List.choose (fun (family, items) ->
        let versions =
            items
            |> List.map (fun (_, tf) -> tf.Version)
            |> List.distinct

        if versions.Length < 2 then
            None
        else
            let detail =
                items
                |> List.distinctBy (fun (project, tf) -> project.FilePath, tf.Moniker)
                |> List.map (fun (project, tf) -> sprintf "%s (%s)" tf.Moniker (projectLabel project))

            finding
                Ids.TargetFrameworkSkew
                (sprintf "Projects disagree on %s versions: %s" (TargetFrameworks.familyName family) (String.join detail))
                solution.FilePath
            |> Some
    )

let private settingFindings
    id
    label
    (modeText: 'mode -> string)
    (selector: ProjectSnapshot -> 'mode)
    (solution: SolutionSnapshot)
    =
    let modes =
        solution.Projects
        |> List.map selector
        |> List.distinct

    if solution.Projects.Length < 2 || modes.Length < 2 then
        []
    else
        let detail =
            solution.Projects
            |> List.map (fun project -> sprintf "%s (%s)" (projectLabel project) (modeText (selector project)))

        [
            finding
                id
                (sprintf "Projects disagree on %s: %s" label (String.join detail))
                solution.FilePath
        ]

let private nullableText =
    function
    | NullableUnspecified -> "unspecified"
    | NullableEnable -> "enable"
    | NullableDisable -> "disable"
    | NullableWarnings -> "warnings"

let private implicitUsingsText =
    function
    | ImplicitUsingsUnspecified -> "unspecified"
    | ImplicitUsingsEnable -> "enable"
    | ImplicitUsingsDisable -> "disable"

let analyzeSolution (solution: SolutionSnapshot) =
    [
        yield! targetFrameworkFindings solution
        yield!
            settingFindings Ids.NullableDisagreement "Nullable" nullableText (fun p -> p.Nullable) solution
        yield!
            settingFindings
                Ids.ImplicitUsingsDisagreement
                "ImplicitUsings"
                implicitUsingsText
                (fun p -> p.ImplicitUsings)
                solution
    ]
