namespace BAnalyzer

open System
open System.Collections.Generic
open System.Collections.Immutable
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Diagnostics

module ApplicationLayers =
    type Layer =
        | DataAccess
        | Presentation
        | Web

    let displayName =
        function
        | DataAccess -> "data access"
        | Presentation -> "presentation"
        | Web -> "web"

    /// Longest prefix wins. Unlisted namespaces (BCL, shared libraries) are ignored.
    let private prefixes =
        [|
            "System.Data.Linq", DataAccess
            "System.Data", DataAccess
            "Microsoft.Data", DataAccess
            "Microsoft.EntityFrameworkCore", DataAccess
            "Dapper", DataAccess
            "NHibernate", DataAccess
            "System.Drawing", Presentation
            "System.Windows.Forms", Presentation
            "System.Windows.Media", Presentation
            "System.Windows", Presentation
            "Microsoft.UI", Presentation
            "System.Web", Web
            "Microsoft.AspNetCore", Web
            "Microsoft.Owin", Web
        |]
        |> Array.sortByDescending (fun (prefix, _) -> prefix.Length)

    let tryGetLayer (namespaceName: string) =
        prefixes
        |> Array.tryPick (fun (prefix, layer) ->
            if namespaceName = prefix
               || namespaceName.StartsWith(prefix + ".", StringComparison.Ordinal) then
                Some layer
            else
                None
        )

[<DiagnosticAnalyzer(LanguageNames.CSharp)>]
type DisparateImportAnalyzer() =
    inherit DiagnosticAnalyzer()

    override _.SupportedDiagnostics =
        ImmutableArray.Create(Rules.DisparateImports)

    override _.Initialize(context: AnalysisContext) =
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None)
        context.EnableConcurrentExecution()
        context.RegisterSyntaxNodeAction(
            DisparateImportAnalyzer.AnalyzeCompilationUnit,
            SyntaxKind.CompilationUnit
        )

    static member AnalyzeCompilationUnit(context: SyntaxNodeAnalysisContext) =
        match context.Node with
        | :? CompilationUnitSyntax as compilationUnit ->
            DisparateImportAnalyzer.AnalyzeUsings(context, compilationUnit)
        | _ -> ()

    static member AnalyzeUsings(context: SyntaxNodeAnalysisContext, compilationUnit: CompilationUnitSyntax) =
        let firstImportByLayer = Dictionary<ApplicationLayers.Layer, UsingDirectiveSyntax>()

        for node in compilationUnit.DescendantNodes() do
            match node with
            | :? UsingDirectiveSyntax as usingDirective when not (isNull usingDirective.Name) ->
                let namespaceName = usingDirective.Name.ToString()

                match ApplicationLayers.tryGetLayer namespaceName with
                | Some layer when not (firstImportByLayer.ContainsKey(layer)) ->
                    firstImportByLayer.Add(layer, usingDirective)
                | _ -> ()
            | _ -> ()

        if firstImportByLayer.Count >= 2 then
            let ordered =
                firstImportByLayer
                |> Seq.sortBy (fun pair -> pair.Value.SpanStart)
                |> Seq.toArray

            let first = ordered.[0]
            let second = ordered.[1]

            let diagnostic =
                Diagnostic.Create(
                    Rules.DisparateImports,
                    second.Value.GetLocation(),
                    first.Value.Name.ToString(),
                    ApplicationLayers.displayName first.Key,
                    second.Value.Name.ToString(),
                    ApplicationLayers.displayName second.Key
                )

            context.ReportDiagnostic(diagnostic)
