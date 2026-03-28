#!/bin/bash
set -e

if [ "$GODMODE_FORCE" != "true" ]; then
    cd "$GODMODE_PROJECT_PATH"
    if [ -n "$(git status --porcelain)" ]; then
        echo "ERROR: Project has uncommitted changes. Commit and push before deleting." >&2
        exit 1
    fi

    BRANCH=$(git rev-parse --abbrev-ref HEAD)
    UPSTREAM=$(git rev-parse --abbrev-ref --symbolic-full-name "@{u}" 2>/dev/null || true)
    if [ -n "$UPSTREAM" ]; then
        UNPUSHED=$(git log "$UPSTREAM..HEAD" --oneline 2>/dev/null || true)
        if [ -n "$UNPUSHED" ]; then
            echo "ERROR: Branch '$BRANCH' has unpushed commits." >&2
            exit 1
        fi
    fi
fi

echo "Removing project directory..."
rm -rf "$GODMODE_PROJECT_PATH"
echo "Teardown complete."
