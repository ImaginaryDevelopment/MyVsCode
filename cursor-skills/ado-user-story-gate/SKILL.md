---
name: ado-user-story-gate
description: >-
  Gates code changes behind an Azure DevOps User Story ID and categorized
  title (Feature, TechnicalDebt, Documentation, or Security), provides a commit
  message that includes #ID and a category keyword (anywhere in the message) for
  auto-linking, checks for the commit-msg hook, and installs it when missing.
  Use when editing code in Azure DevOps repos (dev.azure.com remotes), before
  making code changes, or when preparing a commit that should link to a user
  story. Do not use for MyDev or repos without an Azure DevOps remote.
---

# Azure DevOps User Story Gate

## Scope check (do this first)

1. If the workspace path is `MyDev` or `c:\dev\MyDev` (or otherwise this machine's MyDev repo) → this skill does **not** apply. Proceed normally with no user story requirement.
2. Otherwise run `git remote -v`. If no remote URL contains `dev.azure.com` → this skill does **not** apply.
3. Only enforce the gate below for Azure Repos remotes.

## Hook check (Azure repos only)

After confirming this is an Azure DevOps repo, check for `.git/hooks/commit-msg`.

If the hook is **missing** or does **not** contain the marker text `Azure DevOps user-story commit-msg hook`:

1. Warn the user once that this Azure repo lacks the ado-user-story-gate `commit-msg` hook
2. Create a todo (or checklist item) to install it
3. Offer to install by running the skill's installer from the target repo root (preferred):

```powershell
powershell -File "$env:USERPROFILE\.cursor\skills\ado-user-story-gate\scripts\install-commit-msg-hook.ps1"
```

```bash
sh "$HOME/.cursor/skills/ado-user-story-gate/scripts/install-commit-msg-hook.sh"
```

Do not block the user's coding session solely because the hook is missing — warn + todo, then continue the user-story gate.

## Before any code change

You MUST have both from the user before editing, creating, or deleting files:

- **User Story ID** — numeric only (e.g. `12345`)
- **User Story title** — exact title; must **contain** one of these category keywords somewhere (not necessarily at the start):
  - `Feature`
  - `TechnicalDebt`
  - `Documentation`
  - `Security`

Valid title examples:

- `Feature Implement shift swap validation`
- `Feature: Implement shift swap validation`
- `Implement shift swap validation — Feature`
- `TechnicalDebt Remove unused WinForms path`
- `Remove unused WinForms path (TechnicalDebt)`
- `Documentation Update deployment runbook`
- `Update deployment runbook Documentation`
- `Security Rotate service credentials`
- `Harden token storage — Security`

If the ID is missing, the title is missing, or the title does not contain one of the three category keywords:

1. Ask in one short question (include the three allowed category keywords)
2. **Stop** — do not edit, create, or delete files until both are valid

Do not invent IDs or titles. Do not look up Azure DevOps unless the user asks you to.

Hold the provided ID and title for the rest of the session's work on this change.

## After the change is complete

Give the user copy-paste commit text. Preferred shape (still works; `#ID` and category may appear anywhere):

```
#<ID> <Title>
```

Examples:

```
#12345 Feature Implement shift swap validation
```

```
#12345 TechnicalDebt: Remove unused WinForms path
```

```
Fix login race — TechnicalDebt #12345
```

```
Documentation: update runbook (#12345)
```

```
#12345 Security Rotate service credentials
```

Rules for the commit text:

- Include `#` + the numeric ID **somewhere** in the message so Azure Repos auto-links the commit to the work item
- Include one category keyword (`Feature` | `TechnicalDebt` | `Documentation` | `Security`) **somewhere** in the message (order relative to the ID does not matter)
- Include the full title the user supplied so a wrong ID is still auditable
- Use only the ID and title the user supplied — never invent or substitute
- Remind them: the commit must be **pushed** for Azure to create the work-item link
- If the user asks you to create the git commit, use a message that satisfies the above
- The `commit-msg` hook rejects messages that lack `#<ID>` or a category keyword anywhere (Merge/Revert exempt)

## Utility scripts

**commit-msg** (install into `.git/hooks/commit-msg`): Rejects commits whose message does not contain both `#<ID>` and one of `Feature` | `TechnicalDebt` | `Documentation` | `Security` somewhere in the body. Execute/install; do not rewrite casually. Body extraction keeps `#12345 …` work-item lines and skips git template comments (`# ` / `#Please…`).

**install-commit-msg-hook.ps1** / **install-commit-msg-hook.sh**: Copy `scripts/commit-msg` into the target repo's `.git/hooks/commit-msg` with LF line endings. Prefer the `.ps1` installer on Windows. Run from (or pass) the target repo root.

## When the user corrects the story

If they supply a new ID and/or title mid-session, replace the held values and use the updated pair for any later commit message. Re-validate that the title still contains `Feature`, `TechnicalDebt`, `Documentation`, or `Security`.
