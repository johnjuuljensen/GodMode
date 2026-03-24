#!/bin/bash
# Per-project: create a git worktree from the bare repo
set -e

REPO_NAME=$(basename "{{repoUrl}}" .git)
BARE_PATH="$GODMODE_ROOT_PATH/$REPO_NAME.git"
PROJECT_PATH="$GODMODE_PROJECT_PATH"

# Fetch latest
git -C "$BARE_PATH" fetch origin

# Determine branch: use existing if specified, otherwise create new
if [ -n "$GODMODE_INPUT_BRANCH" ]; then
    echo "Checking out existing branch '$GODMODE_INPUT_BRANCH'..."
    git -C "$BARE_PATH" worktree add "$PROJECT_PATH" "$GODMODE_INPUT_BRANCH"
else
    BRANCH="project/$GODMODE_PROJECT_ID"
    echo "Creating new branch '$BRANCH' from origin/master..."
    git -C "$BARE_PATH" worktree add "$PROJECT_PATH" -b "$BRANCH" origin/master
fi

echo "Worktree ready at $PROJECT_PATH"
