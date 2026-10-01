<Query Kind="FSharpProgram">
  <IncludeUncapsulator>false</IncludeUncapsulator>
</Query>

// Azure DevOps repo configuration audit — unprotected defaults, policy drift, force-push ACL risk.
//
// Shared prefs (same as AzurePackageAuditTool.linq):
//   adoTargetsJson              — [{ "Org","Project" }]
//   adoPat                      — Util.GetPassword
//                                   Code (Read) + Policy (Read); Identity (Read) + Security for force-push ACLs
//   azurePackageAuditSpiderOrgs — remembered org name(s) for spider
//   azurePackageAuditReposJson  — included repos [{ Org, Project, Repo, RepoId, Branch }]
//
// Script prefs:
//   azureRepoConfigAuditParallelism — concurrent ADO calls (default 14)
//
// Flow:
//   0) Resolve org(s) → spider projects → repos (autocomplete catalog)
//   1) Manage org/project targets (adoTargetsJson)
//   2) Manage included repos (shared prefs; v=available; all=scan every discovered this run, don't save)
//   3) For each selected repo: default-branch policies + Force push ACLs (repo + default branch)
//   4) Dump unprotected / weak / force-push risk, then matrix + config-signature groups

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open LINQPad.Controls

// --- prefs ---------------------------------------------------------------------

[<Literal>]
let PrefTargetsJson = "adoTargetsJson"

[<Literal>]
let PrefReposJson = "azurePackageAuditReposJson"

[<Literal>]
let PrefParallelism = "azureRepoConfigAuditParallelism"

[<Literal>]
let PrefPat = "adoPat"

[<Literal>]
let PrefSpiderOrgs = "azurePackageAuditSpiderOrgs"

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

let promptLineWithSuggestions (title: string) (seed: string) (suggestions: string seq) =
    let entered = Util.ReadLine(title, seed, suggestions)
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

let distinctSorted (xs: string list) =
    xs
    |> List.filter (fun s -> not (String.IsNullOrWhiteSpace s))
    |> List.distinctBy (fun s -> s.ToLowerInvariant())
    |> List.sortBy (fun s -> s.ToLowerInvariant())

// --- shared included repos -----------------------------------------------------

[<CLIMutable>]
type AuditRepo = {
    Org: string
    Project: string
    Repo: string
    RepoId: string
    Branch: string
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

let normalizeBranch (b: string) =
    if String.IsNullOrWhiteSpace b then "main"
    elif b.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase) then
        b.Substring("refs/heads/".Length)
    else
        b.Trim()

let branchRef (branch: string) =
    let b = normalizeBranch branch
    if b.StartsWith("refs/", StringComparison.OrdinalIgnoreCase) then b
    else "refs/heads/" + b

let repoPoliciesUrl (org: string) (project: string) (repo: string) (branch: string) =
    sprintf
        "https://dev.azure.com/%s/%s/_git/%s?version=GB%s&_a=history"
        (Uri.EscapeDataString org)
        (Uri.EscapeDataString project)
        (Uri.EscapeDataString repo)
        (Uri.EscapeDataString (normalizeBranch branch))

let repoSettingsPoliciesUrl (org: string) (project: string) (repoId: string) =
    sprintf
        "https://dev.azure.com/%s/%s/_settings/repositories?repo=%s&_a=policiesMid"
        (Uri.EscapeDataString org)
        (Uri.EscapeDataString project)
        (Uri.EscapeDataString repoId)

let repoSettingsPermissionsUrl (org: string) (project: string) (repoId: string) =
    sprintf
        "https://dev.azure.com/%s/%s/_settings/repositories?repo=%s&_a=permissionsMid"
        (Uri.EscapeDataString org)
        (Uri.EscapeDataString project)
        (Uri.EscapeDataString repoId)

// --- HTTP / ADO ----------------------------------------------------------------

let pat =
    let p = Util.GetPassword PrefPat
    if String.IsNullOrWhiteSpace p then
        invalidOp
            (sprintf
                "Set Util password key '%s' (Code Read, Policy Read; Identity Read + Security to read force-push ACLs)."
                PrefPat)
    p.Trim()

let parallelism =
    let get, save = createUserPref PrefParallelism int string
    let seed = get () |> Option.defaultValue 14
    let entered =
        promptLine "ADO scan parallelism (concurrent requests)" (string seed)
    match Int32.TryParse entered with
    | true, n when n > 0 ->
        save (Some n)
        n
    | _ ->
        save (Some 14)
        14

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

let propBool (el: JsonElement) (name: string) (fallback: bool) =
    match el.TryGetProperty name with
    | true, p when p.ValueKind = JsonValueKind.True -> true
    | true, p when p.ValueKind = JsonValueKind.False -> false
    | _ -> fallback

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

// --- PAT access catalog (orgs → projects → repos) ------------------------------

type AdoAccessCatalog = {
    Orgs: string list
    Projects: AdoTarget list
    Repos: AuditRepo list
    OrgSource: string
    Errors: string list
}

let listOrganizations (client: HttpClient) : Result<string list, string> =
    try
        use profileDoc =
            readJson client HttpMethod.Get "https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version=7.1" None
        let memberId = propStr profileDoc.RootElement "id"
        if String.IsNullOrWhiteSpace memberId then
            Error "profile/me returned no id"
        else
            let url =
                sprintf
                    "https://app.vssps.visualstudio.com/_apis/accounts?memberId=%s&api-version=7.1"
                    (Uri.EscapeDataString memberId)
            use accountsDoc = readJson client HttpMethod.Get url None
            let orgs =
                propArr accountsDoc.RootElement "value"
                |> List.choose (fun a ->
                    let name = propStr a "accountName"
                    if isNull name then None else Some name)
                |> distinctSorted
            Ok orgs
    with ex ->
        let msg = ex.Message
        if
            msg.IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0
            || msg.IndexOf("401", StringComparison.OrdinalIgnoreCase) >= 0
        then
            Error "profile"
        else
            Error msg

let parseOrgList (text: string) =
    if String.IsNullOrWhiteSpace text then []
    else
        text.Split([| ','; ';'; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun s -> s.Trim())
        |> Array.toList
        |> distinctSorted

let loadSpiderOrgsPref () =
    let raw = Util.LoadString PrefSpiderOrgs
    parseOrgList (if isNull raw then "" else raw)

let saveSpiderOrgsPref (orgs: string list) =
    let cleaned = distinctSorted orgs
    Util.SaveString(
        PrefSpiderOrgs,
        if cleaned.IsEmpty then null else String.Join(", ", cleaned))
    cleaned

let promptOrgsToSpider (seedOrgs: string list) =
    let suggestions = distinctSorted (seedOrgs @ loadSpiderOrgsPref ())
    let seedText = String.Join(", ", suggestions)
    let entered =
        if suggestions.IsEmpty then
            promptLine
                "ADO organization name(s) to spider for projects/repos (comma-separated)"
                ""
        else
            promptLineWithSuggestions
                "ADO organization name(s) to spider (comma-separated; Enter keeps suggestions)"
                seedText
                suggestions
    let chosen =
        let parsed = parseOrgList entered
        if parsed.IsEmpty then suggestions else parsed
    if chosen.IsEmpty then
        invalidOp "At least one organization name is required to spider projects/repos."
    saveSpiderOrgsPref chosen

let listProjectsForOrg (client: HttpClient) (org: string) : AdoTarget list =
    let url =
        sprintf "https://dev.azure.com/%s/_apis/projects?api-version=7.1&$top=1000" (Uri.EscapeDataString org)
    use doc = readJson client HttpMethod.Get url None
    propArr doc.RootElement "value"
    |> List.choose (fun p ->
        let name = propStr p "name"
        if isNull name then None
        else Some { Org = org; Project = name })

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

let spiderAccessCatalog (client: HttpClient) (seedTargets: AdoTarget list) : AdoAccessCatalog =
    let mutable errors: string list = []
    let seedOrgs =
        distinctSorted (
            (seedTargets |> List.map (fun t -> t.Org))
            @ loadSpiderOrgsPref ())
    let orgs, orgSource =
        match listOrganizations client with
        | Ok xs when not xs.IsEmpty ->
            saveSpiderOrgsPref xs |> ignore
            xs, sprintf "ADO profile (%d)" xs.Length
        | Ok _ ->
            let chosen = promptOrgsToSpider seedOrgs
            chosen, sprintf "prompted (profile returned none) — %s" (String.Join(", ", chosen))
        | Error _ ->
            let chosen = promptOrgsToSpider seedOrgs
            chosen, sprintf "prompted (profile list unavailable) — %s" (String.Join(", ", chosen))

    sprintf "spidering projects/repos for: %s" (String.Join(", ", orgs))
    |> fun s -> s.Dump("access discovery")

    let projectResults =
        runParallel parallelism orgs (fun org ->
            try Ok(listProjectsForOrg client org)
            with ex -> Error(sprintf "%s projects: %s" org ex.Message))
    errors <-
        (projectResults |> List.choose (function Error e -> Some e | _ -> None))
        @ errors
    let projects =
        projectResults
        |> List.choose (function Ok xs -> Some xs | _ -> None)
        |> List.concat
        |> List.distinctBy (fun t -> t.Org.ToLowerInvariant(), t.Project.ToLowerInvariant())
        |> List.sortBy (fun t -> t.Org.ToLowerInvariant(), t.Project.ToLowerInvariant())

    let projectsForRepos =
        if projects.IsEmpty then seedTargets
        else projects

    let repoResults =
        runParallel parallelism projectsForRepos (fun t ->
            try Ok(listReposForTarget client t)
            with ex -> Error(sprintf "%s repos: %s" (targetKey t) ex.Message))
    errors <-
        (repoResults |> List.choose (function Error e -> Some e | _ -> None))
        @ errors
    let repos =
        repoResults
        |> List.choose (function Ok xs -> Some xs | _ -> None)
        |> List.concat
        |> List.distinctBy (fun r ->
            r.Org.ToLowerInvariant(), r.Project.ToLowerInvariant(), r.Repo.ToLowerInvariant())
        |> List.sortBy (fun r ->
            r.Org.ToLowerInvariant(), r.Project.ToLowerInvariant(), r.Repo.ToLowerInvariant())

    {
        Orgs = orgs
        Projects = projects
        Repos = repos
        OrgSource = orgSource
        Errors = errors |> List.rev
    }

let manageTargets (catalog: AdoAccessCatalog) =
    let mutable targets = loadTargets ()
    {|
        OrgSource = catalog.OrgSource
        Orgs = catalog.Orgs
        CatalogProjects = catalog.Projects.Length
        CatalogRepos = catalog.Repos.Length
        StoredTargets = targets.Length
    |}.Dump("access catalog")
    if not catalog.Errors.IsEmpty then
        catalog.Errors.Dump("access catalog errors")
    targets
    |> List.mapi (fun i t -> {| Index = i + 1; Org = t.Org; Project = t.Project |})
    |> fun rows -> rows.Dump(sprintf "stored %s (%d)" PrefTargetsJson rows.Length)

    let dumpTargets heading =
        targets
        |> List.mapi (fun i t -> {| Index = i + 1; Org = t.Org; Project = t.Project |})
        |> fun rows -> rows.Dump(sprintf "%s (%d)" heading rows.Length)

    let orgSuggestions () =
        distinctSorted (catalog.Orgs @ (targets |> List.map (fun t -> t.Org)))

    let projectSuggestions org =
        let fromCatalog =
            catalog.Projects
            |> List.filter (fun t ->
                String.IsNullOrWhiteSpace org
                || String.Equals(t.Org, org, StringComparison.OrdinalIgnoreCase))
            |> List.map (fun t -> t.Project)
        let fromStored =
            targets
            |> List.filter (fun t ->
                String.IsNullOrWhiteSpace org
                || String.Equals(t.Org, org, StringComparison.OrdinalIgnoreCase))
            |> List.map (fun t -> t.Project)
        distinctSorted (fromCatalog @ fromStored)

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
            let orgs = orgSuggestions ()
            let orgSeed = orgs |> List.tryHead |> Option.defaultValue ""
            let org =
                if orgs.IsEmpty then promptLine "Organization name" orgSeed
                else promptLineWithSuggestions "Organization (start typing for autocomplete)" orgSeed orgs
            let projects = projectSuggestions org
            let projectSeed = projects |> List.tryHead |> Option.defaultValue ""
            let project =
                if projects.IsEmpty then promptLine "Project name" projectSeed
                else
                    promptLineWithSuggestions
                        "Project (start typing for autocomplete)"
                        projectSeed
                        projects
            if String.IsNullOrWhiteSpace org || String.IsNullOrWhiteSpace project then
                "add cancelled (org and project required)".Dump()
            else
                targets <- saveTargets ({ Org = org; Project = project } :: targets)
                dumpTargets "stored targets now"
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
                dumpTargets "stored targets now"
            | _ -> "invalid index".Dump()
            loop ()
        | _ ->
            "unknown command — use Enter / a / d / c".Dump()
            loop ()

    let final = loop ()
    if final.IsEmpty then
        invalidOp (sprintf "No targets in %s. Use 'a' to add org+project pairs." PrefTargetsJson)
    final

// --- discover / manage repos (shared PrefReposJson) -----------------------------

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

/// SavedIncluded = PrefReposJson; ScanAll = ephemeral discovered set for this run only.
type RepoScanChoice =
    | SavedIncluded of AuditRepo list
    | ScanAll of AuditRepo list

let manageRepos (client: HttpClient) (ts: AdoTarget list) (catalog: AdoAccessCatalog) =
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
                AvailableCount = available.Length
                Included = repoRows included
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

    let dumpAvailableExpanded () =
        let rows = repoRows available
        rows.Dump(sprintf "available repos expanded (%d) — use i=<index> to include" rows.Length)
        status <- sprintf "dumped %d available repo(s) expanded" available.Length
        refreshView ()

    let rec loop () : RepoScanChoice =
        let cmd =
            promptLine
                "Repos: [Enter]=continue saved, all=scan every discovered (don't save), v=dump available, i=include#, x=exclude#, a=add, r=rediscover, c=clear"
                ""
            |> fun s -> s.ToLowerInvariant()
        match cmd with
        | "" | "q" | "done" | "continue" -> SavedIncluded included
        | "all" | "*" | "scanall" | "scan-all" | "everything" ->
            if discovered.IsEmpty then
                status <- "scan-all cancelled — no discovered repos (try r=rediscover)"
                refreshView ()
                loop ()
            else
                status <-
                    sprintf
                        "scan-all: %d discovered repo(s) this run — saved included list unchanged (%d)"
                        discovered.Length
                        included.Length
                refreshView ()
                ScanAll discovered
        | "v" | "view" | "available" | "avail" ->
            dumpAvailableExpanded ()
            loop ()
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
                @ (catalog.Projects |> List.map (fun t -> t.Org, t.Project))
                @ (discovered |> List.map (fun r -> r.Org, r.Project))
                @ (included |> List.map (fun r -> r.Org, r.Project))
            let orgSuggestions =
                distinctSorted (catalog.Orgs @ (known |> List.map fst))
            let orgSeed =
                ts
                |> List.tryHead
                |> Option.map (fun t -> t.Org)
                |> Option.defaultValue (orgSuggestions |> List.tryHead |> Option.defaultValue "")
            let org =
                if orgSuggestions.IsEmpty then promptLine "Organization" orgSeed
                else promptLineWithSuggestions "Organization (start typing for autocomplete)" orgSeed orgSuggestions
            let projectSuggestions =
                distinctSorted (
                    known
                    |> List.filter (fun (o, _) ->
                        String.IsNullOrWhiteSpace org
                        || String.Equals(o, org, StringComparison.OrdinalIgnoreCase))
                    |> List.map snd)
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
                let fromCatalog =
                    catalog.Repos
                    |> List.filter (fun r ->
                        String.Equals(r.Org, org, StringComparison.OrdinalIgnoreCase)
                        && String.Equals(r.Project, project, StringComparison.OrdinalIgnoreCase))
                    |> List.map (fun r -> r.Repo)
                let fromKnown =
                    discovered @ included
                    |> List.filter (fun r ->
                        String.Equals(r.Org, org, StringComparison.OrdinalIgnoreCase)
                        && String.Equals(r.Project, project, StringComparison.OrdinalIgnoreCase))
                    |> List.map (fun r -> r.Repo)
                let fromAdo =
                    if String.IsNullOrWhiteSpace org || String.IsNullOrWhiteSpace project then []
                    elif not fromCatalog.IsEmpty then []
                    else
                        status <- sprintf "loading repos for %s / %s…" org project
                        refreshView ()
                        try
                            listReposForTarget client { Org = org; Project = project }
                            |> List.map (fun r -> r.Repo)
                        with _ -> []
                distinctSorted (fromCatalog @ fromKnown @ fromAdo)
            let repo =
                if repoSuggestions.IsEmpty then promptLine "Repo name" ""
                else promptLineWithSuggestions "Repo name (start typing for autocomplete)" "" repoSuggestions
            if String.IsNullOrWhiteSpace org || String.IsNullOrWhiteSpace project || String.IsNullOrWhiteSpace repo then
                status <- "add cancelled — org, project, and repo required"
                refreshView ()
            else
                let matched =
                    catalog.Repos @ discovered
                    |> List.tryFind (fun r ->
                        String.Equals(r.Org, org, StringComparison.OrdinalIgnoreCase)
                        && String.Equals(r.Project, project, StringComparison.OrdinalIgnoreCase)
                        && String.Equals(r.Repo, repo, StringComparison.OrdinalIgnoreCase))
                let repoId, defaultBranch =
                    match matched with
                    | Some r -> r.RepoId, r.Branch
                    | None ->
                        try
                            match
                                listReposForTarget client { Org = org; Project = project }
                                |> List.tryFind (fun r ->
                                    String.Equals(r.Repo, repo, StringComparison.OrdinalIgnoreCase))
                            with
                            | Some r -> r.RepoId, r.Branch
                            | None -> "", "main"
                        with _ -> "", "main"
                let added =
                    {
                        Org = org
                        Project = project
                        Repo = repo
                        RepoId = repoId
                        Branch = normalizeBranch defaultBranch
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
                            sprintf "included %s (default branch %s)" (repoKey added) added.Branch
                refreshView ()
            loop ()
        | _ ->
            status <- "unknown command — use Enter / all / v / i / x / a / r / c"
            refreshView ()
            loop ()

    match loop () with
    | SavedIncluded final when final.IsEmpty ->
        invalidOp
            "No repos included. Use 'i'/'a' to save repos, or 'all' to scan every discovered repo this run without saving."
    | SavedIncluded final ->
        status <- sprintf "continuing with %d saved included repo(s)" final.Length
        refreshView ()
        {| Mode = "saved included"; RepoCount = final.Length |}.Dump("repo scan selection")
        final
    | ScanAll final ->
        status <-
            sprintf
                "continuing with scan-all: %d discovered repo(s) — %s unchanged (%d saved)"
                final.Length
                PrefReposJson
                included.Length
        refreshView ()
        {|
            Mode = "scan-all (ephemeral)"
            RepoCount = final.Length
            SavedIncludedUnchanged = included.Length
        |}.Dump("repo scan selection")
        final

// --- branch policy audit -------------------------------------------------------

// Well-known Azure DevOps policy type ids (displayName is preferred when present).
[<Literal>]
let PolicyTypeMinReviewers = "fa4e907d-c16b-4a4c-9dfa-490d34c749b0"

[<Literal>]
let PolicyTypeRequiredReviewers = "fd2167ab-b0be-447a-8ec8-39368250530e"

[<Literal>]
let PolicyTypeBuildValidation = "0609b952-1397-4640-95ec-e00f7e793785"

[<Literal>]
let PolicyTypeWorkItemLinking = "40e92b44-2fe1-4dd6-b3d8-74a9c21d0c6e"

[<Literal>]
let PolicyTypeCommentRequirements = "c6a1889d-b943-4856-b76f-9e46bb6b0df2"

[<Literal>]
let PolicyTypeMergeStrategy = "fa4e907d-c16b-4a4c-9dfa-4916e5d171ab"

[<Literal>]
let GitReposSecurityNamespace = "2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87"

[<Literal>]
let ForcePushPermissionBit = 8

type PolicyHit = {
    Id: int
    TypeId: string
    TypeName: string
    IsEnabled: bool
    IsBlocking: bool
}

type ForcePushAce = {
    Descriptor: string
    DisplayName: string
    Scope: string // project | repo | branch
    Allow: bool
    Deny: bool
}

type RepoConfigRow = {
    Org: string
    Project: string
    Repo: string
    RepoId: string
    DefaultBranch: string
    EnabledPolicyCount: int
    BlockingPolicyCount: int
    HasMinReviewers: bool
    HasRequiredReviewers: bool
    HasBuildValidation: bool
    HasWorkItemLinking: bool
    HasCommentRequirements: bool
    HasMergeStrategy: bool
    PolicyTypes: string
    ConfigSignature: string
    Unprotected: bool
    WeakProtection: bool
    ForcePushAllowedCount: int
    ForcePushBroadAllow: bool
    ForcePushAllowedIdentities: string
    ForcePushRisk: bool
    ForcePushRiskReason: string
    Notes: string
    PoliciesUrl: Hyperlinq
    PermissionsUrl: Hyperlinq
    RepoUrl: Hyperlinq
}

let projectIdCache = System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
let identityNameCache = System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase)

let getProjectId (client: HttpClient) (org: string) (project: string) =
    let key = sprintf "%s|%s" org project
    match projectIdCache.TryGetValue key with
    | true, id -> id
    | _ ->
        let url =
            sprintf
                "https://dev.azure.com/%s/_apis/projects/%s?api-version=7.1"
                (Uri.EscapeDataString org)
                (Uri.EscapeDataString project)
        use doc = readJson client HttpMethod.Get url None
        let id = propStr doc.RootElement "id"
        if String.IsNullOrWhiteSpace id then
            failwith (sprintf "project id missing for %s/%s" org project)
        projectIdCache.[key] <- id
        id

let resolveIdentityDisplayName (client: HttpClient) (org: string) (descriptor: string) =
    if String.IsNullOrWhiteSpace descriptor then descriptor
    else
        match identityNameCache.TryGetValue descriptor with
        | true, name -> name
        | _ ->
            let name =
                try
                    let url =
                        sprintf
                            "https://vssps.dev.azure.com/%s/_apis/identities?descriptors=%s&api-version=7.1"
                            (Uri.EscapeDataString org)
                            (Uri.EscapeDataString descriptor)
                    use doc = readJson client HttpMethod.Get url None
                    match propArr doc.RootElement "value" with
                    | h :: _ ->
                        let display = propStr h "providerDisplayName"
                        let custom = propStr h "customDisplayName"
                        let principal =
                            match h.TryGetProperty "properties" with
                            | true, props ->
                                match props.TryGetProperty "Account" with
                                | true, acct when acct.ValueKind = JsonValueKind.Object ->
                                    propStr acct "$value"
                                | _ -> null
                            | _ -> null
                        [ custom; display; principal ]
                        |> List.choose (fun s ->
                            if String.IsNullOrWhiteSpace s then None else Some s)
                        |> List.tryHead
                        |> Option.defaultValue descriptor
                    | _ -> descriptor
                with _ -> descriptor
            identityNameCache.[descriptor] <- name
            name

let branchTokenUtf16Hex (branch: string) =
    Encoding.Unicode.GetBytes(normalizeBranch branch)
    |> Array.map (fun b -> b.ToString("x2"))
    |> String.concat ""

let gitSecurityTokens (projectId: string) (repoId: string) (branch: string) =
    let projectToken = sprintf "repoV2/%s" projectId
    let repoToken = sprintf "repoV2/%s/%s" projectId repoId
    let branchToken =
        sprintf "repoV2/%s/%s/refs/heads/%s/" projectId repoId (branchTokenUtf16Hex branch)
    [
        "project", projectToken
        "repo", repoToken
        "branch", branchToken
    ]

let hasPermissionBit (mask: int) (bit: int) = (mask &&& bit) <> 0

let readAceMasks (ace: JsonElement) =
    let allow =
        match ace.TryGetProperty "allow" with
        | true, p when p.ValueKind = JsonValueKind.Number -> p.GetInt32()
        | _ -> 0
    let deny =
        match ace.TryGetProperty "deny" with
        | true, p when p.ValueKind = JsonValueKind.Number -> p.GetInt32()
        | _ -> 0
    let effAllow, effDeny =
        match ace.TryGetProperty "extendedInfo" with
        | true, ext ->
            let ea =
                match ext.TryGetProperty "effectiveAllow" with
                | true, p when p.ValueKind = JsonValueKind.Number -> p.GetInt32()
                | _ -> allow
            let ed =
                match ext.TryGetProperty "effectiveDeny" with
                | true, p when p.ValueKind = JsonValueKind.Number -> p.GetInt32()
                | _ -> deny
            ea, ed
        | _ -> allow, deny
    effAllow, effDeny

let listForcePushAcesForToken (client: HttpClient) (org: string) (token: string) (scope: string) =
    let url =
        sprintf
            "https://dev.azure.com/%s/_apis/accesscontrollists/%s?token=%s&includeExtendedInfo=true&api-version=7.1"
            (Uri.EscapeDataString org)
            GitReposSecurityNamespace
            (Uri.EscapeDataString token)
    use doc = readJson client HttpMethod.Get url None
    propArr doc.RootElement "value"
    |> List.collect (fun acl ->
        let aces =
            match acl.TryGetProperty "acesDictionary" with
            | true, map when map.ValueKind = JsonValueKind.Object ->
                map.EnumerateObject()
                |> Seq.map (fun prop -> prop.Value)
                |> Seq.toList
            | true, arr when arr.ValueKind = JsonValueKind.Array ->
                arr.EnumerateArray() |> Seq.toList
            | _ -> []
        aces
        |> List.choose (fun ace ->
            let descriptor = propStr ace "descriptor" |> fun s -> if isNull s then "" else s
            let effAllow, effDeny = readAceMasks ace
            let allowFp = hasPermissionBit effAllow ForcePushPermissionBit
            let denyFp = hasPermissionBit effDeny ForcePushPermissionBit
            if not allowFp && not denyFp then None
            else
                Some
                    {
                        Descriptor = descriptor
                        DisplayName = resolveIdentityDisplayName client org descriptor
                        Scope = scope
                        Allow = allowFp && not denyFp
                        Deny = denyFp
                    }))

let isBroadIdentity (displayName: string) =
    let n = if isNull displayName then "" else displayName
    [
        "Contributors"
        "Readers"
        "Project Valid Users"
        "Project Collection Valid Users"
        "Everyone"
        "Endpoint Creators"
    ]
    |> List.exists (fun needle -> n.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)

let isLikelyAdminIdentity (displayName: string) =
    let n = if isNull displayName then "" else displayName
    [
        "Project Collection Administrators"
        "Project Administrators"
        "Team Foundation Administrators"
    ]
    |> List.exists (fun needle -> n.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)

let auditForcePush
    (client: HttpClient)
    (org: string)
    (project: string)
    (repoId: string)
    (branch: string)
    : Result<ForcePushAce list * string, string> =
    try
        let projectId = getProjectId client org project
        let tokens = gitSecurityTokens projectId repoId branch
        let aces =
            tokens
            |> List.collect (fun (scope, token) ->
                try listForcePushAcesForToken client org token scope
                with ex ->
                    // Branch token may 404 when no explicit ACL exists; treat as empty.
                    let msg = ex.Message
                    if
                        msg.IndexOf("NotFound", StringComparison.OrdinalIgnoreCase) >= 0
                        || msg.IndexOf("404", StringComparison.OrdinalIgnoreCase) >= 0
                    then
                        []
                    else
                        raise ex)
            // Prefer most-specific scope when same descriptor appears multiple times.
            |> List.groupBy (fun a -> a.Descriptor.ToLowerInvariant())
            |> List.map (fun (_, group) ->
                let rank =
                    function
                    | "branch" -> 3
                    | "repo" -> 2
                    | _ -> 1
                group |> List.maxBy (fun a -> rank a.Scope))
        Ok(aces, "")
    with ex ->
        Error ex.Message

let getRepoDefaultBranch (client: HttpClient) (r: AuditRepo) =
    let idOrName = if String.IsNullOrWhiteSpace r.RepoId then r.Repo else r.RepoId
    let url =
        sprintf
            "%s/_apis/git/repositories/%s?api-version=7.1"
            (baseUrl r.Org r.Project)
            (Uri.EscapeDataString idOrName)
    use doc = readJson client HttpMethod.Get url None
    let id = propStr doc.RootElement "id" |> fun s -> if String.IsNullOrWhiteSpace s then r.RepoId else s
    let name = propStr doc.RootElement "name" |> fun s -> if String.IsNullOrWhiteSpace s then r.Repo else s
    let branch =
        propStr doc.RootElement "defaultBranch"
        |> fun b -> if isNull b then normalizeBranch r.Branch else normalizeBranch b
    {| RepoId = id; Repo = name; DefaultBranch = branch |}

let listPoliciesForBranch (client: HttpClient) (org: string) (project: string) (repoId: string) (branch: string) =
    let url =
        sprintf
            "%s/_apis/git/policy/configurations?repositoryId=%s&refName=%s&api-version=7.1"
            (baseUrl org project)
            (Uri.EscapeDataString repoId)
            (Uri.EscapeDataString (branchRef branch))
    use doc = readJson client HttpMethod.Get url None
    propArr doc.RootElement "value"
    |> List.choose (fun p ->
        let isDeleted = propBool p "isDeleted" false
        if isDeleted then None
        else
            let id =
                match p.TryGetProperty "id" with
                | true, x when x.ValueKind = JsonValueKind.Number -> x.GetInt32()
                | _ -> 0
            let typeId, typeName =
                match p.TryGetProperty "type" with
                | true, t ->
                    (propStr t "id" |> fun s -> if isNull s then "" else s),
                    (propStr t "displayName" |> fun s -> if isNull s then "" else s)
                | _ -> "", ""
            Some
                {
                    Id = id
                    TypeId = typeId
                    TypeName =
                        if String.IsNullOrWhiteSpace typeName then typeId
                        else typeName
                    IsEnabled = propBool p "isEnabled" false
                    IsBlocking = propBool p "isBlocking" false
                })

let hasPolicyType (policies: PolicyHit list) (typeId: string) (nameContains: string) =
    policies
    |> List.exists (fun p ->
        p.IsEnabled
        && (
            String.Equals(p.TypeId, typeId, StringComparison.OrdinalIgnoreCase)
            || p.TypeName.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0))

let auditRepoConfig (client: HttpClient) (r: AuditRepo) : Result<RepoConfigRow, string> =
    try
        let meta = getRepoDefaultBranch client r
        if String.IsNullOrWhiteSpace meta.RepoId then
            Error(sprintf "%s/%s/%s: missing RepoId" r.Org r.Project r.Repo)
        else
            let policies = listPoliciesForBranch client r.Org r.Project meta.RepoId meta.DefaultBranch
            let enabled = policies |> List.filter (fun p -> p.IsEnabled)
            let blocking = enabled |> List.filter (fun p -> p.IsBlocking)
            let hasMin = hasPolicyType enabled PolicyTypeMinReviewers "Minimum number of reviewers"
            let hasReq = hasPolicyType enabled PolicyTypeRequiredReviewers "Required reviewers"
            let hasBuild = hasPolicyType enabled PolicyTypeBuildValidation "Build"
            let hasWi = hasPolicyType enabled PolicyTypeWorkItemLinking "Work item"
            let hasComment = hasPolicyType enabled PolicyTypeCommentRequirements "Comment"
            let hasMerge = hasPolicyType enabled PolicyTypeMergeStrategy "Merge strategy"
            let typeNames =
                enabled
                |> List.map (fun p -> p.TypeName)
                |> List.distinctBy (fun s -> s.ToLowerInvariant())
                |> List.sortBy (fun s -> s.ToLowerInvariant())

            let forceAces, forceNote =
                match auditForcePush client r.Org r.Project meta.RepoId meta.DefaultBranch with
                | Ok(aces, _) -> aces, ""
                | Error msg -> [], sprintf "force-push ACL read failed: %s" msg

            let allowed =
                forceAces
                |> List.filter (fun a -> a.Allow)
                |> List.sortBy (fun a -> a.DisplayName.ToLowerInvariant())
            let broadAllows = allowed |> List.filter (fun a -> isBroadIdentity a.DisplayName)
            let nonAdminAllows =
                allowed
                |> List.filter (fun a ->
                    not (isBroadIdentity a.DisplayName) && not (isLikelyAdminIdentity a.DisplayName))
            let forcePushRisk = not broadAllows.IsEmpty || not nonAdminAllows.IsEmpty
            let forcePushRiskReason =
                match not broadAllows.IsEmpty, not nonAdminAllows.IsEmpty with
                | true, true -> "broad group + non-admin identity"
                | true, false -> "broad group"
                | false, true -> "non-admin identity"
                | false, false -> ""

            let signatureParts =
                [
                    if hasMin then "min-reviewers"
                    if hasReq then "required-reviewers"
                    if hasBuild then "build"
                    if hasWi then "work-item"
                    if hasComment then "comments"
                    if hasMerge then "merge-strategy"
                    if forcePushRisk then "force-push-open"
                    elif allowed.IsEmpty && String.IsNullOrWhiteSpace forceNote then "force-push-locked"
                    for extra in typeNames do
                        let known =
                            extra.IndexOf("reviewer", StringComparison.OrdinalIgnoreCase) >= 0
                            || extra.IndexOf("Build", StringComparison.OrdinalIgnoreCase) >= 0
                            || extra.IndexOf("Work item", StringComparison.OrdinalIgnoreCase) >= 0
                            || extra.IndexOf("Comment", StringComparison.OrdinalIgnoreCase) >= 0
                            || extra.IndexOf("Merge", StringComparison.OrdinalIgnoreCase) >= 0
                        if not known then extra.ToLowerInvariant()
                ]
                |> List.distinct
                |> List.sort
            let signature =
                if signatureParts.IsEmpty then "(none)"
                else String.Join(" + ", signatureParts)
            let unprotected = enabled.IsEmpty
            let weak =
                not unprotected
                && not hasMin
                && not hasReq
            let allowedSummary =
                allowed
                |> List.map (fun a -> sprintf "%s [%s]" a.DisplayName a.Scope)
                |> fun xs -> String.Join("; ", xs)
            // Force-push detail lives in ForcePushAllowedIdentities / ForcePushBroadAllow columns.
            let notes =
                [
                    if unprotected then "no enabled branch policies on default branch"
                    if weak then "policies present but no reviewer requirement"
                    if blocking.IsEmpty && not enabled.IsEmpty then "enabled policies are non-blocking"
                    if not (String.IsNullOrWhiteSpace forceNote) then forceNote
                ]
                |> fun xs -> String.Join("; ", xs)
            Ok
                {
                    Org = r.Org
                    Project = r.Project
                    Repo = meta.Repo
                    RepoId = meta.RepoId
                    DefaultBranch = meta.DefaultBranch
                    EnabledPolicyCount = enabled.Length
                    BlockingPolicyCount = blocking.Length
                    HasMinReviewers = hasMin
                    HasRequiredReviewers = hasReq
                    HasBuildValidation = hasBuild
                    HasWorkItemLinking = hasWi
                    HasCommentRequirements = hasComment
                    HasMergeStrategy = hasMerge
                    PolicyTypes = String.Join(", ", typeNames)
                    ConfigSignature = signature
                    Unprotected = unprotected
                    WeakProtection = weak
                    ForcePushAllowedCount = allowed.Length
                    ForcePushBroadAllow = not broadAllows.IsEmpty
                    ForcePushAllowedIdentities = allowedSummary
                    ForcePushRisk = forcePushRisk
                    ForcePushRiskReason = forcePushRiskReason
                    Notes = notes
                    PoliciesUrl =
                        Hyperlinq(repoSettingsPoliciesUrl r.Org r.Project meta.RepoId, "policies")
                    PermissionsUrl =
                        Hyperlinq(repoSettingsPermissionsUrl r.Org r.Project meta.RepoId, "permissions")
                    RepoUrl =
                        Hyperlinq(repoPoliciesUrl r.Org r.Project meta.Repo meta.DefaultBranch, meta.DefaultBranch)
                }
    with ex ->
        Error(sprintf "%s/%s/%s: %s" r.Org r.Project r.Repo ex.Message)

let auditAllRepos (client: HttpClient) (repos: AuditRepo list) =
    let results =
        runParallel parallelism repos (fun r -> auditRepoConfig client r)
    let errors = results |> List.choose (function Error e -> Some e | _ -> None)
    let rows =
        results
        |> List.choose (function Ok x -> Some x | _ -> None)
        |> List.sortBy (fun r ->
            (if r.ForcePushRisk then 0 elif r.Unprotected then 1 elif r.WeakProtection then 2 else 3),
            r.Org.ToLowerInvariant(),
            r.Project.ToLowerInvariant(),
            r.Repo.ToLowerInvariant())
    rows, errors

// --- main ----------------------------------------------------------------------

use client = makeClient ()

"resolving org(s), then spidering projects → repos via PAT…".Dump("access discovery")
let accessCatalog = spiderAccessCatalog client (loadTargets ())
let targets = manageTargets accessCatalog

{|
    TargetCount = targets.Length
    Parallelism = parallelism
    CatalogOrgs = accessCatalog.Orgs.Length
    CatalogProjects = accessCatalog.Projects.Length
    CatalogRepos = accessCatalog.Repos.Length
    PrefKeys =
        [|
            PrefTargetsJson
            PrefReposJson
            PrefParallelism
            PrefSpiderOrgs
        |]
|}.Dump("AzureRepoConfigAudit")

let includedRepos = manageRepos client targets accessCatalog

"scanning default-branch policies + force-push ACLs…".Dump("policy audit")
let rows, auditErrors = auditAllRepos client includedRepos
if not auditErrors.IsEmpty then
    auditErrors.Dump("policy audit errors")

let unprotected = rows |> List.filter (fun r -> r.Unprotected)
let weak = rows |> List.filter (fun r -> r.WeakProtection)
let forcePushRiskRows = rows |> List.filter (fun r -> r.ForcePushRisk)
let protectedOk =
    rows
    |> List.filter (fun r -> not r.Unprotected && not r.WeakProtection && not r.ForcePushRisk)

{|
    Scanned = rows.Length
    UnprotectedDefaultBranch = unprotected.Length
    WeakProtection = weak.Length
    ForcePushRisk = forcePushRiskRows.Length
    LooksProtected = protectedOk.Length
    Errors = auditErrors.Length
    DistinctConfigSignatures =
        rows
        |> List.map (fun r -> r.ConfigSignature)
        |> List.distinct
        |> List.length
|}.Dump("summary")

forcePushRiskRows
|> List.map (fun r ->
    {|
        Org = r.Org
        Project = r.Project
        Repo = r.Repo
        DefaultBranch = r.RepoUrl
        RiskReason = r.ForcePushRiskReason
        BroadGroupAllow = r.ForcePushBroadAllow
        AllowedCount = r.ForcePushAllowedCount
        AllowedIdentities = r.ForcePushAllowedIdentities
        Notes = r.Notes
        Permissions = r.PermissionsUrl
    |})
|> fun xs -> xs.Dump(sprintf "FORCE PUSH RISK (%d)" xs.Length)

unprotected
|> List.map (fun r ->
    {|
        Org = r.Org
        Project = r.Project
        Repo = r.Repo
        DefaultBranch = r.RepoUrl
        ForcePushRisk = r.ForcePushRisk
        Notes = r.Notes
        Policies = r.PoliciesUrl
        Permissions = r.PermissionsUrl
    |})
|> fun xs -> xs.Dump(sprintf "UNPROTECTED default branches (%d) — no enabled policies" xs.Length)

weak
|> List.map (fun r ->
    {|
        Org = r.Org
        Project = r.Project
        Repo = r.Repo
        DefaultBranch = r.RepoUrl
        PolicyTypes = r.PolicyTypes
        ForcePushRisk = r.ForcePushRisk
        Notes = r.Notes
        Policies = r.PoliciesUrl
        Permissions = r.PermissionsUrl
    |})
|> fun xs -> xs.Dump(sprintf "weak protection (%d) — policies but no reviewer requirement" xs.Length)

rows
|> List.map (fun r ->
    {|
        Risk =
            if r.ForcePushRisk then sprintf "FORCE-PUSH (%s)" r.ForcePushRiskReason
            elif r.Unprotected then "UNPROTECTED"
            elif r.WeakProtection then "weak"
            else "ok"
        Org = r.Org
        Project = r.Project
        Repo = r.Repo
        DefaultBranch = r.RepoUrl
        EnabledPolicies = r.EnabledPolicyCount
        BlockingPolicies = r.BlockingPolicyCount
        MinReviewers = r.HasMinReviewers
        RequiredReviewers = r.HasRequiredReviewers
        BuildValidation = r.HasBuildValidation
        WorkItemLinking = r.HasWorkItemLinking
        CommentRequirements = r.HasCommentRequirements
        MergeStrategy = r.HasMergeStrategy
        ForcePushAllowedCount = r.ForcePushAllowedCount
        ForcePushBroadAllow = r.ForcePushBroadAllow
        ForcePushAllowedIdentities = r.ForcePushAllowedIdentities
        ForcePushRiskReason = r.ForcePushRiskReason
        PolicyTypes = r.PolicyTypes
        ConfigSignature = r.ConfigSignature
        Notes = r.Notes
        Policies = r.PoliciesUrl
        Permissions = r.PermissionsUrl
    |})
|> fun xs -> xs.Dump(sprintf "all repos policy + force-push matrix (%d)" xs.Length)

rows
|> List.groupBy (fun r -> r.ConfigSignature)
|> List.map (fun (configSig, group) ->
    {|
        ConfigSignature = configSig
        RepoCount = group.Length
        Unprotected = group |> List.filter (fun r -> r.Unprotected) |> List.length
        ForcePushRisk = group |> List.filter (fun r -> r.ForcePushRisk) |> List.length
        Repos =
            group
            |> List.map (fun r -> sprintf "%s/%s/%s" r.Org r.Project r.Repo)
            |> List.sort
            |> fun xs -> String.Join("; ", xs)
    |})
|> List.sortByDescending (fun g -> g.RepoCount)
|> fun xs -> xs.Dump(sprintf "config signature groups (%d) — policy drift view" xs.Length)

sprintf
    "done — %d force-push risk, %d unprotected, %d weak, %d ok (of %d scanned)"
    forcePushRiskRows.Length
    unprotected.Length
    weak.Length
    protectedOk.Length
    rows.Length
|> fun s -> s.Dump("AzureRepoConfigAudit complete")
