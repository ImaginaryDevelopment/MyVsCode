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

    type TfFamily =
        | NetFramework
        | Net
        | NetStandard
        | NetCoreApp
        | Other

    type TargetFramework =
        {
            Moniker: string
            Family: TfFamily
            Version: string
        }

    type NullableMode =
        | NullableUnspecified
        | NullableEnable
        | NullableDisable
        | NullableWarnings

    type ImplicitUsingsMode =
        | ImplicitUsingsUnspecified
        | ImplicitUsingsEnable
        | ImplicitUsingsDisable

    type ProjectSnapshot =
        {
            FilePath: string
            Name: string
            TargetFrameworks: TargetFramework list
            Nullable: NullableMode
            ImplicitUsings: ImplicitUsingsMode
        }

    type SolutionSnapshot =
        {
            FilePath: string
            Projects: ProjectSnapshot list
        }
