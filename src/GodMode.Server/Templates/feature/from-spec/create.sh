#!/bin/bash
# Create a feature worktree from the bare repo (spec-driven)
set -e

REPO_NAME=$(basename "{{repoUrl}}" .git)
BARE_PATH="$GODMODE_ROOT_PATH/$REPO_NAME.git"
PROJECT_PATH="$GODMODE_PROJECT_PATH"
DEFAULT_BRANCH="{{defaultBranch}}"

# Fetch latest
git -C "$BARE_PATH" fetch origin

# Create feature branch
BRANCH="feature/$GODMODE_PROJECT_ID"
echo "Creating worktree with branch '$BRANCH' from 'origin/$DEFAULT_BRANCH'..."
git -C "$BARE_PATH" worktree add "$PROJECT_PATH" -b "$BRANCH" "origin/$DEFAULT_BRANCH"

echo "Worktree ready at $PROJECT_PATH (branch: $BRANCH)"
