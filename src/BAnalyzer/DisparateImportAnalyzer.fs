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

module internal DisparateImportAnalysis =
    let report
        (context: SyntaxNodeAnalysisContext)
        (imports: (string * Location * int) seq)
        =
        let firstImportByLayer = Dictionary<ApplicationLayers.Layer, struct (string * Location * int)>()

        for namespaceName, location, spanStart in imports do
            match ApplicationLayers.tryGetLayer namespaceName with
            | Some layer when not (firstImportByLayer.ContainsKey(layer)) ->
                firstImportByLayer.Add(layer, struct (namespaceName, location, spanStart))
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

            context.ReportDiagnostic(
                Diagnostic.Create(
                    Rules.DisparateImports,
                    secondLocation,
                    firstName,
                    ApplicationLayers.displayName first.Key,
                    secondName,
                    ApplicationLayers.displayName second.Key
                )
            )

// C# `using` and VB `Imports` are different syntax types, so each language
// needs its own DiagnosticAnalyzer. They cannot share one Initialize method:
// registering both SyntaxKinds in the same method body would JIT-load
// Microsoft.CodeAnalysis.CSharp into the VB compiler (and vice versa). vbc
// does not ship the C# assembly, and a combined analyzer throws
// FileNotFoundException (AD0001) instead of reporting BA0002.
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
            Microsoft.CodeAnalysis.CSharp.SyntaxKind.CompilationUnit
        )

    static member AnalyzeCompilationUnit(context: SyntaxNodeAnalysisContext) =
        if context.Compilation.Language <> LanguageNames.CSharp then
            ()
        else
            let imports =
                context.Node.DescendantNodesAndSelf()
                |> Seq.choose (fun node ->
                    match node with
                    | :? Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax as usingDirective when
                        not (isNull usingDirective.Name) ->
                        Some(usingDirective.Name.ToString(), usingDirective.GetLocation(), usingDirective.SpanStart)
                    | _ -> None
                )

            DisparateImportAnalysis.report context imports

[<DiagnosticAnalyzer(LanguageNames.VisualBasic)>]
type DisparateImportVisualBasicAnalyzer() =
    inherit DiagnosticAnalyzer()

    override _.SupportedDiagnostics =
        ImmutableArray.Create(Rules.DisparateImports)

    override _.Initialize(context: AnalysisContext) =
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None)
        context.EnableConcurrentExecution()
        context.RegisterSyntaxNodeAction(
            DisparateImportVisualBasicAnalyzer.AnalyzeCompilationUnit,
            Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.CompilationUnit
        )

    static member AnalyzeCompilationUnit(context: SyntaxNodeAnalysisContext) =
        if context.Compilation.Language <> LanguageNames.VisualBasic then
            ()
        else
            let imports =
                context.Node.DescendantNodesAndSelf()
                |> Seq.collect (fun node ->
                    match node with
                    | :? Microsoft.CodeAnalysis.VisualBasic.Syntax.ImportsStatementSyntax as importsStatement ->
                        importsStatement.ImportsClauses
                        |> Seq.choose (fun clause ->
                            match clause with
                            | :? Microsoft.CodeAnalysis.VisualBasic.Syntax.SimpleImportsClauseSyntax as simple when
                                not (isNull simple.Name) ->
                                Some(simple.Name.ToString(), simple.GetLocation(), simple.SpanStart)
                            | _ -> None
                        )
                    | _ -> Seq.empty
                )

            DisparateImportAnalysis.report context imports
