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

    let DisparateImports =
        DiagnosticDescriptor(
            id = "BA0002",
            title = "File mixes imports from different application layers",
            messageFormat = "This file imports {0} ({1}) together with {2} ({3}), which target different application layers",
            category = Category,
            defaultSeverity = DiagnosticSeverity.Warning,
            isEnabledByDefault = true,
            description = "A single compilation unit should not import namespaces that belong to unrelated application layers, such as data access (System.Data.Linq) and presentation (System.Drawing)."
        )
