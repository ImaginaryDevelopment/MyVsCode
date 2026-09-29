<Query Kind="FSharpProgram">
  <NuGetReference>NuGet.Versioning</NuGetReference>
  <Namespace>NuGet.Versioning</Namespace>
  <IncludeUncapsulator>false</IncludeUncapsulator>
</Query>

// Azure DevOps multi-repo lockfile CVE audit (npm package-lock.json + NuGet packages.lock.json).
// Reports P/D/I, publish dates (cached under %TEMP%/linqpad-package-dates), and clickable LockPath links.
//
// Shared prefs:
//   adoTargetsJson — [{ "Org","Project" }] (same key as other ADO LINQPad scripts)
//
// Script prefs:
//   azurePackageAuditReposJson     — [{ Org, Project, Repo, RepoId, Branch }]
//   azurePackageAuditLockfilesJson — [{ Org, Project, Repo, RepoId, Branch, Path, Kind }]
//                                   Kind = "npm" | "nuget"
//   azurePackageAuditFocusRepo     — "all" or org/project/repo@branch (last focus pick)
//   azurePackageAuditParallelism   — concurrent ADO/OSV work (default 14)
//
// Secret: Util.GetPassword("adoPat") — Code (Read) across orgs in adoTargetsJson
//
// Flow:
//   1) Manage org/project targets
//   2) Discover repos → DumpContainer with Included vs Available + Status; include/exclude/save
//   2b) Optional focus on one included repo (or all)
//   3) Scan focused repos for package-lock.json / packages.lock.json; edit audit set
//   4) Download lockfiles; CVE-check (OSV for npm, nuget.org VulnerabilityInfo for NuGet)
//   5) Dump vulnerable packages only + summary

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading.Tasks
open LINQPad.Controls
open NuGet.Versioning

// --- prefs ---------------------------------------------------------------------

[<Literal>]
let PrefTargetsJson = "adoTargetsJson"

[<Literal>]
let PrefReposJson = "azurePackageAuditReposJson"

[<Literal>]
let PrefLockfilesJson = "azurePackageAuditLockfilesJson"

[<Literal>]
let PrefParallelism = "azurePackageAuditParallelism"

[<Literal>]
let PrefPat = "adoPat"

let defaultIfNone =
    function
    | None -> String.Empty
    | Some v -> v

let createUserPref<'t> key (fRead: string -> 't) (fWrite: 't -> string) =
    let getUserPref () =
        let maybeValue = Util.LoadString key
        if String.IsNullOrEmpty maybeValue then None
        else maybeValue |> fRead |> Some
    let setUserPref =
        function
        | None -> Util.SaveString(key, null)
        | Some v -> Util.SaveString(key, fWrite v)
    getUserPref, setUserPref

let promptLine (title: string) (seed: string) =
    let entered = Util.ReadLine(title, seed)
    if isNull entered then String.Empty else entered.Trim()

let jsonOpts =
    let o = JsonSerializerOptions(WriteIndented = false, PropertyNameCaseInsensitive = true)
    o

// --- shared org/project targets ------------------------------------------------

[<CLIMutable>]
type AdoTarget = {
    Org: string
    Project: string
}

let loadTargets () : AdoTarget list =
    let raw = Util.LoadString PrefTargetsJson
    if String.IsNullOrWhiteSpace raw then []
    else
        try
            JsonSerializer.Deserialize<AdoTarget[]>(raw, jsonOpts)
            |> Array.toList
            |> List.filter (fun t ->
                not (String.IsNullOrWhiteSpace t.Org)
                && not (String.IsNullOrWhiteSpace t.Project))
        with ex ->
            invalidOp (sprintf "Failed to parse %s: %s" PrefTargetsJson ex.Message)

let saveTargets (targets: AdoTarget list) =
    let distinct =
        targets
        |> List.distinctBy (fun t -> t.Org.ToLowerInvariant(), t.Project.ToLowerInvariant())
        |> List.sortBy (fun t -> t.Org.ToLowerInvariant(), t.Project.ToLowerInvariant())
    Util.SaveString(PrefTargetsJson, JsonSerializer.Serialize(List.toArray distinct, jsonOpts))
    distinct

let targetKey (t: AdoTarget) = sprintf "%s / %s" t.Org t.Project

let manageTargets () =
    let mutable targets = loadTargets ()
    targets
    |> List.mapi (fun i t -> {| Index = i + 1; Org = t.Org; Project = t.Project |})
    |> fun rows -> rows.Dump(sprintf "stored %s (%d)" PrefTargetsJson rows.Length)

    let rec loop () =
        let cmd =
            promptLine "Targets: [Enter]=continue, a=add, d=delete by index, c=clear all" ""
            |> fun s -> s.ToLowerInvariant()
        match cmd with
        | "" | "q" | "done" | "continue" -> targets
        | "c" | "clear" ->
            targets <- saveTargets []
            [].Dump("targets cleared")
            loop ()
        | "a" | "add" ->
            let org = promptLine "Organization name" ""
            let project = promptLine "Project name" ""
            if String.IsNullOrWhiteSpace org || String.IsNullOrWhiteSpace project then
                "add cancelled (org and project required)".Dump()
            else
                targets <- saveTargets ({ Org = org; Project = project } :: targets)
                targets
                |> List.mapi (fun i t -> {| Index = i + 1; Org = t.Org; Project = t.Project |})
                |> fun rows -> rows.Dump(sprintf "stored targets now (%d)" rows.Length)
            loop ()
        | "d" | "delete" | "del" ->
            let idxText = promptLine "Index to delete (from table)" ""
            match Int32.TryParse idxText with
            | true, n when n >= 1 && n <= targets.Length ->
                let removed = targets.[n - 1]
                targets <-
                    saveTargets (
                        targets
                        |> List.indexed
                        |> List.filter (fun (i, _) -> i <> n - 1)
                        |> List.map snd)
                sprintf "removed %s" (targetKey removed) |> fun s -> s.Dump()
                targets
                |> List.mapi (fun i t -> {| Index = i + 1; Org = t.Org; Project = t.Project |})
                |> fun rows -> rows.Dump(sprintf "stored targets now (%d)" rows.Length)
            | _ -> "invalid index".Dump()
            loop ()
        | _ ->
            "unknown command — use Enter / a / d / c".Dump()
            loop ()

    let final = loop ()
    if final.IsEmpty then
        invalidOp (sprintf "No targets in %s. Use 'a' to add org+project pairs." PrefTargetsJson)
    final

// --- audit repo / lockfile prefs -----------------------------------------------

[<CLIMutable>]
type AuditRepo = {
    Org: string
    Project: string
    Repo: string
    RepoId: string
    Branch: string
}

[<CLIMutable>]
type AuditLockfile = {
    Org: string
    Project: string
    Repo: string
    RepoId: string
    Branch: string
    Path: string
    Kind: string // npm | nuget
}

let loadJsonList<'t> key : 't list =
    let raw = Util.LoadString key
    if String.IsNullOrWhiteSpace raw then []
    else
        try
            JsonSerializer.Deserialize<'t[]>(raw, jsonOpts) |> Array.toList
        with ex ->
            invalidOp (sprintf "Failed to parse %s: %s" key ex.Message)

let saveJsonList key (items: 't list) =
    Util.SaveString(key, JsonSerializer.Serialize(List.toArray items, jsonOpts))
    items

let repoKey (r: AuditRepo) =
    sprintf "%s/%s/%s@%s" r.Org r.Project r.Repo r.Branch

let lockKey (l: AuditLockfile) =
    sprintf "%s/%s/%s@%s:%s" l.Org l.Project l.Repo l.Branch l.Path

let normalizeBranch (b: string) =
    if String.IsNullOrWhiteSpace b then "main"
    elif b.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase) then
        b.Substring("refs/heads/".Length)
    else
        b.Trim()

let detectLockKind (path: string) =
    let name = Path.GetFileName path
    if name.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase) then Some "npm"
    elif name.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase) then Some "nuget"
    else None

let lockfileBrowseUrl (l: AuditLockfile) =
    sprintf
        "https://dev.azure.com/%s/%s/_git/%s?path=%s&version=GB%s&_a=contents"
        (Uri.EscapeDataString l.Org)
        (Uri.EscapeDataString l.Project)
        (Uri.EscapeDataString l.Repo)
        (Uri.EscapeDataString l.Path)
        (Uri.EscapeDataString (normalizeBranch l.Branch))

let lockPathLink (l: AuditLockfile) =
    Hyperlinq(lockfileBrowseUrl l, l.Path)

// --- HTTP / ADO ----------------------------------------------------------------

let pat =
    let p = Util.GetPassword PrefPat
    if String.IsNullOrWhiteSpace p then
        invalidOp (sprintf "Set Util password key '%s' (Code Read)." PrefPat)
    p.Trim()

let targets = manageTargets ()

let parallelism =
    let get, save = createUserPref PrefParallelism int string
    let seed = get () |> Option.defaultValue 14
    let entered =
        promptLine "CVE / ADO scan parallelism (concurrent requests)" (string seed)
    match Int32.TryParse entered with
    | true, n when n > 0 ->
        save (Some n)
        n
    | _ ->
        save (Some 14)
        14

{|
    TargetCount = targets.Length
    Parallelism = parallelism
    PrefKeys =
        [|
            PrefTargetsJson
            PrefReposJson
            PrefLockfilesJson
            PrefParallelism
            "azurePackageAuditFocusRepo"
        |]
|}.Dump("AzurePackageAuditTool")

let makeClient () =
    let handler = new HttpClientHandler(AutomaticDecompression = DecompressionMethods.All)
    let client = new HttpClient(handler)
    client.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue("application/json"))
    let token = Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat))
    client.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Basic", token)
    client.Timeout <- TimeSpan.FromMinutes 10.0
    client

let readJson (client: HttpClient) (method: HttpMethod) (url: string) (body: string option) =
    use req = new HttpRequestMessage(method, url)
    match body with
    | Some json -> req.Content <- new StringContent(json, Encoding.UTF8, "application/json")
    | None -> ()
    use resp = client.Send(req)
    let text = resp.Content.ReadAsStringAsync().Result
    if not resp.IsSuccessStatusCode then
        failwith (sprintf "%s %s -> %A%s%s" (method.Method) url resp.StatusCode Environment.NewLine text)
    JsonDocument.Parse text

let propStr (el: JsonElement) (name: string) =
    match el.TryGetProperty name with
    | true, p when p.ValueKind = JsonValueKind.String -> p.GetString()
    | true, p when p.ValueKind = JsonValueKind.Number -> p.ToString()
    | _ -> null

let propArr (el: JsonElement) (name: string) =
    match el.TryGetProperty name with
    | true, p when p.ValueKind = JsonValueKind.Array -> p.EnumerateArray() |> Seq.toList
    | _ -> []

let baseUrl (org: string) (project: string) =
    sprintf
        "https://dev.azure.com/%s/%s"
        (Uri.EscapeDataString org)
        (Uri.EscapeDataString project)

let runParallel (degree: int) (items: 'a list) (work: 'a -> 'b) : 'b list =
    if items.IsEmpty then []
    else
        items
        |> List.chunkBySize (max 1 degree)
        |> List.collect (fun batch ->
            batch
            |> List.map (fun x -> async { return work x })
            |> Async.Parallel
            |> Async.RunSynchronously
            |> Array.toList)

// --- discover repos ------------------------------------------------------------

let listReposForTarget (client: HttpClient) (t: AdoTarget) : AuditRepo list =
    let url = sprintf "%s/_apis/git/repositories?api-version=7.1" (baseUrl t.Org t.Project)
    use doc = readJson client HttpMethod.Get url None
    propArr doc.RootElement "value"
    |> List.choose (fun r ->
        let id = propStr r "id"
        let name = propStr r "name"
        let branch = propStr r "defaultBranch" |> fun b -> if isNull b then "main" else normalizeBranch b
        if isNull id || isNull name then None
        else
            Some
                {
                    Org = t.Org
                    Project = t.Project
                    Repo = name
                    RepoId = id
                    Branch = branch
                })

let listBranchesForRepo (client: HttpClient) (r: AuditRepo) : string list =
    let url =
        sprintf
            "%s/_apis/git/repositories/%s/refs?filter=heads/&api-version=7.1"
            (baseUrl r.Org r.Project)
            (Uri.EscapeDataString (if String.IsNullOrWhiteSpace r.RepoId then r.Repo else r.RepoId))
    use doc = readJson client HttpMethod.Get url None
    propArr doc.RootElement "value"
    |> List.choose (fun refEl ->
        let name = propStr refEl "name"
        if isNull name then None
        else Some(normalizeBranch name))
    |> List.distinct
    |> List.sortBy (fun b -> b.ToLowerInvariant())

let promptLineWithSuggestions (title: string) (seed: string) (suggestions: string seq) =
    let entered = Util.ReadLine(title, seed, suggestions)
    if isNull entered then String.Empty else entered.Trim()

let discoverRepos (client: HttpClient) (ts: AdoTarget list) =
    let results =
        runParallel parallelism ts (fun t ->
            try Ok(listReposForTarget client t)
            with ex -> Error(sprintf "%s: %s" (targetKey t) ex.Message))
    let errors = results |> List.choose (function Error e -> Some e | _ -> None)
    let repos = results |> List.choose (function Ok xs -> Some xs | _ -> None) |> List.concat
    let sorted =
        repos
        |> List.distinctBy (fun r ->
            r.Org.ToLowerInvariant(), r.Project.ToLowerInvariant(), r.Repo.ToLowerInvariant(), r.Branch.ToLowerInvariant())
        |> List.sortBy (fun r -> r.Org.ToLowerInvariant(), r.Project.ToLowerInvariant(), r.Repo.ToLowerInvariant())
    sorted, errors

let repoIdentity (r: AuditRepo) =
    r.Org.ToLowerInvariant(),
    r.Project.ToLowerInvariant(),
    r.Repo.ToLowerInvariant(),
    r.Branch.ToLowerInvariant()

let sortRepos (repos: AuditRepo list) =
    repos
    |> List.sortBy (fun r ->
        r.Org.ToLowerInvariant(), r.Project.ToLowerInvariant(), r.Repo.ToLowerInvariant(), r.Branch.ToLowerInvariant())

let availableFrom (discovered: AuditRepo list) (included: AuditRepo list) =
    let includedIds = included |> List.map repoIdentity |> Set.ofList
    discovered
    |> List.filter (fun r -> not (Set.contains (repoIdentity r) includedIds))
    |> sortRepos

let repoRows (repos: AuditRepo list) =
    repos
    |> List.mapi (fun i r ->
        {|
            Index = i + 1
            Org = r.Org
            Project = r.Project
            Repo = r.Repo
            Branch = r.Branch
        |})

let manageRepos (client: HttpClient) (ts: AdoTarget list) =
    let mutable included = loadJsonList<AuditRepo> PrefReposJson |> sortRepos
    let mutable discovered: AuditRepo list = []
    let mutable available: AuditRepo list = []
    let mutable status = "discovering repos from targets…"

    let container = DumpContainer()
    container.Dump("repo management")

    let refreshView () =
        container.Content <-
            {|
                Status = status
                Included = repoRows included
                Available =
                    Util.OnDemand(
                        sprintf "%d available — expand to include" available.Length,
                        Func<obj>(fun () -> repoRows available :> obj))
            |}

    refreshView ()

    let discoveredRepos, discoverErrors = discoverRepos client ts
    discovered <- discoveredRepos
    available <- availableFrom discovered included
    status <-
        if discoverErrors.IsEmpty then
            sprintf
                "discovered %d repos — %d included, %d available"
                discovered.Length
                included.Length
                available.Length
        else
            sprintf
                "discovered %d repos (%d errors) — %d included, %d available"
                discovered.Length
                discoverErrors.Length
                included.Length
                available.Length
    refreshView ()
    if not discoverErrors.IsEmpty then
        discoverErrors.Dump("repo discovery errors")

    let persistIncluded () =
        included <- saveJsonList PrefReposJson (sortRepos included) |> sortRepos
        available <- availableFrom discovered included

    let rec loop () =
        let cmd =
            promptLine
                "Repos: [Enter]=continue, i=include#, x=exclude#, cb=change branch (included#), a=add, r=rediscover, c=clear"
                ""
            |> fun s -> s.ToLowerInvariant()
        match cmd with
        | "" | "q" | "done" | "continue" -> included
        | "c" | "clear" ->
            let n = included.Length
            included <- []
            persistIncluded ()
            status <- sprintf "cleared %d included repo(s)" n
            refreshView ()
            loop ()
        | "r" | "rediscover" ->
            status <- "rediscovering…"
            refreshView ()
            let found, errors = discoverRepos client ts
            discovered <- found
            available <- availableFrom discovered included
            status <-
                if errors.IsEmpty then
                    sprintf
                        "rediscovered %d repos — %d included, %d available"
                        discovered.Length
                        included.Length
                        available.Length
                else
                    sprintf
                        "rediscovered %d repos (%d errors) — %d included, %d available"
                        discovered.Length
                        errors.Length
                        included.Length
                        available.Length
            refreshView ()
            if not errors.IsEmpty then
                errors.Dump("repo discovery errors")
            loop ()
        | "cb" | "changebranch" | "change-branch" ->
            let idxText = promptLine "Included index to change branch" ""
            match Int32.TryParse idxText with
            | true, n when n >= 1 && n <= included.Length ->
                let current = included.[n - 1]
                status <- sprintf "loading branches for %s…" (repoKey current)
                refreshView ()
                let branches, branchLoadNote =
                    try
                        let bs = listBranchesForRepo client current
                        bs,
                        if bs.IsEmpty then " (no heads/ refs returned)"
                        else sprintf " — %d branch(es); start typing for autocomplete" bs.Length
                    with ex ->
                        [], sprintf " — failed to list branches: %s" ex.Message
                container.Content <-
                    {|
                        Status = sprintf "change branch%s" branchLoadNote
                        Org = current.Org
                        Project = current.Project
                        Repo = current.Repo
                        Branch = current.Branch
                        Branches = branches
                    |}
                let newBranch =
                    promptLineWithSuggestions
                        (sprintf "New branch for %s/%s/%s" current.Org current.Project current.Repo)
                        current.Branch
                        branches
                    |> normalizeBranch
                if String.IsNullOrWhiteSpace newBranch then
                    status <- "change branch cancelled — empty branch"
                elif String.Equals(newBranch, current.Branch, StringComparison.OrdinalIgnoreCase) then
                    status <- sprintf "branch unchanged: %s" (repoKey current)
                else
                    let updated = { current with Branch = newBranch }
                    if
                        included
                        |> List.indexed
                        |> List.exists (fun (i, r) -> i <> n - 1 && repoIdentity r = repoIdentity updated)
                    then
                        status <- sprintf "change branch cancelled — already included: %s" (repoKey updated)
                    else
                        included <-
                            included
                            |> List.mapi (fun i r -> if i = n - 1 then updated else r)
                        persistIncluded ()
                        status <- sprintf "branch %s → %s (%s)" current.Branch newBranch (repoKey updated)
                refreshView ()
            | _ ->
                status <- "change branch cancelled — invalid included index"
                refreshView ()
            loop ()
        | "i" | "include" ->
            let idxText = promptLine "Available index to include" ""
            match Int32.TryParse idxText with
            | true, n when n >= 1 && n <= available.Length ->
                let chosen = available.[n - 1]
                included <- chosen :: included
                persistIncluded ()
                status <- sprintf "included %s" (repoKey chosen)
                refreshView ()
            | _ ->
                status <- "include cancelled — invalid available index"
                refreshView ()
            loop ()
        | "x" | "exclude" | "d" | "delete" | "del" ->
            let idxText = promptLine "Included index to exclude" ""
            match Int32.TryParse idxText with
            | true, n when n >= 1 && n <= included.Length ->
                let removed = included.[n - 1]
                included <-
                    included
                    |> List.indexed
                    |> List.filter (fun (i, _) -> i <> n - 1)
                    |> List.map snd
                persistIncluded ()
                status <- sprintf "excluded %s" (repoKey removed)
                refreshView ()
            | _ ->
                status <- "exclude cancelled — invalid included index"
                refreshView ()
            loop ()
        | "a" | "add" ->
            let known =
                (ts |> List.map (fun t -> t.Org, t.Project))
                @ (discovered |> List.map (fun r -> r.Org, r.Project))
                @ (included |> List.map (fun r -> r.Org, r.Project))
            let orgSuggestions =
                known
                |> List.map fst
                |> List.filter (fun s -> not (String.IsNullOrWhiteSpace s))
                |> List.distinctBy (fun s -> s.ToLowerInvariant())
                |> List.sortBy (fun s -> s.ToLowerInvariant())
            let orgSeed =
                ts
                |> List.tryHead
                |> Option.map (fun t -> t.Org)
                |> Option.defaultValue (orgSuggestions |> List.tryHead |> Option.defaultValue "")
            let org =
                if orgSuggestions.IsEmpty then promptLine "Organization" orgSeed
                else promptLineWithSuggestions "Organization (start typing for autocomplete)" orgSeed orgSuggestions
            let projectSuggestions =
                known
                |> List.filter (fun (o, _) ->
                    String.IsNullOrWhiteSpace org
                    || String.Equals(o, org, StringComparison.OrdinalIgnoreCase))
                |> List.map snd
                |> List.filter (fun s -> not (String.IsNullOrWhiteSpace s))
                |> List.distinctBy (fun s -> s.ToLowerInvariant())
                |> List.sortBy (fun s -> s.ToLowerInvariant())
            let projectSeed =
                known
                |> List.tryFind (fun (o, _) -> String.Equals(o, org, StringComparison.OrdinalIgnoreCase))
                |> Option.map snd
                |> Option.defaultValue (
                    ts
                    |> List.tryHead
                    |> Option.map (fun t -> t.Project)
                    |> Option.defaultValue (projectSuggestions |> List.tryHead |> Option.defaultValue ""))
            let project =
                if projectSuggestions.IsEmpty then promptLine "Project" projectSeed
                else
                    promptLineWithSuggestions
                        "Project (start typing for autocomplete)"
                        projectSeed
                        projectSuggestions
            let repoSuggestions =
                let fromKnown =
                    discovered @ included
                    |> List.filter (fun r ->
                        String.Equals(r.Org, org, StringComparison.OrdinalIgnoreCase)
                        && String.Equals(r.Project, project, StringComparison.OrdinalIgnoreCase))
                    |> List.map (fun r -> r.Repo)
                let fromAdo =
                    if String.IsNullOrWhiteSpace org || String.IsNullOrWhiteSpace project then []
                    else
                        status <- sprintf "loading repos for %s / %s…" org project
                        refreshView ()
                        try
                            listReposForTarget client { Org = org; Project = project }
                            |> List.map (fun r -> r.Repo)
                        with _ -> []
                fromKnown @ fromAdo
                |> List.filter (fun s -> not (String.IsNullOrWhiteSpace s))
                |> List.distinctBy (fun s -> s.ToLowerInvariant())
                |> List.sortBy (fun s -> s.ToLowerInvariant())
            let repo =
                if repoSuggestions.IsEmpty then promptLine "Repo name" ""
                else promptLineWithSuggestions "Repo name (start typing for autocomplete)" "" repoSuggestions
            let branch = promptLine "Branch (no refs/heads/)" "main"
            if String.IsNullOrWhiteSpace org || String.IsNullOrWhiteSpace project || String.IsNullOrWhiteSpace repo then
                status <- "add cancelled — org, project, and repo required"
                refreshView ()
            else
                let repoId =
                    try
                        listReposForTarget client { Org = org; Project = project }
                        |> List.tryFind (fun r ->
                            String.Equals(r.Repo, repo, StringComparison.OrdinalIgnoreCase))
                        |> Option.map (fun r -> r.RepoId)
                        |> Option.defaultValue ""
                    with _ -> ""
                let added =
                    {
                        Org = org
                        Project = project
                        Repo = repo
                        RepoId = repoId
                        Branch = normalizeBranch branch
                    }
                if included |> List.exists (fun r -> repoIdentity r = repoIdentity added) then
                    status <- sprintf "already included: %s" (repoKey added)
                else
                    included <- added :: included
                    persistIncluded ()
                    status <-
                        if String.IsNullOrWhiteSpace repoId then
                            sprintf "included %s (warning: RepoId unresolved)" (repoKey added)
                        else
                            sprintf "included %s" (repoKey added)
                refreshView ()
            loop ()
        | _ ->
            status <- "unknown command — use Enter / i / x / cb / a / r / c"
            refreshView ()
            loop ()

    let final = loop ()
    if final.IsEmpty then
        invalidOp "No repos included. Use 'i' (from available) or 'a' to include repositories."
    status <- sprintf "continuing with %d included repo(s)" final.Length
    refreshView ()
    final

// --- discover lockfiles --------------------------------------------------------

let listLockfilesInRepo (client: HttpClient) (r: AuditRepo) : AuditLockfile list =
    let branch = normalizeBranch r.Branch
    let url =
        sprintf
            "%s/_apis/git/repositories/%s/items?scopePath=/&recursionLevel=Full&versionDescriptor[version]=%s&versionDescriptor[versionType]=branch&api-version=7.1"
            (baseUrl r.Org r.Project)
            (Uri.EscapeDataString (if String.IsNullOrWhiteSpace r.RepoId then r.Repo else r.RepoId))
            (Uri.EscapeDataString branch)
    use doc = readJson client HttpMethod.Get url None
    let items =
        match doc.RootElement.ValueKind with
        | JsonValueKind.Array -> doc.RootElement.EnumerateArray() |> Seq.toList
        | _ -> propArr doc.RootElement "value"
    items
    |> List.choose (fun item ->
        let isFolder =
            match item.TryGetProperty "isFolder" with
            | true, p when p.ValueKind = JsonValueKind.True -> true
            | _ -> false
        if isFolder then None
        else
            let path = propStr item "path"
            if isNull path then None
            else
                match detectLockKind path with
                | None -> None
                | Some kind ->
                    Some
                        {
                            Org = r.Org
                            Project = r.Project
                            Repo = r.Repo
                            RepoId = r.RepoId
                            Branch = branch
                            Path = path
                            Kind = kind
                        })

let discoverLockfiles (client: HttpClient) (repos: AuditRepo list) =
    let results =
        runParallel parallelism repos (fun r ->
            try Ok(listLockfilesInRepo client r)
            with ex -> Error(sprintf "%s: %s" (repoKey r) ex.Message))
    let errors = results |> List.choose (function Error e -> Some e | _ -> None)
    let locks = results |> List.choose (function Ok xs -> Some xs | _ -> None) |> List.concat
    if not errors.IsEmpty then
        errors.Dump("lockfile discovery errors")
    locks
    |> List.distinctBy (fun l ->
        l.Org.ToLowerInvariant(),
        l.Project.ToLowerInvariant(),
        l.Repo.ToLowerInvariant(),
        l.Branch.ToLowerInvariant(),
        l.Path.ToLowerInvariant())
    |> List.sortBy (fun l ->
        l.Org.ToLowerInvariant(), l.Project.ToLowerInvariant(), l.Repo.ToLowerInvariant(), l.Path.ToLowerInvariant())

let dumpLockfiles (locks: AuditLockfile list) (title: string) =
    locks
    |> List.mapi (fun i l ->
        {|
            Index = i + 1
            Kind = l.Kind
            Org = l.Org
            Project = l.Project
            Repo = l.Repo
            Branch = l.Branch
            Path = lockPathLink l
        |})
    |> fun rows -> rows.Dump(title)

let lockRepoIdentity (l: AuditLockfile) =
    l.Org.ToLowerInvariant(),
    l.Project.ToLowerInvariant(),
    l.Repo.ToLowerInvariant(),
    l.Branch.ToLowerInvariant()

let manageLockfiles (client: HttpClient) (repos: AuditRepo list) =
    let includedRepoIds = repos |> List.map repoIdentity |> Set.ofList
    let matchesIncludedRepo (l: AuditLockfile) =
        Set.contains (lockRepoIdentity l) includedRepoIds

    // Always scan the current included repos at their configured branches (do not reuse a
    // prior default-branch scan when the user changed branches with cb).
    sprintf "scanning %d included repo(s) for lockfiles at configured branches…" repos.Length
    |> fun s -> s.Dump()
    let found = discoverLockfiles client repos
    let savedMatching =
        loadJsonList<AuditLockfile> PrefLockfilesJson
        |> List.filter matchesIncludedRepo

    let mutable locks =
        if savedMatching.IsEmpty then
            found
        else
            // Preserve prior curation (deletes) when still on the same repo@branch;
            // only keep saved paths that still exist on this branch scan.
            let foundKeys = found |> List.map lockKey |> Set.ofList
            let kept = savedMatching |> List.filter (fun l -> foundKeys.Contains(lockKey l))
            if kept.IsEmpty then found else kept

    locks <- saveJsonList PrefLockfilesJson locks
    dumpLockfiles
        locks
        (sprintf "lockfiles for included repos@branches (%d) — edit before audit" locks.Length)

    let rec loop () =
        let cmd =
            promptLine
                "Lockfiles: [Enter]=continue (audit), a=add, d=delete by index, r=rescan (replace), c=clear"
                ""
            |> fun s -> s.ToLowerInvariant()
        match cmd with
        | "" | "q" | "done" | "continue" -> locks
        | "c" | "clear" ->
            locks <- saveJsonList PrefLockfilesJson []
            [].Dump("lockfiles cleared")
            loop ()
        | "r" | "rescan" | "rediscover" ->
            let foundNow = discoverLockfiles client repos
            locks <- saveJsonList PrefLockfilesJson foundNow
            dumpLockfiles locks (sprintf "lockfiles after rescan (%d)" locks.Length)
            loop ()
        | "a" | "add" ->
            let org = promptLine "Organization" ""
            let project = promptLine "Project" ""
            let repo = promptLine "Repo" ""
            let branchSeed =
                repos
                |> List.tryFind (fun r ->
                    String.Equals(r.Org, org, StringComparison.OrdinalIgnoreCase)
                    && String.Equals(r.Project, project, StringComparison.OrdinalIgnoreCase)
                    && String.Equals(r.Repo, repo, StringComparison.OrdinalIgnoreCase))
                |> Option.map (fun r -> r.Branch)
                |> Option.defaultValue "main"
            let branch = promptLine "Branch" branchSeed
            let path = promptLine "Path (e.g. /src/app/package-lock.json)" ""
            let kindSeed =
                detectLockKind path |> Option.defaultValue "npm"
            let kind = promptLine "Kind: npm | nuget" kindSeed |> fun s -> s.ToLowerInvariant()
            if
                String.IsNullOrWhiteSpace org
                || String.IsNullOrWhiteSpace project
                || String.IsNullOrWhiteSpace repo
                || String.IsNullOrWhiteSpace path
            then
                "add cancelled".Dump()
            elif kind <> "npm" && kind <> "nuget" then
                "kind must be npm or nuget".Dump()
            else
                let matchedRepo =
                    repos
                    |> List.tryFind (fun r ->
                        String.Equals(r.Org, org, StringComparison.OrdinalIgnoreCase)
                        && String.Equals(r.Project, project, StringComparison.OrdinalIgnoreCase)
                        && String.Equals(r.Repo, repo, StringComparison.OrdinalIgnoreCase)
                        && String.Equals(r.Branch, normalizeBranch branch, StringComparison.OrdinalIgnoreCase))
                let repoId =
                    matchedRepo
                    |> Option.map (fun r -> r.RepoId)
                    |> Option.defaultValue (
                        repos
                        |> List.tryFind (fun r ->
                            String.Equals(r.Org, org, StringComparison.OrdinalIgnoreCase)
                            && String.Equals(r.Project, project, StringComparison.OrdinalIgnoreCase)
                            && String.Equals(r.Repo, repo, StringComparison.OrdinalIgnoreCase))
                        |> Option.map (fun r -> r.RepoId)
                        |> Option.defaultValue "")
                locks <-
                    saveJsonList PrefLockfilesJson (
                        {
                            Org = org
                            Project = project
                            Repo = repo
                            RepoId = repoId
                            Branch = normalizeBranch branch
                            Path = if path.StartsWith("/") then path else "/" + path
                            Kind = kind
                        }
                        :: locks
                        |> List.distinctBy (fun l -> lockKey l))
                dumpLockfiles locks (sprintf "lockfiles now (%d)" locks.Length)
            loop ()
        | "d" | "delete" | "del" ->
            let idxText = promptLine "Index to delete" ""
            match Int32.TryParse idxText with
            | true, n when n >= 1 && n <= locks.Length ->
                let removed = locks.[n - 1]
                locks <-
                    saveJsonList PrefLockfilesJson (
                        locks
                        |> List.indexed
                        |> List.filter (fun (i, _) -> i <> n - 1)
                        |> List.map snd)
                sprintf "removed %s" (lockKey removed) |> fun s -> s.Dump()
                dumpLockfiles locks (sprintf "lockfiles now (%d)" locks.Length)
            | _ -> "invalid index".Dump()
            loop ()
        | _ ->
            "unknown — Enter / a / d / r / c".Dump()
            loop ()

    let final = loop ()
    if final.IsEmpty then
        invalidOp "No lockfiles selected for audit."
    final

// --- download lockfile content -------------------------------------------------

let downloadItemContent (client: HttpClient) (l: AuditLockfile) =
    let url =
        sprintf
            "%s/_apis/git/repositories/%s/items?path=%s&versionDescriptor[version]=%s&versionDescriptor[versionType]=branch&includeContent=true&api-version=7.1"
            (baseUrl l.Org l.Project)
            (Uri.EscapeDataString (if String.IsNullOrWhiteSpace l.RepoId then l.Repo else l.RepoId))
            (Uri.EscapeDataString l.Path)
            (Uri.EscapeDataString (normalizeBranch l.Branch))
    use doc = readJson client HttpMethod.Get url None
    let content = propStr doc.RootElement "content"
    if isNull content || String.IsNullOrWhiteSpace content then
        failwith (sprintf "empty content for %s" (lockKey l))
    content

// --- parse packages ------------------------------------------------------------

let tryGetString (el: JsonElement) (name: string) =
    let mutable prop = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(name, &prop) && prop.ValueKind = JsonValueKind.String then Some(prop.GetString())
    else None

let tryGetBool (el: JsonElement) (name: string) =
    let mutable prop = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(name, &prop) then
        match prop.ValueKind with
        | JsonValueKind.True -> Some true
        | JsonValueKind.False -> Some false
        | _ -> None
    else None

let tryGetObject (el: JsonElement) (name: string) =
    let mutable prop = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(name, &prop) && prop.ValueKind = JsonValueKind.Object then Some prop
    else None

let packageNameFromLockPath (key: string) =
    if String.IsNullOrEmpty key then None
    else
        let marker = "node_modules/"
        let i = key.LastIndexOf(marker, StringComparison.Ordinal)
        if i < 0 then None
        else Some(key.Substring(i + marker.Length))

let isTopLevelLockPath (key: string) =
    key.StartsWith("node_modules/", StringComparison.Ordinal)
    && key.IndexOf("node_modules/", "node_modules/".Length, StringComparison.Ordinal) < 0

type LockedPackage = {
    Name: string
    Version: string
    DependencyType: string // P, D, or I
}

/// Prefer first-seen DependencyType when the same name+version appears twice (P/D over I).
let mergeLockedPackages (pkgs: LockedPackage list) =
    let rank =
        function
        | "P" -> 0
        | "D" -> 1
        | _ -> 2
    pkgs
    |> List.groupBy (fun p -> p.Name, p.Version)
    |> List.map (fun ((_, _), xs) -> xs |> List.minBy (fun p -> rank p.DependencyType))

/// npm package-lock.json → packages with P/D/I (top-level + lock "dev" flag; nested = I).
let packagesFromNpmLock (json: string) : LockedPackage list =
    use doc = JsonDocument.Parse json
    let root = doc.RootElement
    let mutable packagesEl = Unchecked.defaultof<JsonElement>
    if root.TryGetProperty("packages", &packagesEl) && packagesEl.ValueKind = JsonValueKind.Object then
        packagesEl.EnumerateObject()
        |> Seq.choose (fun prop ->
            match packageNameFromLockPath prop.Name, tryGetString prop.Value "version" with
            | Some name, Some version when not (String.IsNullOrWhiteSpace version) ->
                let depType =
                    if not (isTopLevelLockPath prop.Name) then "I"
                    elif tryGetBool prop.Value "dev" = Some true then "D"
                    else "P"
                Some { Name = name; Version = version; DependencyType = depType }
            | _ -> None)
        |> Seq.toList
        |> mergeLockedPackages
    else
        // lockfileVersion 1: first-level deps = P, nested = I (no reliable D without package.json).
        let rec walk (depsEl: JsonElement) (depth: int) acc =
            depsEl.EnumerateObject()
            |> Seq.fold
                (fun acc prop ->
                    let version = tryGetString prop.Value "version" |> Option.defaultValue ""
                    let acc =
                        if String.IsNullOrWhiteSpace version then acc
                        else
                            {
                                Name = prop.Name
                                Version = version
                                DependencyType = if depth = 0 then "P" else "I"
                            }
                            :: acc
                    match tryGetObject prop.Value "dependencies" with
                    | Some nested -> walk nested (depth + 1) acc
                    | None -> acc)
                acc
        match tryGetObject root "dependencies" with
        | None -> []
        | Some deps -> walk deps 0 [] |> mergeLockedPackages

let packageKey (name: string) = name.ToLowerInvariant()

/// NuGet packages.lock.json — Direct → P, Transitive → I (D not in lockfile).
let packagesFromNugetLock (json: string) : LockedPackage list =
    use doc = JsonDocument.Parse json
    match tryGetObject doc.RootElement "dependencies" with
    | None -> []
    | Some frameworks ->
        frameworks.EnumerateObject()
        |> Seq.collect (fun fw ->
            if fw.Value.ValueKind <> JsonValueKind.Object then Seq.empty
            else
                fw.Value.EnumerateObject()
                |> Seq.choose (fun pkg ->
                    match tryGetString pkg.Value "resolved" with
                    | Some v when not (String.IsNullOrWhiteSpace v) ->
                        let typ =
                            tryGetString pkg.Value "type"
                            |> Option.defaultValue "Transitive"
                        let depType =
                            if typ.Equals("Direct", StringComparison.OrdinalIgnoreCase) then "P"
                            elif typ.Equals("Project", StringComparison.OrdinalIgnoreCase) then "P"
                            else "I"
                        Some { Name = pkg.Name; Version = v; DependencyType = depType }
                    | _ -> None))
        |> Seq.toList
        |> mergeLockedPackages

let isAuditableVersion (version: string) =
    not (String.IsNullOrWhiteSpace version)
    && version.IndexOf("://", StringComparison.Ordinal) < 0
    && not (version.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
    && not (version.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
    && not (version.StartsWith("workspace:", StringComparison.OrdinalIgnoreCase))
    && not (version.StartsWith("project", StringComparison.OrdinalIgnoreCase))

// --- npm OSV -------------------------------------------------------------------

type OsvVuln = {
    Id: string
    SeverityRank: int
    SeverityLabel: string
    Url: string
}

let severityRankFromLabel =
    function
    | "Critical" -> 0
    | "High" -> 1
    | "Moderate" -> 2
    | "Low" -> 3
    | _ -> 4

let parseOsvSeverity (vuln: JsonElement) =
    let fromDb =
        let mutable db = Unchecked.defaultof<JsonElement>
        if vuln.TryGetProperty("database_specific", &db) && db.ValueKind = JsonValueKind.Object then
            let mutable sev = Unchecked.defaultof<JsonElement>
            if db.TryGetProperty("severity", &sev) && sev.ValueKind = JsonValueKind.String then
                Some(sev.GetString().ToUpperInvariant())
            else None
        else None
    let fromSeverityArray =
        let mutable sevArr = Unchecked.defaultof<JsonElement>
        if vuln.TryGetProperty("severity", &sevArr) && sevArr.ValueKind = JsonValueKind.Array then
            sevArr.EnumerateArray()
            |> Seq.choose (fun item ->
                let mutable t = Unchecked.defaultof<JsonElement>
                if item.TryGetProperty("type", &t) && t.ValueKind = JsonValueKind.String then
                    let typ = t.GetString().ToUpperInvariant()
                    if typ.Contains("CRITICAL") then Some "CRITICAL"
                    elif typ.Contains("HIGH") then Some "HIGH"
                    elif typ.Contains("MEDIUM") || typ.Contains("MODERATE") then Some "MODERATE"
                    elif typ.Contains("LOW") then Some "LOW"
                    else None
                else None)
            |> Seq.tryHead
        else None
    match fromDb |> Option.orElse fromSeverityArray |> Option.defaultValue "UNKNOWN" with
    | "CRITICAL" -> "Critical"
    | "HIGH" -> "High"
    | "MODERATE" | "MEDIUM" -> "Moderate"
    | "LOW" -> "Low"
    | _ -> "Unknown"

let osvPrimaryUrl (vuln: JsonElement) (id: string) =
    let mutable refs = Unchecked.defaultof<JsonElement>
    let fromRefs =
        if vuln.TryGetProperty("references", &refs) && refs.ValueKind = JsonValueKind.Array then
            let urls =
                refs.EnumerateArray()
                |> Seq.choose (fun r ->
                    let mutable u = Unchecked.defaultof<JsonElement>
                    let mutable t = Unchecked.defaultof<JsonElement>
                    let url =
                        if r.TryGetProperty("url", &u) && u.ValueKind = JsonValueKind.String then
                            Some(u.GetString())
                        else None
                    let typ =
                        if r.TryGetProperty("type", &t) && t.ValueKind = JsonValueKind.String then
                            t.GetString()
                        else ""
                    url |> Option.map (fun x -> typ, x))
                |> Seq.toList
            urls
            |> List.tryFind (fun (typ, _) -> typ.Equals("ADVISORY", StringComparison.OrdinalIgnoreCase))
            |> Option.map snd
            |> Option.orElse (urls |> List.tryHead |> Option.map snd)
        else None
    match fromRefs with
    | Some u when not (String.IsNullOrWhiteSpace u) -> u
    | _ -> sprintf "https://osv.dev/vulnerability/%s" id

let fetchOsvVuln (client: HttpClient) (id: string) =
    let url = sprintf "https://api.osv.dev/v1/vulns/%s" (Uri.EscapeDataString id)
    let json = client.GetStringAsync(url).GetAwaiter().GetResult()
    use doc = JsonDocument.Parse json
    let root = doc.RootElement
    let label = parseOsvSeverity root
    {
        Id = id
        SeverityRank = severityRankFromLabel label
        SeverityLabel = label
        Url = osvPrimaryUrl root id
    }

let queryOsvBatch (client: HttpClient) (queries: (string * string) list) =
    if queries.IsEmpty then []
    else
        let body =
            let items =
                queries
                |> List.map (fun (name, ver) ->
                    sprintf
                        """{"package":{"name":%s,"ecosystem":"npm"},"version":%s}"""
                        (JsonSerializer.Serialize name)
                        (JsonSerializer.Serialize ver))
            "{\"queries\":[" + String.Join(",", items) + "]}"
        use req = new HttpRequestMessage(HttpMethod.Post, "https://api.osv.dev/v1/querybatch")
        req.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        use resp = client.Send(req)
        let text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if not resp.IsSuccessStatusCode then
            failwith (sprintf "OSV querybatch -> %A%s%s" resp.StatusCode Environment.NewLine text)
        use doc = JsonDocument.Parse text
        let mutable resultsEl = Unchecked.defaultof<JsonElement>
        let resultBlocks =
            if doc.RootElement.TryGetProperty("results", &resultsEl) && resultsEl.ValueKind = JsonValueKind.Array then
                resultsEl.EnumerateArray() |> Seq.toList
            else
                []
        let idLists =
            resultBlocks
            |> List.map (fun block ->
                let mutable vulnsEl = Unchecked.defaultof<JsonElement>
                if block.TryGetProperty("vulns", &vulnsEl) && vulnsEl.ValueKind = JsonValueKind.Array then
                    vulnsEl.EnumerateArray()
                    |> Seq.choose (fun v ->
                        let mutable idEl = Unchecked.defaultof<JsonElement>
                        if v.TryGetProperty("id", &idEl) && idEl.ValueKind = JsonValueKind.String then
                            Some(idEl.GetString())
                        else None)
                    |> Seq.toList
                else
                    [])
        let idLists =
            if idLists.Length >= queries.Length then idLists |> List.take queries.Length
            else idLists @ List.replicate (queries.Length - idLists.Length) []
        let uniqueIds = idLists |> List.collect id |> List.distinct
        let detailById =
            uniqueIds
            |> fun ids ->
                runParallel parallelism ids (fun id ->
                    try Some(id, fetchOsvVuln client id)
                    with _ -> None)
            |> List.choose id
            |> Map.ofList
        idLists
        |> List.map (fun ids ->
            ids
            |> List.choose (fun id -> detailById |> Map.tryFind id)
            |> List.distinctBy (fun v -> v.Id))

let auditNpmPackages (pairs: (string * string) list) =
    let pairs = pairs |> List.filter (fun (_, v) -> isAuditableVersion v) |> List.distinct
    if pairs.IsEmpty then Map.empty
    else
        use handler =
            new HttpClientHandler(
                AutomaticDecompression = (DecompressionMethods.GZip ||| DecompressionMethods.Deflate))
        use client = new HttpClient(handler)
        client.Timeout <- TimeSpan.FromMinutes 5.0
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AzurePackageAuditTool/1.0")
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json")
        pairs
        |> List.chunkBySize 80
        |> List.collect (fun chunk ->
            let hitLists = queryOsvBatch client chunk
            List.zip chunk hitLists)
        |> Map.ofList

// --- nuget VulnerabilityInfo ---------------------------------------------------

type NugetVulnEntry = {
    Severity: int
    Url: string
    Versions: string
}

let nugetSeverityLabel =
    function
    | 0 -> "Low"
    | 1 -> "Moderate"
    | 2 -> "High"
    | 3 -> "Critical"
    | _ -> "Unknown"

let parseVulnPage (json: string) =
    use doc = JsonDocument.Parse json
    let root = doc.RootElement
    if root.ValueKind = JsonValueKind.Array then Map.empty
    elif root.ValueKind <> JsonValueKind.Object then Map.empty
    else
        root.EnumerateObject()
        |> Seq.choose (fun prop ->
            if prop.Value.ValueKind <> JsonValueKind.Array then None
            else
                let entries =
                    prop.Value.EnumerateArray()
                    |> Seq.choose (fun item ->
                        let mutable sevEl = Unchecked.defaultof<JsonElement>
                        let mutable urlEl = Unchecked.defaultof<JsonElement>
                        let mutable verEl = Unchecked.defaultof<JsonElement>
                        let sevOk = item.TryGetProperty("severity", &sevEl)
                        let urlOk = item.TryGetProperty("url", &urlEl) && urlEl.ValueKind = JsonValueKind.String
                        let verOk = item.TryGetProperty("versions", &verEl) && verEl.ValueKind = JsonValueKind.String
                        if not (sevOk && urlOk && verOk) then None
                        else
                            let sev =
                                match sevEl.ValueKind with
                                | JsonValueKind.Number -> sevEl.GetInt32()
                                | JsonValueKind.String ->
                                    match Int32.TryParse(sevEl.GetString()) with
                                    | true, n -> n
                                    | _ -> -1
                                | _ -> -1
                            Some { Severity = sev; Url = urlEl.GetString(); Versions = verEl.GetString() })
                    |> Seq.toList
                if entries.IsEmpty then None else Some(prop.Name.ToLowerInvariant(), entries))
        |> Map.ofSeq

let mergeVulnMaps (maps: Map<string, NugetVulnEntry list> list) =
    maps
    |> List.fold
        (fun (acc: Map<string, NugetVulnEntry list>) m ->
            m
            |> Map.fold
                (fun acc id entries ->
                    let existing = acc |> Map.tryFind id |> Option.defaultValue []
                    acc |> Map.add id (existing @ entries))
                acc)
        Map.empty

let downloadVulnerabilityDb () =
    use handler =
        new HttpClientHandler(
            AutomaticDecompression = (DecompressionMethods.GZip ||| DecompressionMethods.Deflate))
    use client = new HttpClient(handler)
    client.Timeout <- TimeSpan.FromSeconds 90.0
    client.DefaultRequestHeaders.UserAgent.ParseAdd("AzurePackageAuditTool/1.0")
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json")
    let indexJson =
        client.GetStringAsync("https://api.nuget.org/v3/vulnerabilities/index.json").GetAwaiter().GetResult()
    use indexDoc = JsonDocument.Parse indexJson
    let pageUrls =
        if indexDoc.RootElement.ValueKind <> JsonValueKind.Array then []
        else
            indexDoc.RootElement.EnumerateArray()
            |> Seq.choose (fun item ->
                let mutable idEl = Unchecked.defaultof<JsonElement>
                if item.TryGetProperty("@id", &idEl) && idEl.ValueKind = JsonValueKind.String then
                    Some(idEl.GetString())
                else None)
            |> Seq.toList
    let maps =
        pageUrls
        |> fun urls ->
            runParallel parallelism urls (fun url ->
                let pageJson = client.GetStringAsync(url).GetAwaiter().GetResult()
                parseVulnPage pageJson)
    let db = mergeVulnMaps maps
    {| PageCount = pageUrls.Length; PackageIdsWithAdvisories = db.Count |}.Dump("nuget VulnerabilityInfo")
    db

let loadVulnerabilityDbCached () =
    Util.Cache(Func<_>(downloadVulnerabilityDb), key = "nugetVulnerabilityInfoDb")

let versionSatisfiesRange (versionText: string) (rangeText: string) =
    let verCore =
        let i = versionText.IndexOf(',')
        let core = if i < 0 then versionText.Trim() else versionText.Substring(0, i).Trim()
        core.ToLowerInvariant()
    if String.IsNullOrWhiteSpace verCore || String.IsNullOrWhiteSpace rangeText then false
    else
        let mutable nv = Unchecked.defaultof<NuGetVersion>
        if not (NuGetVersion.TryParse(verCore, &nv)) then false
        else
            try
                let range = VersionRange.Parse rangeText
                range.Satisfies nv
            with _ -> false

let matchNugetVulns (db: Map<string, NugetVulnEntry list>) (packageId: string) (versionText: string) =
    match db |> Map.tryFind (packageKey packageId) with
    | None -> []
    | Some entries -> entries |> List.filter (fun e -> versionSatisfiesRange versionText e.Versions)

// --- registry publish dates (temp JSON cache; successful fetches only) ----------

let packageDateCacheDir =
    Path.Combine(Path.GetTempPath(), "linqpad-package-dates")

let npmDateCachePath = Path.Combine(packageDateCacheDir, "npm-registry-times.json")
let nugetDateCachePath = Path.Combine(packageDateCacheDir, "nuget-registry-times.json")

let loadDateCache (path: string) : Map<string, Map<string, DateTimeOffset>> =
    try
        if not (File.Exists path) then Map.empty
        else
            use doc = JsonDocument.Parse(File.ReadAllText path)
            if doc.RootElement.ValueKind <> JsonValueKind.Object then Map.empty
            else
                doc.RootElement.EnumerateObject()
                |> Seq.choose (fun pkg ->
                    if pkg.Value.ValueKind <> JsonValueKind.Object then None
                    else
                        let times =
                            pkg.Value.EnumerateObject()
                            |> Seq.choose (fun ver ->
                                if ver.Value.ValueKind <> JsonValueKind.String then None
                                else
                                    match DateTimeOffset.TryParse(ver.Value.GetString()) with
                                    | true, dt -> Some(ver.Name, dt)
                                    | _ -> None)
                            |> Map.ofSeq
                        Some(pkg.Name, times))
                |> Map.ofSeq
    with _ ->
        Map.empty

let saveDateCache (path: string) (cache: Map<string, Map<string, DateTimeOffset>>) =
    Directory.CreateDirectory packageDateCacheDir |> ignore
    use stream = new MemoryStream()
    use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
    writer.WriteStartObject()
    for KeyValue(pkg, times) in cache |> Seq.sortBy (fun (KeyValue(k, _)) -> k) do
        writer.WritePropertyName pkg
        writer.WriteStartObject()
        for KeyValue(ver, dt) in times |> Seq.sortBy (fun (KeyValue(k, _)) -> k) do
            writer.WriteString(ver, dt.ToString("o"))
        writer.WriteEndObject()
    writer.WriteEndObject()
    writer.Flush()
    File.WriteAllBytes(path, stream.ToArray())

let npmRegistryUrl (name: string) =
    if name.StartsWith("@", StringComparison.Ordinal) then
        let parts = name.Split('/')
        if parts.Length < 2 then sprintf "https://registry.npmjs.org/%s" (Uri.EscapeDataString name)
        else sprintf "https://registry.npmjs.org/%s%%2F%s" parts[0] parts[1]
    else
        sprintf "https://registry.npmjs.org/%s" name

let parseNpmTimeMap (json: string) =
    use doc = JsonDocument.Parse json
    let mutable timeEl = Unchecked.defaultof<JsonElement>
    if not (doc.RootElement.TryGetProperty("time", &timeEl)) || timeEl.ValueKind <> JsonValueKind.Object then
        Map.empty
    else
        timeEl.EnumerateObject()
        |> Seq.choose (fun prop ->
            if prop.Name = "created" || prop.Name = "modified" then None
            elif prop.Value.ValueKind <> JsonValueKind.String then None
            else
                match DateTimeOffset.TryParse(prop.Value.GetString()) with
                | true, dt -> Some(prop.Name, dt)
                | _ -> None)
        |> Map.ofSeq

let versionLookupKey (version: string) =
    let core =
        let i = version.IndexOf(',')
        if i < 0 then version else version.Substring(0, i)
    core.Trim().ToLowerInvariant()

let nugetRegistrationUrl (packageId: string) =
    sprintf "https://api.nuget.org/v3/registration5-gz-semver2/%s/index.json" (packageId.ToLowerInvariant())

let parsePublished (el: JsonElement) =
    let mutable published = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty("published", &published) && published.ValueKind = JsonValueKind.String then
        match DateTimeOffset.TryParse(published.GetString()) with
        | true, dt when dt.Year > 1900 -> Some dt
        | _ -> None
    else None

let collectRegistrationEntries (root: JsonElement) (fetchPage: string -> string option) =
    let acc = ResizeArray<string * DateTimeOffset>()
    let addFromCatalogEntry (catalog: JsonElement) =
        let mutable verEl = Unchecked.defaultof<JsonElement>
        if catalog.TryGetProperty("version", &verEl) && verEl.ValueKind = JsonValueKind.String then
            match parsePublished catalog with
            | Some dt -> acc.Add(verEl.GetString(), dt)
            | None -> ()
    let addFromLeafItem (item: JsonElement) =
        let mutable catalog = Unchecked.defaultof<JsonElement>
        if item.TryGetProperty("catalogEntry", &catalog) && catalog.ValueKind = JsonValueKind.Object then
            addFromCatalogEntry catalog
    let rec walkItems (items: JsonElement) =
        if items.ValueKind = JsonValueKind.Array then
            for item in items.EnumerateArray() do
                let mutable nested = Unchecked.defaultof<JsonElement>
                if item.TryGetProperty("items", &nested) && nested.ValueKind = JsonValueKind.Array then
                    walkItems nested
                else
                    let mutable idEl = Unchecked.defaultof<JsonElement>
                    let hasInlineCatalog =
                        let mutable c = Unchecked.defaultof<JsonElement>
                        item.TryGetProperty("catalogEntry", &c) && c.ValueKind = JsonValueKind.Object
                    if hasInlineCatalog then addFromLeafItem item
                    elif item.TryGetProperty("@id", &idEl) && idEl.ValueKind = JsonValueKind.String then
                        match fetchPage (idEl.GetString()) with
                        | None -> ()
                        | Some pageJson ->
                            use pageDoc = JsonDocument.Parse pageJson
                            let mutable pageItems = Unchecked.defaultof<JsonElement>
                            if pageDoc.RootElement.TryGetProperty("items", &pageItems) then
                                walkItems pageItems
                            else
                                addFromLeafItem pageDoc.RootElement
    let mutable rootItems = Unchecked.defaultof<JsonElement>
    if root.TryGetProperty("items", &rootItems) then
        walkItems rootItems
    acc
    |> Seq.distinctBy (fun (v, _) -> versionLookupKey v)
    |> Seq.map (fun (v, dt) -> versionLookupKey v, dt)
    |> Map.ofSeq

let formatVersionWithRelease (version: string) (released: DateTimeOffset option) =
    match released with
    | None when String.IsNullOrEmpty version -> ""
    | None -> version
    | Some dt -> sprintf "%s, %s" version (dt.ToString("yyyy-MM-dd"))

let fetchNpmTimesForNames (names: string list) =
    let mutable cache = loadDateCache npmDateCachePath
    let misses = names |> List.distinct |> List.filter (fun n -> not (cache.ContainsKey n))
    if not misses.IsEmpty then
        use client = new HttpClient()
        client.Timeout <- TimeSpan.FromSeconds 30.0
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AzurePackageAuditTool/1.0")
        let fetched =
            runParallel parallelism misses (fun name ->
                try
                    let json = client.GetStringAsync(npmRegistryUrl name).GetAwaiter().GetResult()
                    Some(name, parseNpmTimeMap json)
                with _ -> None)
            |> List.choose id
        if not fetched.IsEmpty then
            for name, times in fetched do
                cache <- cache |> Map.add name times
            saveDateCache npmDateCachePath cache
    {|
        CacheHits = names |> List.distinct |> List.filter cache.ContainsKey |> List.length
        Fetched = misses.Length
        CachePath = npmDateCachePath
    |}.Dump("npm publish dates")
    cache

let fetchNugetTimesForNames (names: string list) =
    let mutable cache = loadDateCache nugetDateCachePath
    // nuget cache keys are lowercase package ids
    let keyOf (n: string) = n.ToLowerInvariant()
    let misses =
        names
        |> List.distinctBy keyOf
        |> List.filter (fun n -> not (cache.ContainsKey(keyOf n)))
    if not misses.IsEmpty then
        use handler =
            new HttpClientHandler(
                AutomaticDecompression = (DecompressionMethods.GZip ||| DecompressionMethods.Deflate))
        use client = new HttpClient(handler)
        client.Timeout <- TimeSpan.FromSeconds 45.0
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AzurePackageAuditTool/1.0")
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json")
        let fetched =
            runParallel parallelism misses (fun name ->
                try
                    let indexJson =
                        client.GetStringAsync(nugetRegistrationUrl name).GetAwaiter().GetResult()
                    use doc = JsonDocument.Parse indexJson
                    let fetchPage (url: string) =
                        try Some(client.GetStringAsync(url).GetAwaiter().GetResult())
                        with _ -> None
                    let times = collectRegistrationEntries doc.RootElement fetchPage
                    Some(keyOf name, times)
                with _ -> None)
            |> List.choose id
        if not fetched.IsEmpty then
            for key, times in fetched do
                cache <- cache |> Map.add key times
            saveDateCache nugetDateCachePath cache
    {|
        CacheHits = names |> List.distinctBy keyOf |> List.filter (fun n -> cache.ContainsKey(keyOf n)) |> List.length
        Fetched = misses.Length
        CachePath = nugetDateCachePath
    |}.Dump("nuget publish dates")
    cache

let lookupNpmRelease (cache: Map<string, Map<string, DateTimeOffset>>) (name: string) (version: string) =
    cache
    |> Map.tryFind name
    |> Option.bind (fun times -> times |> Map.tryFind version)

let lookupNugetRelease (cache: Map<string, Map<string, DateTimeOffset>>) (name: string) (version: string) =
    cache
    |> Map.tryFind (name.ToLowerInvariant())
    |> Option.bind (fun times -> times |> Map.tryFind (versionLookupKey version))

// --- findings ------------------------------------------------------------------

type Finding = {
    Kind: string
    Org: string
    Project: string
    Repo: string
    Branch: string
    LockPath: string
    Lockfile: AuditLockfile
    DependencyType: string
    Package: string
    Version: string
    CveSeverity: string
    CveCount: int
    CveUrls: string list
}

let cveUrlLinks (urls: string list) =
    if urls.IsEmpty then null
    else
        urls
        |> List.map (fun u ->
            let label =
                try
                    let uri = Uri u
                    let segs = uri.AbsolutePath.Trim('/').Split('/')
                    if segs.Length > 0 && not (String.IsNullOrWhiteSpace segs.[segs.Length - 1]) then
                        segs.[segs.Length - 1]
                    else
                        uri.Host
                with _ -> u
            Hyperlinq(u, label) :> obj)
        |> fun xs -> Util.HorizontalRun(true, Array.ofList xs)

let isWarningFinding (f: Finding) =
    match f.CveSeverity with
    | "Moderate" -> true
    | "High" when f.DependencyType = "D" -> true
    | _ -> false

/// Critical, or High on non-dev (P/I) deps — dull red.
let isAlertFinding (f: Finding) =
    match f.CveSeverity with
    | "Critical" -> true
    | "High" when f.DependencyType <> "D" -> true
    | _ -> false

[<Literal>]
let AlertHighlight = "#8B3A3A" // dull red

[<Literal>]
let WarningHighlight = "#9A7B2F" // muted mustard yellow (not bright)

type FindingDumpRow
    (
        kind: string,
        org: string,
        project: string,
        repo: string,
        branch: string,
        lockPath: Hyperlinq,
        dependencyType: string,
        packageName: string,
        version: string,
        cveSeverity: string,
        cveCount: int,
        cveUrls: obj
    ) =
    member _.Kind = kind
    member _.Org = org
    member _.Project = project
    member _.Repo = repo
    member _.Branch = branch
    member _.LockPath = lockPath
    member _.DependencyType = dependencyType
    member _.Package = packageName
    member _.Version = version
    member _.CveSeverity = cveSeverity
    member _.CveCount = cveCount
    member _.CveUrls = cveUrls

let toDumpRow (f: Finding) =
    FindingDumpRow(
        f.Kind,
        f.Org,
        f.Project,
        f.Repo,
        f.Branch,
        lockPathLink f.Lockfile,
        f.DependencyType,
        f.Package,
        f.Version,
        f.CveSeverity,
        f.CveCount,
        cveUrlLinks f.CveUrls)

// --- main ----------------------------------------------------------------------

use client = makeClient ()

let allIncludedRepos = manageRepos client targets

let pickFocusedRepos (repos: AuditRepo list) =
    if repos.Length <= 1 then
        repos
    else
        let suggestions = "all" :: (repos |> List.map repoKey)
        let get, save = createUserPref "azurePackageAuditFocusRepo" id id
        let seed =
            match get () with
            | Some s when suggestions |> List.exists (fun x -> String.Equals(x, s, StringComparison.OrdinalIgnoreCase)) ->
                s
            | _ -> "all"
        let entered =
            promptLineWithSuggestions
                "Focus repo (all = every included repo; autocomplete from included)"
                seed
                suggestions
        let pick = if String.IsNullOrWhiteSpace entered then "all" else entered.Trim()
        save (Some pick)
        if pick.Equals("all", StringComparison.OrdinalIgnoreCase) then
            repos
        else
            let byKey =
                repos
                |> List.filter (fun r -> String.Equals(repoKey r, pick, StringComparison.OrdinalIgnoreCase))
            if not byKey.IsEmpty then byKey
            else
                let byName =
                    repos
                    |> List.filter (fun r -> String.Equals(r.Repo, pick, StringComparison.OrdinalIgnoreCase))
                match byName with
                | [ one ] -> [ one ]
                | many when not many.IsEmpty ->
                    sprintf
                        "focus '%s' matched %d included repos — using all matches"
                        pick
                        many.Length
                    |> fun s -> s.Dump()
                    many
                | _ ->
                    sprintf "focus '%s' not found in included repos — auditing all %d" pick repos.Length
                    |> fun s -> s.Dump()
                    repos

let repos = pickFocusedRepos allIncludedRepos

{|
    Focus =
        if repos.Length = allIncludedRepos.Length && allIncludedRepos.Length <> 1 then "all"
        else repos |> List.map repoKey |> fun xs -> String.Join("; ", xs)
    IncludedRepos = allIncludedRepos.Length
    FocusedRepos = repos.Length
|}.Dump("repo focus")

let lockfiles = manageLockfiles client repos

{|
    RepoCount = repos.Length
    LockfileCount = lockfiles.Length
    NpmLockfiles = lockfiles |> List.filter (fun l -> l.Kind = "npm") |> List.length
    NugetLockfiles = lockfiles |> List.filter (fun l -> l.Kind = "nuget") |> List.length
|}.Dump("audit scope")

type LockDownload =
    | LockOk of AuditLockfile * string
    | LockErr of AuditLockfile * string

let downloads =
    runParallel parallelism lockfiles (fun l ->
        try LockOk(l, downloadItemContent client l)
        with ex -> LockErr(l, ex.Message))

let downloadErrors =
    downloads
    |> List.choose (function LockErr(l, msg) -> Some(sprintf "%s — %s" (lockKey l) msg) | _ -> None)

if not downloadErrors.IsEmpty then
    downloadErrors.Dump("lockfile download errors")

let okDownloads = downloads |> List.choose (function LockOk(l, c) -> Some(l, c) | _ -> None)

let npmPkgsByLock =
    okDownloads
    |> List.filter (fun (l, _) -> l.Kind = "npm")
    |> List.map (fun (l, json) ->
        try l, packagesFromNpmLock json
        with ex ->
            sprintf "%s parse error: %s" (lockKey l) ex.Message |> fun s -> s.Dump()
            l, [])

let nugetPkgsByLock =
    okDownloads
    |> List.filter (fun (l, _) -> l.Kind = "nuget")
    |> List.map (fun (l, json) ->
        try l, packagesFromNugetLock json
        with ex ->
            sprintf "%s parse error: %s" (lockKey l) ex.Message |> fun s -> s.Dump()
            l, [])

{|
    NpmPackageVersions = npmPkgsByLock |> List.sumBy (fun (_, ps) -> ps.Length)
    NugetPackageVersions = nugetPkgsByLock |> List.sumBy (fun (_, ps) -> ps.Length)
    NpmP = npmPkgsByLock |> List.sumBy (fun (_, ps) -> ps |> List.filter (fun p -> p.DependencyType = "P") |> List.length)
    NpmD = npmPkgsByLock |> List.sumBy (fun (_, ps) -> ps |> List.filter (fun p -> p.DependencyType = "D") |> List.length)
    NpmI = npmPkgsByLock |> List.sumBy (fun (_, ps) -> ps |> List.filter (fun p -> p.DependencyType = "I") |> List.length)
    NugetP = nugetPkgsByLock |> List.sumBy (fun (_, ps) -> ps |> List.filter (fun p -> p.DependencyType = "P") |> List.length)
    NugetI = nugetPkgsByLock |> List.sumBy (fun (_, ps) -> ps |> List.filter (fun p -> p.DependencyType = "I") |> List.length)
|}.Dump("parsed packages (P/D/I)")

let allNpmPairs =
    npmPkgsByLock
    |> List.collect snd
    |> List.filter (fun p -> isAuditableVersion p.Version)
    |> List.map (fun p -> p.Name, p.Version)
    |> List.distinct

let npmHitsByPair =
    if allNpmPairs.IsEmpty then Map.empty
    else
        try auditNpmPackages allNpmPairs
        with ex ->
            [ ex.Message ].Dump("OSV audit errors")
            Map.empty

let nugetDb =
    if nugetPkgsByLock |> List.exists (fun (_, ps) -> not ps.IsEmpty) then
        try Some(loadVulnerabilityDbCached ())
        with ex ->
            [ ex.Message ].Dump("VulnerabilityInfo fetch errors")
            None
    else None

let npmFindingsRaw =
    npmPkgsByLock
    |> List.collect (fun (l, pkgs) ->
        pkgs
        |> List.choose (fun p ->
            if not (isAuditableVersion p.Version) then None
            else
                let hits = npmHitsByPair |> Map.tryFind (p.Name, p.Version) |> Option.defaultValue []
                if hits.IsEmpty then None
                else
                    let sev = (hits |> List.minBy (fun h -> h.SeverityRank)).SeverityLabel
                    Some
                        {
                            Kind = "npm"
                            Org = l.Org
                            Project = l.Project
                            Repo = l.Repo
                            Branch = l.Branch
                            LockPath = l.Path
                            Lockfile = l
                            DependencyType = p.DependencyType
                            Package = p.Name
                            Version = p.Version
                            CveSeverity = sev
                            CveCount = hits.Length
                            CveUrls =
                                hits
                                |> List.map (fun h -> h.Url)
                                |> List.distinct
                                |> List.sort
                        }))

let nugetFindingsRaw =
    match nugetDb with
    | None -> []
    | Some db ->
        nugetPkgsByLock
        |> List.collect (fun (l, pkgs) ->
            pkgs
            |> List.choose (fun p ->
                if not (isAuditableVersion p.Version) then None
                else
                    let hits = matchNugetVulns db p.Name p.Version
                    if hits.IsEmpty then None
                    else
                        let sev = nugetSeverityLabel (hits |> List.map (fun h -> h.Severity) |> List.max)
                        Some
                            {
                                Kind = "nuget"
                                Org = l.Org
                                Project = l.Project
                                Repo = l.Repo
                                Branch = l.Branch
                                LockPath = l.Path
                                Lockfile = l
                                DependencyType = p.DependencyType
                                Package = p.Name
                                Version = p.Version
                                CveSeverity = sev
                                CveCount = hits.Length
                                CveUrls =
                                    hits
                                    |> List.map (fun h -> h.Url)
                                    |> List.distinct
                                    |> List.sort
                            }))

let npmDateCache =
    let names = npmFindingsRaw |> List.map (fun f -> f.Package) |> List.distinct
    if names.IsEmpty then Map.empty else fetchNpmTimesForNames names

let nugetDateCache =
    let names = nugetFindingsRaw |> List.map (fun f -> f.Package) |> List.distinct
    if names.IsEmpty then Map.empty else fetchNugetTimesForNames names

let withPublishDates (f: Finding) =
    let released =
        match f.Kind with
        | "npm" -> lookupNpmRelease npmDateCache f.Package f.Version
        | "nuget" -> lookupNugetRelease nugetDateCache f.Package f.Version
        | _ -> None
    { f with Version = formatVersionWithRelease f.Version released }

let highlightSortRank (f: Finding) =
    if isAlertFinding f then 0 // red band first
    elif isWarningFinding f then 1 // yellow band
    else 2

let findings =
    (npmFindingsRaw @ nugetFindingsRaw)
    |> List.map withPublishDates
    |> List.sortBy (fun f ->
        let depRank =
            match f.DependencyType with
            | "P" -> 0
            | "D" -> 1
            | _ -> 2
        highlightSortRank f,
        severityRankFromLabel f.CveSeverity,
        f.Kind,
        depRank,
        f.Org.ToLowerInvariant(),
        f.Project.ToLowerInvariant(),
        f.Repo.ToLowerInvariant(),
        f.Package.ToLowerInvariant())

{|
    VulnerablePackages = findings.Length
    TotalCveHits = findings |> List.sumBy (fun f -> f.CveCount)
    Critical = findings |> List.filter (fun f -> f.CveSeverity = "Critical") |> List.length
    High = findings |> List.filter (fun f -> f.CveSeverity = "High") |> List.length
    Moderate = findings |> List.filter (fun f -> f.CveSeverity = "Moderate") |> List.length
    Low = findings |> List.filter (fun f -> f.CveSeverity = "Low") |> List.length
    ByDependencyType =
        findings
        |> List.groupBy (fun f -> f.DependencyType)
        |> List.map (fun (t, xs) -> t, xs.Length)
        |> List.sortBy fst
    LockfilesAudited = okDownloads.Length
    LockfilesFailed = downloadErrors.Length
|}.Dump("CVE summary")

findings
|> List.map (fun f ->
    let row = toDumpRow f
    // Single HighlightIf wrapper per row (nested wrappers dump as collapsible objects).
    if isAlertFinding f then Util.HighlightIf(true, AlertHighlight, row)
    elif isWarningFinding f then Util.HighlightIf(true, WarningHighlight, row)
    else Util.HighlightIf(false, WarningHighlight, row))
|> fun rows ->
    rows.Dump(sprintf "vulnerable packages across audited lockfiles — %d" findings.Length)
