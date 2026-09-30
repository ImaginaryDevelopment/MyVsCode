# Install (or refresh) the ado-user-story-gate commit-msg hook into a git repo.
# Usage: .\install-commit-msg-hook.ps1 [[-RepoRoot] <path>]
#
# Writes LF-normalized content so Git for Windows sh runs the hook reliably.

param(
    [string]$RepoRoot = (Get-Location).Path
)

$ErrorActionPreference = "Stop"

$hookSrc = Join-Path $PSScriptRoot "commit-msg"
$gitDir = Join-Path $RepoRoot ".git"
$hookDest = Join-Path $gitDir "hooks\commit-msg"

if (-not (Test-Path $gitDir -PathType Container)) {
    Write-Error "Not a git repo: $RepoRoot"
}

if (-not (Test-Path $hookSrc -PathType Leaf)) {
    Write-Error "Missing source hook: $hookSrc"
}

New-Item -ItemType Directory -Force -Path (Split-Path $hookDest) | Out-Null

# Normalize to LF: Copy-Item can leave CRLF on Windows and break /bin/sh parsing.
$text = [IO.File]::ReadAllText($hookSrc) -replace "`r`n", "`n" -replace "`r", "`n"
[IO.File]::WriteAllBytes($hookDest, [Text.Encoding]::UTF8.GetBytes($text))

Write-Host "Installed commit-msg hook -> $hookDest"
