module BPlug.Workspace.Projects

open System
open System.Collections.Generic
open System.IO
open System.Xml.Linq
open BPlug
open BPlug.Model

let private projectExtensions = [| ".csproj"; ".fsproj"; ".vbproj" |]

let private isProjectFile (path: string) =
    projectExtensions
    |> Array.exists (fun ext -> path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))

let private isUnderBinOrObj (path: string) =
    path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
    |> Array.exists (fun segment ->
        segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
    )

let private tryLoadXml (path: string) =
    try
        XDocument.Load(path).Root |> Option.ofObj
    with _ ->
        None

let private hasCondition (element: XElement) =
    let attr = element.Attribute(XName.Get("Condition"))
    not (isNull attr) && not (String.IsNullOrWhiteSpace attr.Value)

let private applyProperties (properties: Dictionary<string, string>) (root: XElement) =
    root.Descendants()
    |> Seq.filter (fun element -> element.Name.LocalName = "PropertyGroup" && not (hasCondition element))
    |> Seq.collect (fun group -> group.Elements())
    |> Seq.filter (fun element -> not (hasCondition element) && not (String.IsNullOrWhiteSpace element.Value))
    |> Seq.iter (fun element -> properties.[element.Name.LocalName] <- element.Value.Trim())

let private ancestorDirectories (startDir: string) =
    let rec loop (dir: string) acc =
        if String.IsNullOrEmpty dir then
            acc
        else
            loop (Path.GetDirectoryName dir) (dir :: acc)

    loop startDir []

let private loadProperties (projectPath: string) =
    let properties = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    let projectDir = Path.GetDirectoryName projectPath

    for dir in ancestorDirectories projectDir do
        let propsPath = Path.Combine(dir, "Directory.Build.props")

        if File.Exists propsPath then
            match tryLoadXml propsPath with
            | Some root -> applyProperties properties root
            | None -> ()

    match tryLoadXml projectPath with
    | Some root -> applyProperties properties root
    | None -> ()

    properties

let private tryGet (properties: Dictionary<string, string>) name =
    match properties.TryGetValue name with
    | true, value when not (String.IsNullOrWhiteSpace value) -> Some value
    | _ -> None

let private monikersFrom (properties: Dictionary<string, string>) =
    match tryGet properties "TargetFrameworks" with
    | Some value ->
        value.Split([| ';'; ',' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun item -> item.Trim())
        |> Array.filter (fun item -> item <> "")
        |> Array.toList
    | None ->
        match tryGet properties "TargetFramework" with
        | Some value -> [ value ]
        | None ->
            match tryGet properties "TargetFrameworkVersion" with
            | Some value -> [ value ]
            | None -> []

let fromProjectFile (projectPath: string) : ProjectSnapshot =
    let fullPath = Path.GetFullPath projectPath
    let properties = loadProperties fullPath

    let nullable =
        tryGet properties "Nullable"
        |> Option.map TargetFrameworks.parseNullable
        |> Option.defaultValue NullableUnspecified

    let implicitUsings =
        tryGet properties "ImplicitUsings"
        |> Option.map TargetFrameworks.parseImplicitUsings
        |> Option.defaultValue ImplicitUsingsUnspecified

    {
        FilePath = fullPath
        Name = Path.GetFileNameWithoutExtension fullPath
        TargetFrameworks = monikersFrom properties |> List.map TargetFrameworks.parse
        Nullable = nullable
        ImplicitUsings = implicitUsings
    }

let private projectPathsFromSolution (solutionPath: string) =
    let solutionDir = Path.GetDirectoryName solutionPath

    File.ReadAllLines solutionPath
    |> Array.choose (fun line ->
        let trimmed = line.Trim()

        if not (trimmed.StartsWith("Project(", StringComparison.Ordinal)) then
            None
        else
            let parts = trimmed.Split(',')

            if parts.Length < 2 then
                None
            else
                let relative =
                    parts.[1].Trim().Trim('"').Replace('\\', Path.DirectorySeparatorChar)

                if isProjectFile relative then
                    Some(Path.GetFullPath(Path.Combine(solutionDir, relative)))
                else
                    None
    )
    |> Array.distinct
    |> Array.filter File.Exists
    |> Array.toList

let private projectPathsFromDirectory (directory: string) =
    Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
    |> Seq.filter (fun path -> isProjectFile path && not (isUnderBinOrObj path))
    |> Seq.map Path.GetFullPath
    |> Seq.distinct
    |> Seq.toList

let fromPath (path: string) : SolutionSnapshot =
    let fullPath = Path.GetFullPath path

    let solutionPath, projectPaths =
        if File.Exists fullPath && fullPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) then
            fullPath, projectPathsFromSolution fullPath
        elif File.Exists fullPath && isProjectFile fullPath then
            fullPath, [ fullPath ]
        elif Directory.Exists fullPath then
            fullPath, projectPathsFromDirectory fullPath
        elif File.Exists fullPath && fullPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) then
            fullPath, projectPathsFromDirectory (Path.GetDirectoryName fullPath)
        else
            invalidArg "path" (sprintf "No solution, project, or directory at '%s'." path)

    {
        FilePath = solutionPath
        Projects = projectPaths |> List.map fromProjectFile
    }

let fromProjectFiles (solutionLabel: string) (projectPaths: string list) : SolutionSnapshot =
    let projects =
        projectPaths
        |> List.map Path.GetFullPath
        |> List.distinct
        |> List.filter File.Exists
        |> List.map fromProjectFile

    {
        FilePath = solutionLabel
        Projects = projects
    }
