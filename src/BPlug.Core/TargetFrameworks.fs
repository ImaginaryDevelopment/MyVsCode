module BPlug.TargetFrameworks

open System
open BPlug.Model

let familyName =
    function
    | TfFamily.NetFramework -> ".NET Framework"
    | TfFamily.Net -> ".NET"
    | TfFamily.NetStandard -> ".NET Standard"
    | TfFamily.NetCoreApp -> ".NET Core"
    | TfFamily.Other -> "target framework"

let private dottedNetFramework (digits: string) =
    match digits.Length with
    | 2 -> sprintf "%c.%c" digits.[0] digits.[1]
    | 3 -> sprintf "%c.%c.%c" digits.[0] digits.[1] digits.[2]
    | _ -> digits

let parse (raw: string) : TargetFramework =
    let moniker = raw.Trim()
    let s = moniker.ToLowerInvariant()

    let family, version =
        if s.StartsWith("v", StringComparison.Ordinal) && s.Length > 1 then
            TfFamily.NetFramework, s.Substring(1)
        elif s.StartsWith("netstandard", StringComparison.Ordinal) then
            TfFamily.NetStandard, s.Substring("netstandard".Length)
        elif s.StartsWith("netcoreapp", StringComparison.Ordinal) then
            TfFamily.NetCoreApp, s.Substring("netcoreapp".Length)
        elif s.StartsWith("net", StringComparison.Ordinal) && s.Length > 3 && Char.IsDigit s.[3] then
            let rest = s.Substring(3)

            if rest.IndexOf('.') >= 0 then
                TfFamily.Net, rest
            elif rest |> Seq.forall Char.IsDigit then
                TfFamily.NetFramework, dottedNetFramework rest
            else
                TfFamily.Other, rest
        else
            TfFamily.Other, s

    { Moniker = moniker; Family = family; Version = version }

let parseNullable (raw: string) =
    match raw.Trim().ToLowerInvariant() with
    | "enable"
    | "true" -> NullableEnable
    | "disable"
    | "false" -> NullableDisable
    | "warnings" -> NullableWarnings
    | _ -> NullableUnspecified

let parseImplicitUsings (raw: string) =
    match raw.Trim().ToLowerInvariant() with
    | "enable"
    | "true" -> ImplicitUsingsEnable
    | "disable"
    | "false" -> ImplicitUsingsDisable
    | _ -> ImplicitUsingsUnspecified
