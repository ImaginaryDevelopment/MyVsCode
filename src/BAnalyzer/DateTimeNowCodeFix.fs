namespace BAnalyzer

open System
open System.Collections.Immutable
open System.Composition
open System.Threading
open System.Threading.Tasks
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CodeActions
open Microsoft.CodeAnalysis.CodeFixes
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax

[<ExportCodeFixProvider(LanguageNames.CSharp, Name = "BAnalyzer.BA0001")>]
[<Shared>]
type DateTimeNowCodeFix() =
    inherit CodeFixProvider()

    static let title = "Use DateTime.UtcNow"

    override _.FixableDiagnosticIds =
        ImmutableArray.Create(Rules.PreferUtcNow.Id)

    override _.GetFixAllProvider() =
        WellKnownFixAllProviders.BatchFixer

    override _.RegisterCodeFixesAsync(context: CodeFixContext) : Task =
        task {
            let! root = context.Document.GetSyntaxRootAsync(context.CancellationToken)
            let diagnostic = Seq.head context.Diagnostics
            match root.FindNode(diagnostic.Location.SourceSpan) with
            | :? MemberAccessExpressionSyntax as memberAccess ->
                let createChangedDocument =
                    Func<CancellationToken, Task<Document>>(fun ct ->
                        DateTimeNowCodeFix.UseUtcNowAsync(context.Document, memberAccess, ct)
                    )

                context.RegisterCodeFix(CodeAction.Create(title, createChangedDocument, title), diagnostic)
            | _ -> ()
        }
        :> Task

    static member UseUtcNowAsync
        (document: Document, memberAccess: MemberAccessExpressionSyntax, cancellationToken: CancellationToken)
        =
        task {
            let! root = document.GetSyntaxRootAsync(cancellationToken)
            let utcNow = memberAccess.WithName(SyntaxFactory.IdentifierName("UtcNow"))
            let newRoot = root.ReplaceNode(memberAccess, utcNow)
            return document.WithSyntaxRoot(newRoot)
        }
