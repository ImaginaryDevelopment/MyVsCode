module BPlug.Server.Program

[<EntryPoint>]
let main _ =
    // IDE extensions will start this process and speak a protocol over stdio.
    // Roslyn snapshotting can live here until a second .NET host needs it extracted.
    stdout.WriteLine("BPlug.Server ready")
    0
