#!/bin/bash
# Remove git worktree and local branch (safe by default)
set -e

REPO_NAME=$(basename "{{repoUrl}}" .git)
BARE_PATH="$GODMODE_ROOT_PATH/$REPO_NAME.git"
PROJECT_PATH="$GODMODE_PROJECT_PATH"

if [ "$GODMODE_FORCE" != "true" ]; then
    if [ -n "$(git -C "$PROJECT_PATH" status --porcelain)" ]; then
        echo "ERROR: Project has uncommitted changes. Commit and push before deleting." >&2
        exit 1
    fi

    BRANCH=$(git -C "$PROJECT_PATH" rev-parse --abbrev-ref HEAD)
    UPSTREAM=$(git -C "$PROJECT_PATH" rev-parse --abbrev-ref --symbolic-full-name "@{u}" 2>/dev/null || true)

    if [ -z "$UPSTREAM" ]; then
        UNPUSHED=$(git -C "$PROJECT_PATH" log "origin/{{defaultBranch}}..HEAD" --oneline 2>/dev/null || true)
        if [ -n "$UNPUSHED" ]; then
            echo "ERROR: Branch '$BRANCH' has commits not pushed to any remote." >&2
            echo "$UNPUSHED" >&2
            exit 1
        fi
    else
        UNPUSHED=$(git -C "$PROJECT_PATH" log "$UPSTREAM..HEAD" --oneline 2>/dev/null || true)
        if [ -n "$UNPUSHED" ]; then
            echo "ERROR: Branch '$BRANCH' has unpushed commits." >&2
            echo "$UNPUSHED" >&2
            exit 1
        fi
    fi

    echo "All changes are pushed. Removing worktree..."
else
    echo "Force delete requested. Removing worktree..."
    BRANCH=$(git -C "$PROJECT_PATH" rev-parse --abbrev-ref HEAD 2>/dev/null || echo "")
fi

git -C "$BARE_PATH" worktree remove "$PROJECT_PATH" --force

if [[ "$BRANCH" == feature/* ]]; then
    echo "Deleting local branch '$BRANCH'..."
    git -C "$BARE_PATH" branch -D "$BRANCH" 2>/dev/null || true
fi

echo "Teardown complete."
