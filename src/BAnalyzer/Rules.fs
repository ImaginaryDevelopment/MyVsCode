namespace BAnalyzer

open Microsoft.CodeAnalysis

module Rules =
    [<Literal>]
    let Category = "Usage"

    let PreferUtcNow =
        DiagnosticDescriptor(
            id = "BA0001",
            title = "Prefer DateTime.UtcNow over DateTime.Now",
            messageFormat = "Replace DateTime.Now with DateTime.UtcNow",
            category = Category,
            defaultSeverity = DiagnosticSeverity.Warning,
            isEnabledByDefault = true,
            description = "DateTime.Now uses the local time zone, which is a common source of bugs on servers and in distributed systems. Prefer DateTime.UtcNow."
        )
