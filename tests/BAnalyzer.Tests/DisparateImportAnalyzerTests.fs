module BAnalyzer.Tests.DisparateImportAnalyzerTests

open BAnalyzer
open Xunit

let private getCSharpDiagnostics = AnalyzerTestHost.getCSharpDiagnostics (DisparateImportAnalyzer())
let private getVisualBasicDiagnostics = AnalyzerTestHost.getVisualBasicDiagnostics (DisparateImportAnalyzer())

[<Fact>]
let ``BA0002 is reported for System.Data.Linq and System.Drawing`` () =
    let source =
        """
        using System.Data.Linq;
        using System.Drawing;

        public class C { }
        """

    let diagnostics = getCSharpDiagnostics source
    Assert.Contains(diagnostics, fun d -> d.Id = Rules.DisparateImports.Id)
    Assert.Contains(diagnostics, fun d -> d.GetMessage().Contains("System.Data.Linq") && d.GetMessage().Contains("System.Drawing"))

[<Fact>]
let ``BA0002 is not reported for two data-access imports`` () =
    let source =
        """
        using System.Data;
        using System.Data.Linq;

        public class C { }
        """

    let diagnostics = getCSharpDiagnostics source
    Assert.DoesNotContain(diagnostics, fun d -> d.Id = Rules.DisparateImports.Id)

[<Fact>]
let ``BA0002 is not reported for a single layer-specific import`` () =
    let source =
        """
        using System.Data.Linq;

        public class C { }
        """

    let diagnostics = getCSharpDiagnostics source
    Assert.DoesNotContain(diagnostics, fun d -> d.Id = Rules.DisparateImports.Id)

[<Fact>]
let ``BA0002 is not reported for ordinary BCL imports`` () =
    let source =
        """
        using System;
        using System.Collections.Generic;
        using System.Linq;

        public class C { }
        """

    let diagnostics = getCSharpDiagnostics source
    Assert.DoesNotContain(diagnostics, fun d -> d.Id = Rules.DisparateImports.Id)

[<Fact>]
let ``BA0002 is reported for presentation and web imports`` () =
    let source =
        """
        using System.Windows.Forms;
        using Microsoft.AspNetCore.Mvc;

        public class C { }
        """

    let diagnostics = getCSharpDiagnostics source
    Assert.Contains(diagnostics, fun d -> d.Id = Rules.DisparateImports.Id)

[<Fact>]
let ``BA0002 is reported for System.Data.Linq and System.Drawing in Visual Basic`` () =
    let source =
        """
        Imports System.Data.Linq
        Imports System.Drawing

        Public Class C
        End Class
        """

    let diagnostics = getVisualBasicDiagnostics source
    Assert.Contains(diagnostics, fun d -> d.Id = Rules.DisparateImports.Id)
    Assert.Contains(diagnostics, fun d -> d.GetMessage().Contains("System.Data.Linq") && d.GetMessage().Contains("System.Drawing"))

[<Fact>]
let ``BA0002 is not reported for two data-access imports in Visual Basic`` () =
    let source =
        """
        Imports System.Data
        Imports System.Data.Linq

        Public Class C
        End Class
        """

    let diagnostics = getVisualBasicDiagnostics source
    Assert.DoesNotContain(diagnostics, fun d -> d.Id = Rules.DisparateImports.Id)

[<Fact>]
let ``BA0002 is not reported for a single layer-specific import in Visual Basic`` () =
    let source =
        """
        Imports System.Data.Linq

        Public Class C
        End Class
        """

    let diagnostics = getVisualBasicDiagnostics source
    Assert.DoesNotContain(diagnostics, fun d -> d.Id = Rules.DisparateImports.Id)

[<Fact>]
let ``BA0002 is not reported for ordinary BCL imports in Visual Basic`` () =
    let source =
        """
        Imports System
        Imports System.Collections.Generic
        Imports System.Linq

        Public Class C
        End Class
        """

    let diagnostics = getVisualBasicDiagnostics source
    Assert.DoesNotContain(diagnostics, fun d -> d.Id = Rules.DisparateImports.Id)

[<Fact>]
let ``BA0002 is reported for presentation and web imports in Visual Basic`` () =
    let source =
        """
        Imports System.Windows.Forms
        Imports Microsoft.AspNetCore.Mvc

        Public Class C
        End Class
        """

    let diagnostics = getVisualBasicDiagnostics source
    Assert.Contains(diagnostics, fun d -> d.Id = Rules.DisparateImports.Id)
