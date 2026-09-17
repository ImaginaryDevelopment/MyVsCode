namespace BAnalyzer

open System.Collections.Immutable
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.Diagnostics
open Microsoft.CodeAnalysis.Operations

[<DiagnosticAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic)>]
type DateTimeNowAnalyzer() =
    inherit DiagnosticAnalyzer()

    override _.SupportedDiagnostics =
        ImmutableArray.Create(Rules.PreferUtcNow)

    override _.Initialize(context: AnalysisContext) =
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None)
        context.EnableConcurrentExecution()
        context.RegisterOperationAction(
            DateTimeNowAnalyzer.AnalyzePropertyReference,
            OperationKind.PropertyReference
        )

    static member AnalyzePropertyReference(context: OperationAnalysisContext) =
        match context.Operation with
        | :? IPropertyReferenceOperation as propertyReference ->
            let property = propertyReference.Property

            if property.Name = "Now"
               && not (isNull property.ContainingType)
               && property.ContainingType.SpecialType = SpecialType.System_DateTime then
                context.ReportDiagnostic(
                    Diagnostic.Create(Rules.PreferUtcNow, propertyReference.Syntax.GetLocation())
                )
        | _ -> ()
