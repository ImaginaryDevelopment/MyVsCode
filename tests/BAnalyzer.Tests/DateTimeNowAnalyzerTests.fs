module BAnalyzer.Tests.DateTimeNowAnalyzerTests

open BAnalyzer
open Xunit

let private getCSharpDiagnostics = AnalyzerTestHost.getCSharpDiagnostics (DateTimeNowAnalyzer())
let private getVisualBasicDiagnostics = AnalyzerTestHost.getVisualBasicDiagnostics (DateTimeNowAnalyzer())

[<Fact>]
let ``BA0001 is reported for DateTime.Now`` () =
    let source =
        """
        using System;
        public class C
        {
            public DateTime M() => DateTime.Now;
        }
        """

    let diagnostics = getCSharpDiagnostics source
    Assert.Contains(diagnostics, fun d -> d.Id = Rules.PreferUtcNow.Id)

[<Fact>]
let ``BA0001 is not reported for DateTime.UtcNow`` () =
    let source =
        """
        using System;
        public class C
        {
            public DateTime M() => DateTime.UtcNow;
        }
        """

    let diagnostics = getCSharpDiagnostics source
    Assert.DoesNotContain(diagnostics, fun d -> d.Id = Rules.PreferUtcNow.Id)

[<Fact>]
let ``BA0001 is reported for DateTime.Now in Visual Basic`` () =
    let source =
        """
        Imports System
        Public Class C
            Public Function M() As DateTime
                Return DateTime.Now
            End Function
        End Class
        """

    let diagnostics = getVisualBasicDiagnostics source
    Assert.Contains(diagnostics, fun d -> d.Id = Rules.PreferUtcNow.Id)

[<Fact>]
let ``BA0001 is reported for Date.Now in Visual Basic`` () =
    let source =
        """
        Imports System
        Public Class C
            Public Function M() As Date
                Return Date.Now
            End Function
        End Class
        """

    let diagnostics = getVisualBasicDiagnostics source
    Assert.Contains(diagnostics, fun d -> d.Id = Rules.PreferUtcNow.Id)

[<Fact>]
let ``BA0001 is not reported for DateTime.UtcNow in Visual Basic`` () =
    let source =
        """
        Imports System
        Public Class C
            Public Function M() As DateTime
                Return DateTime.UtcNow
            End Function
        End Class
        """

    let diagnostics = getVisualBasicDiagnostics source
    Assert.DoesNotContain(diagnostics, fun d -> d.Id = Rules.PreferUtcNow.Id)
