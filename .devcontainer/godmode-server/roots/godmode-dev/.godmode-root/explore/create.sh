#!/bin/bash
# Per-project: create a git worktree for exploration (read-only-ish, from master)
set -e

BARE_PATH="$GODMODE_ROOT_PATH/GodMode.git"
PROJECT_PATH="$GODMODE_PROJECT_PATH"

# Fetch latest
git -C "$BARE_PATH" fetch origin

BRANCH="explore/$GODMODE_PROJECT_ID"
echo "Creating branch '$BRANCH' from origin/master..."
git -C "$BARE_PATH" worktree add "$PROJECT_PATH" -b "$BRANCH" origin/master

echo "Worktree ready at $PROJECT_PATH"
