module BAnalyzer.Tests.AnalyzerTestHost

open System
open System.Collections.Immutable
open System.IO
open System.Threading
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.Diagnostics
open Microsoft.CodeAnalysis.VisualBasic

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

let getCSharpDiagnostics (analyzer: DiagnosticAnalyzer) (source: string) =
    let tree = CSharpSyntaxTree.ParseText(source)
    let compilation =
        CSharpCompilation.Create(
            "AnalyzerTest",
            [| tree |],
            metadataReferences (),
            CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        )

    compilation
        .WithAnalyzers(ImmutableArray.Create(analyzer))
        .GetAnalyzerDiagnosticsAsync(CancellationToken.None)
        .GetAwaiter()
        .GetResult()

let getVisualBasicDiagnostics (analyzer: DiagnosticAnalyzer) (source: string) =
    let tree = VisualBasicSyntaxTree.ParseText(source)
    let compilation =
        VisualBasicCompilation.Create(
            "AnalyzerTest",
            [| tree |],
            metadataReferences (),
            VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        )

    compilation
        .WithAnalyzers(ImmutableArray.Create(analyzer))
        .GetAnalyzerDiagnosticsAsync(CancellationToken.None)
        .GetAwaiter()
        .GetResult()
