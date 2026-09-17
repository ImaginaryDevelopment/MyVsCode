module BPlug.VSCode.Extension

/// Fable emits JS for the VS Code extension host. Do not reference Workspace/Roslyn here.
/// The extension will start BPlug.Server and apply Core findings to the Problems panel.
let activate (_context: obj) = ()

let deactivate () = ()
