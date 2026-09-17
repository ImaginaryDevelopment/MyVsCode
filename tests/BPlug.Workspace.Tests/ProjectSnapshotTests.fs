module BPlug.Workspace.Tests.ProjectSnapshotTests

open System.IO
open BPlug
open BPlug.Model
open Xunit

let private write (dir: string) relative contents =
    let path = Path.Combine(dir, relative)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, contents)
    path

let private sdkProject tfm extras =
    """
    <Project Sdk="Microsoft.NET.Sdk">
      <PropertyGroup>
        <TargetFramework>"""
    + tfm
    + """</TargetFramework>
        """
    + extras
    + """
      </PropertyGroup>
    </Project>
    """

[<Fact>]
let ``fromProjectFile reads TFM nullable and implicit usings`` () =
    let dir = Path.Combine(Path.GetTempPath(), "BPlug-" + Path.GetRandomFileName())
    Directory.CreateDirectory dir |> ignore

    try
        let path =
            write dir "App.csproj" (sdkProject "net8.0" "<Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>")

        let snapshot = Workspace.Projects.fromProjectFile path

        Assert.Equal("net8.0", snapshot.TargetFrameworks.Head.Moniker)
        Assert.Equal(TfFamily.Net, snapshot.TargetFrameworks.Head.Family)
        Assert.Equal(NullableEnable, snapshot.Nullable)
        Assert.Equal(ImplicitUsingsEnable, snapshot.ImplicitUsings)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``fromPath reports mixed Framework TFMs across projects`` () =
    let dir = Path.Combine(Path.GetTempPath(), "BPlug-" + Path.GetRandomFileName())
    Directory.CreateDirectory dir |> ignore

    try
        write dir "Old.csproj" (sdkProject "net45" "") |> ignore
        write dir "New.csproj" (sdkProject "net46" "") |> ignore

        let findings =
            Workspace.Projects.fromPath dir
            |> Rules.analyzeSolution

        Assert.Contains(findings, fun f -> f.Id = Rules.Ids.TargetFrameworkSkew)
    finally
        Directory.Delete(dir, true)
