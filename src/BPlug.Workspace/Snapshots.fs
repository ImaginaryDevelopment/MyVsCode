module BPlug.Workspace.Snapshots

open BPlug.Model
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax

let private toSpan (span: Microsoft.CodeAnalysis.Text.TextSpan) : TextSpan =
    { Start = span.Start; Length = span.Length }

/// Roslyn reads C#; Core only sees the snapshot. IDE projects should not call this.
let fromCSharpSource (filePath: string) (source: string) : DocumentSnapshot =
    let tree = CSharpSyntaxTree.ParseText(source) :?> CSharpSyntaxTree
    let root = tree.GetCompilationUnitRoot()

    let imports =
        root.Usings
        |> Seq.choose (fun usingDirective ->
            if isNull usingDirective.Name then
                None
            else
                Some
                    {
                        Name = usingDirective.Name.ToString()
                        Span = toSpan usingDirective.Span
                    }
        )
        |> Seq.toList

    {
        FilePath = filePath
        Imports = imports
        MemberAccesses = []
    }
