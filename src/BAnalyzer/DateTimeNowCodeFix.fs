namespace BAnalyzer

open System
open System.Collections.Immutable
open System.Composition
open System.Threading
open System.Threading.Tasks
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CodeActions
open Microsoft.CodeAnalysis.CodeFixes

[<ExportCodeFixProvider(LanguageNames.CSharp, LanguageNames.VisualBasic, Name = "BAnalyzer.BA0001")>]
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
            let node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie = true)

            match DateTimeNowCodeFix.TryReplaceMemberAccess(node) with
            | None -> ()
            | Some(original, replacement) ->
                let createChangedDocument =
                    Func<CancellationToken, Task<Document>>(fun ct ->
                        DateTimeNowCodeFix.ReplaceAsync(context.Document, original, replacement, ct)
                    )

                context.RegisterCodeFix(CodeAction.Create(title, createChangedDocument, title), diagnostic)
        }
        :> Task

    static member TryReplaceMemberAccess(node: SyntaxNode) =
        let rec walk (current: SyntaxNode) =
            match current with
            | null -> None
            | :? Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax as memberAccess ->
                let replacement =
                    memberAccess.WithName(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName("UtcNow"))
                    :> SyntaxNode

                Some(memberAccess :> SyntaxNode, replacement)
            | :? Microsoft.CodeAnalysis.VisualBasic.Syntax.MemberAccessExpressionSyntax as memberAccess ->
                let replacement =
                    memberAccess.WithName(Microsoft.CodeAnalysis.VisualBasic.SyntaxFactory.IdentifierName("UtcNow"))
                    :> SyntaxNode

                Some(memberAccess :> SyntaxNode, replacement)
            | _ -> walk current.Parent

        walk node

    static member ReplaceAsync
        (document: Document, node: SyntaxNode, replacement: SyntaxNode, cancellationToken: CancellationToken)
        =
        task {
            let! root = document.GetSyntaxRootAsync(cancellationToken)
            return document.WithSyntaxRoot(root.ReplaceNode(node, replacement))
        }
