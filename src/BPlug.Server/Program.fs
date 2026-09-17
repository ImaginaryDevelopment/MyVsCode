module BPlug.Server.Program

[<EntryPoint>]
let main _ =
    // Extensions start this process. It will call BPlug.Workspace to snapshot
    // buffers, then BPlug.Core to produce findings, and reply over stdio.
    stdout.WriteLine("BPlug.Server ready")
    0
