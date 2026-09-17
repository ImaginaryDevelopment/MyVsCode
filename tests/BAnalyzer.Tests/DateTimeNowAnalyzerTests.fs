module BAnalyzer.Tests.DateTimeNowAnalyzerTests

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
    let required =
        [
            typeof<obj>.Assembly.Location
            typeof<Console>.Assembly.Location
            typeof<DateTime>.Assembly.Location
            Path.Combine(Path.GetDirectoryName(typeof<obj>.Assembly.Location), "System.Runtime.dll")
        ]
        |> List.distinct

    required
    |> List.filter File.Exists
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

    let analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(DateTimeNowAnalyzer())
    compilation
        .WithAnalyzers(analyzers)
        .GetAnalyzerDiagnosticsAsync(CancellationToken.None)
        .GetAwaiter()
        .GetResult()

[<Fact>]
let ``BA0001 is reported for DateTime.Now`` () =
    let source =
        """
        using System;
        public class C
        {
            public DateTime M() => DateTime.Now;
        }
        """

    let diagnostics = getDiagnostics source
    Assert.Contains(diagnostics, fun d -> d.Id = Rules.PreferUtcNow.Id)

[<Fact>]
let ``BA0001 is not reported for DateTime.UtcNow`` () =
    let source =
        """
        using System;
        public class C
        {
            public DateTime M() => DateTime.UtcNow;
        }
        """

    let diagnostics = getDiagnostics source
    Assert.DoesNotContain(diagnostics, fun d -> d.Id = Rules.PreferUtcNow.Id)
