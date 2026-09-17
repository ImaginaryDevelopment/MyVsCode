module BAnalyzer.Tests.DisparateImportAnalyzerTests

open BAnalyzer
open System
open System.Collections.Immutable
open System.IO
open System.Threading
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.Diagnostics
open Xunit

let private metadataReferences () =
    let runtime = Path.Combine(Path.GetDirectoryName(typeof<obj>.Assembly.Location), "System.Runtime.dll")

    [ typeof<obj>.Assembly.Location; runtime ]
    |> List.filter File.Exists
    |> List.distinct
    |> List.map (fun path -> MetadataReference.CreateFromFile(path) :> MetadataReference)
    |> Array.ofList

let private getDiagnostics (source: string) =
    let tree = CSharpSyntaxTree.ParseText(source)
    let compilation =
        CSharpCompilation.Create(
            "AnalyzerTest",
            [| tree |],
            metadataReferences (),
            CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        )

    let analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(DisparateImportAnalyzer())
    compilation
        .WithAnalyzers(analyzers)
        .GetAnalyzerDiagnosticsAsync(CancellationToken.None)
        .GetAwaiter()
        .GetResult()

[<Fact>]
let ``BA0002 is reported for System.Data.Linq and System.Drawing`` () =
    let source =
        """
        using System.Data.Linq;
        using System.Drawing;

        public class C { }
        """

    let diagnostics = getDiagnostics source
    Assert.Contains(diagnostics, fun d -> d.Id = Rules.DisparateImports.Id)
    Assert.Contains(diagnostics, fun d -> d.GetMessage().Contains("System.Data.Linq") && d.GetMessage().Contains("System.Drawing"))

[<Fact>]
let ``BA0002 is not reported for two data-access imports`` () =
    let source =
        """
        using System.Data;
        using System.Data.Linq;

        public class C { }
        """

    let diagnostics = getDiagnostics source
    Assert.DoesNotContain(diagnostics, fun d -> d.Id = Rules.DisparateImports.Id)

[<Fact>]
let ``BA0002 is not reported for a single layer-specific import`` () =
    let source =
        """
        using System.Data.Linq;

        public class C { }
        """

    let diagnostics = getDiagnostics source
    Assert.DoesNotContain(diagnostics, fun d -> d.Id = Rules.DisparateImports.Id)

[<Fact>]
let ``BA0002 is not reported for ordinary BCL imports`` () =
    let source =
        """
        using System;
        using System.Collections.Generic;
        using System.Linq;

        public class C { }
        """

    let diagnostics = getDiagnostics source
    Assert.DoesNotContain(diagnostics, fun d -> d.Id = Rules.DisparateImports.Id)

[<Fact>]
let ``BA0002 is reported for presentation and web imports`` () =
    let source =
        """
        using System.Windows.Forms;
        using Microsoft.AspNetCore.Mvc;

        public class C { }
        """

    let diagnostics = getDiagnostics source
    Assert.Contains(diagnostics, fun d -> d.Id = Rules.DisparateImports.Id)
