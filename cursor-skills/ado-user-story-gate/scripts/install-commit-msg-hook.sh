#!/bin/sh
# Install (or refresh) the ado-user-story-gate commit-msg hook into the current repo.
# Run from a git repo root, or pass the repo path as $1.
#
# Writes LF-normalized content so the hook runs reliably under sh.

set -e

REPO_ROOT="${1:-.}"
HOOK_SRC="$(CDPATH= cd -- "$(dirname "$0")" && pwd)/commit-msg"
HOOK_DEST="$REPO_ROOT/.git/hooks/commit-msg"

if [ ! -d "$REPO_ROOT/.git" ]; then
  echo "install-commit-msg-hook: not a git repo: $REPO_ROOT" >&2
  exit 1
fi

if [ ! -f "$HOOK_SRC" ]; then
  echo "install-commit-msg-hook: missing source hook: $HOOK_SRC" >&2
  exit 1
fi

mkdir -p "$(dirname "$HOOK_DEST")"
# Strip CR so Windows-edited sources still install as LF.
tr -d '\r' < "$HOOK_SRC" > "$HOOK_DEST"
chmod +x "$HOOK_DEST"
echo "Installed commit-msg hook -> $HOOK_DEST"
