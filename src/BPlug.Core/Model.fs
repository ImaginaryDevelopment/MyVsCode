namespace BPlug

/// Host-agnostic types. This project must stay Fable-safe: no Roslyn, no IDE APIs.
module Model =
    type TextSpan = { Start: int; Length: int }

    type Import = { Name: string; Span: TextSpan }

    type MemberAccess = { Receiver: string; Name: string; Span: TextSpan }

    type DocumentSnapshot =
        {
            FilePath: string
            Imports: Import list
            MemberAccesses: MemberAccess list
        }

    type Finding =
        {
            Id: string
            Message: string
            FilePath: string
            Span: TextSpan
        }
