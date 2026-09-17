module BPlug.Workspace.Tests.SnapshotTests

open BPlug.Workspace
open Xunit

[<Fact>]
let ``fromCSharpSource copies using names into a Core snapshot`` () =
    let source =
        """
        using System.Data.Linq;
        using System.Drawing;

        public class C { }
        """

    let snapshot = Snapshots.fromCSharpSource "MixedLayers.cs" source

    Assert.Equal("MixedLayers.cs", snapshot.FilePath)
    Assert.Equal<string>(
        [ "System.Data.Linq"; "System.Drawing" ],
        snapshot.Imports |> List.map (fun i -> i.Name)
    )
