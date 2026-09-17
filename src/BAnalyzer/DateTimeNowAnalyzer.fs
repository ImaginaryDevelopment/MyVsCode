namespace BAnalyzer

open System.Collections.Immutable
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Diagnostics

[<DiagnosticAnalyzer(LanguageNames.CSharp)>]
type DateTimeNowAnalyzer() =
    inherit DiagnosticAnalyzer()

    override _.SupportedDiagnostics =
        ImmutableArray.Create(Rules.PreferUtcNow)

    override _.Initialize(context: AnalysisContext) =
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None)
        context.EnableConcurrentExecution()
        context.RegisterSyntaxNodeAction(
            DateTimeNowAnalyzer.AnalyzeMemberAccess,
            SyntaxKind.SimpleMemberAccessExpression
        )

    static member AnalyzeMemberAccess(context: SyntaxNodeAnalysisContext) =
        match context.Node with
        | :? MemberAccessExpressionSyntax as memberAccess when
            memberAccess.Name.Identifier.ValueText = "Now" ->
            match context.SemanticModel.GetSymbolInfo(memberAccess, context.CancellationToken).Symbol with
            | :? IPropertySymbol as property when
                property.Name = "Now"
                && property.ContainingType.SpecialType = SpecialType.System_DateTime ->
                context.ReportDiagnostic(Diagnostic.Create(Rules.PreferUtcNow, memberAccess.GetLocation()))
            | _ -> ()
        | _ -> ()
