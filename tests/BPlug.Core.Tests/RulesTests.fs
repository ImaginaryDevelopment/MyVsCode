module BPlug.Core.Tests.RulesTests

open BPlug
open BPlug.Model
open Xunit

let private project name monikers nullable implicitUsings =
    {
        FilePath = name + ".csproj"
        Name = name
        TargetFrameworks = monikers |> List.map TargetFrameworks.parse
        Nullable = nullable
        ImplicitUsings = implicitUsings
    }

let private solution projects =
    { FilePath = "App.sln"; Projects = projects }

let private ids findings = findings |> List.map (fun f -> f.Id)

[<Fact>]
let ``BP0001 is reported when Framework versions differ`` () =
    let findings =
        solution
            [
                project "Legacy" [ "net45" ] NullableUnspecified ImplicitUsingsUnspecified
                project "Newer" [ "net46" ] NullableUnspecified ImplicitUsingsUnspecified
            ]
        |> Rules.analyzeSolution

    Assert.Contains(Rules.Ids.TargetFrameworkSkew, ids findings)

[<Fact>]
let ``BP0001 is not reported for netstandard next to net8`` () =
    let findings =
        solution
            [
                project "Lib" [ "netstandard2.0" ] NullableUnspecified ImplicitUsingsUnspecified
                project "App" [ "net8.0" ] NullableUnspecified ImplicitUsingsUnspecified
            ]
        |> Rules.analyzeSolution

    Assert.DoesNotContain(Rules.Ids.TargetFrameworkSkew, ids findings)

[<Fact>]
let ``BP0002 is reported when Nullable disagrees`` () =
    let findings =
        solution
            [
                project "A" [ "net8.0" ] NullableEnable ImplicitUsingsUnspecified
                project "B" [ "net8.0" ] NullableDisable ImplicitUsingsUnspecified
            ]
        |> Rules.analyzeSolution

    Assert.Contains(Rules.Ids.NullableDisagreement, ids findings)

[<Fact>]
let ``BP0003 is reported when ImplicitUsings disagrees`` () =
    let findings =
        solution
            [
                project "A" [ "net8.0" ] NullableEnable ImplicitUsingsEnable
                project "B" [ "net8.0" ] NullableEnable ImplicitUsingsDisable
            ]
        |> Rules.analyzeSolution

    Assert.Contains(Rules.Ids.ImplicitUsingsDisagreement, ids findings)

[<Fact>]
let ``no findings when project settings match`` () =
    let findings =
        solution
            [
                project "A" [ "net8.0" ] NullableEnable ImplicitUsingsEnable
                project "B" [ "net8.0" ] NullableEnable ImplicitUsingsEnable
            ]
        |> Rules.analyzeSolution

    Assert.Empty(findings)
