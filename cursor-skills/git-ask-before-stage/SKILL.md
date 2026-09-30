---
name: git-ask-before-stage
description: >-
  Requires asking before staging git changes. Use when running git add, git
  stage, restaging after edits, preparing a commit, or when the agent might
  otherwise stage files without an explicit user request.
---

# Git — ask before staging

please do not stage anything without asking if it is ok

## Rule

Do **not** run `git add`, `git stage`, or otherwise stage/restage files unless the user has explicitly said it is OK to stage (for this change or in general for the current task).

Before staging:

1. Tell the user which paths you would stage.
2. Ask if it is OK.
3. Wait for confirmation.
4. Only then run the stage command.

## Exceptions

None by default. Even after editing a file that was already staged, ask before restaging. Committing when the user asked to commit may still require asking about unstaged paths you want to include first.
