#!/bin/bash
# Per-project: remove git worktree and local branch (only if all changes are pushed)
set -e

BARE_PATH="$GODMODE_ROOT_PATH/GodMode.git"
PROJECT_PATH="$GODMODE_PROJECT_PATH"
PROJECT_ID="$GODMODE_PROJECT_ID"

if [ "$GODMODE_FORCE" != "true" ]; then
    # Check for uncommitted changes
    if [ -n "$(git -C "$PROJECT_PATH" status --porcelain)" ]; then
        echo "ERROR: Project has uncommitted changes. Commit and push before deleting." >&2
        exit 1
    fi

    # Check for unpushed commits on the current branch
    BRANCH=$(git -C "$PROJECT_PATH" rev-parse --abbrev-ref HEAD)
    UPSTREAM=$(git -C "$PROJECT_PATH" rev-parse --abbrev-ref --symbolic-full-name "@{u}" 2>/dev/null || true)

    if [ -z "$UPSTREAM" ]; then
        # No upstream set — check if branch has any commits beyond origin/master
        UNPUSHED=$(git -C "$PROJECT_PATH" log origin/master..HEAD --oneline 2>/dev/null || true)
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
    echo "Force delete requested. Skipping git checks. Removing worktree..."
    BRANCH=$(git -C "$PROJECT_PATH" rev-parse --abbrev-ref HEAD)
fi

# Remove the worktree via the bare repo
git -C "$BARE_PATH" worktree remove "$PROJECT_PATH" --force

# Delete the local branch if it was auto-created (project/*)
if [[ "$BRANCH" == project/* ]] || [[ "$BRANCH" == issue-* ]]; then
    echo "Deleting local branch '$BRANCH'..."
    git -C "$BARE_PATH" branch -D "$BRANCH" 2>/dev/null || true
fi

echo "Teardown complete."
