module BPlug.Core.Tests.ModelTests

open BPlug.Model
open Xunit

[<Fact>]
let ``document snapshot can describe imports without Roslyn types`` () =
    let snapshot =
        {
            FilePath = "Sample.cs"
            Imports =
                [
                    { Name = "System.Data.Linq"; Span = { Start = 0; Length = 22 } }
                    { Name = "System.Drawing"; Span = { Start = 30; Length = 14 } }
                ]
            MemberAccesses = []
        }

    Assert.Equal(2, snapshot.Imports.Length)
    Assert.Equal("System.Drawing", snapshot.Imports.[1].Name)
