namespace BAnalyzer

open System
open System.Collections.Generic
open System.Collections.Immutable
open Microsoft.CodeAnalysis
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

[<DiagnosticAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic)>]
type DisparateImportAnalyzer() =
    inherit DiagnosticAnalyzer()

    override _.SupportedDiagnostics =
        ImmutableArray.Create(Rules.DisparateImports)

    override _.Initialize(context: AnalysisContext) =
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None)
        context.EnableConcurrentExecution()
        context.RegisterSyntaxNodeAction(
            DisparateImportAnalyzer.AnalyzeCompilationUnit,
            Microsoft.CodeAnalysis.CSharp.SyntaxKind.CompilationUnit
        )
        context.RegisterSyntaxNodeAction(
            DisparateImportAnalyzer.AnalyzeCompilationUnit,
            Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.CompilationUnit
        )

    static member AnalyzeCompilationUnit(context: SyntaxNodeAnalysisContext) =
        let firstImportByLayer = Dictionary<ApplicationLayers.Layer, struct (string * Location * int)>()

        let consider (namespaceName: string) (location: Location) (spanStart: int) =
            match ApplicationLayers.tryGetLayer namespaceName with
            | Some layer when not (firstImportByLayer.ContainsKey(layer)) ->
                firstImportByLayer.Add(layer, struct (namespaceName, location, spanStart))
            | _ -> ()

        for node in context.Node.DescendantNodesAndSelf() do
            match node with
            | :? Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax as usingDirective when
                not (isNull usingDirective.Name) ->
                consider (usingDirective.Name.ToString()) (usingDirective.GetLocation()) usingDirective.SpanStart
            | :? Microsoft.CodeAnalysis.VisualBasic.Syntax.ImportsStatementSyntax as importsStatement ->
                for clause in importsStatement.ImportsClauses do
                    match clause with
                    | :? Microsoft.CodeAnalysis.VisualBasic.Syntax.SimpleImportsClauseSyntax as simple when
                        not (isNull simple.Name) ->
                        consider (simple.Name.ToString()) (simple.GetLocation()) simple.SpanStart
                    | _ -> ()
            | _ -> ()

        if firstImportByLayer.Count >= 2 then
            let ordered =
                firstImportByLayer
                |> Seq.sortBy (fun pair ->
                    let struct (_, _, spanStart) = pair.Value
                    spanStart
                )
                |> Seq.toArray

            let first = ordered.[0]
            let second = ordered.[1]
            let struct (firstName, _, _) = first.Value
            let struct (secondName, secondLocation, _) = second.Value

            let diagnostic =
                Diagnostic.Create(
                    Rules.DisparateImports,
                    secondLocation,
                    firstName,
                    ApplicationLayers.displayName first.Key,
                    secondName,
                    ApplicationLayers.displayName second.Key
                )

            context.ReportDiagnostic(diagnostic)
